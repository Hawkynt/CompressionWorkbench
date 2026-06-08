#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.Ghost;

/// <summary>
/// Reads Symantec / Norton Ghost backup images (<c>.gho</c> primary,
/// <c>.ghs</c> spanned-segment continuation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> This reader targets the Ghost 11.x / 12.x record container
/// (FE EF file magic, 10-byte record headers with magic 0x012F18D8,
/// 32 KB compressed blocks). The framing was reverse-engineered from
/// Norton Ghost 11.5.1 binaries and ported from the MIT-licensed
/// <c>nyarime/gho</c> Go implementation
/// (<see href="https://github.com/nyarime/gho"/>).
/// </para>
/// <para>
/// <b>Out of scope.</b> The legacy DOS-era Ghost 4-7 framing is
/// <em>different</em> from the Ghost 11.x record container — Ghost 4-7
/// images do not start with FE EF and do not use the 0x012F18D8 record
/// magic. When the reader detects a Ghost 4-7-shaped header it
/// version-gates: <see cref="GenerationHint"/> is set to
/// <see cref="GhostGenerationHint.PossiblyLegacy4To7"/>, the entries
/// list contains the diagnostic metadata + the raw container bytes,
/// and the parsing path does not attempt LZ77 decompression. Recovery
/// for legacy images requires Symantec Ghost Explorer.
/// </para>
/// <para>
/// <b>Compression modes.</b>
/// <see cref="GhostConstants.CompressionNone"/> (Z0/Z1 stored mode)
/// surfaces raw partition bytes; <see cref="GhostConstants.CompressionFast"/>
/// (Z1) uses <see cref="GhostFastLz"/>; <see cref="GhostConstants.CompressionHigh3"/>
/// through <see cref="GhostConstants.CompressionHigh9"/> use
/// <see cref="GhostZlib"/>.
/// </para>
/// <para>
/// <b>Encryption.</b> When byte 12, bit 1 of the file header is set the
/// image is CRC-16-cipher encrypted (see
/// <see cref="GhostCrc16Cipher"/>). The constructor accepts a password;
/// supplying the wrong password decompresses to garbage rather than
/// throwing — the underlying cipher has no integrity check.
/// </para>
/// </remarks>
public sealed class GhostReader : IDisposable {

  private readonly byte[] _data;
  private readonly string? _password;
  private readonly List<GhostEntry> _entries = [];

  private GhostFileHeader? _header;
  private readonly List<GhostPartitionInfo> _partitions = [];
  private GhostRecord? _endRecord;

  /// <summary>The entries exposed to the registry (metadata + partitions, or fallback raw image bytes).</summary>
  public IReadOnlyList<GhostEntry> Entries => this._entries;

  /// <summary>First 16 bytes of the file (used for diagnostics).</summary>
  public byte[] LeadingBytes { get; private set; } = [];

  /// <summary>Best-effort generation classification (set after Parse).</summary>
  public GhostGenerationHint GenerationHint { get; private set; } = GhostGenerationHint.Unknown;

  /// <summary>True when this stream was opened as a <c>.ghs</c> continuation segment.</summary>
  public bool LikelySpannedSegment { get; }

  /// <summary>True when the file header indicates encryption (byte 12, bit 1).</summary>
  public bool IsEncrypted => this._header?.IsEncrypted ?? false;

  /// <summary>True when the modern record container parsed cleanly.</summary>
  public bool IsModernContainerParsed => this._header != null && this._partitions.Count >= 0 && this.GenerationHint == GhostGenerationHint.Modern11Plus;

  public GhostReader(Stream stream, bool isSpannedSegment = false, string? password = null) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.LikelySpannedSegment = isSpannedSegment;
    this._password = password;
    this.Parse();
  }

  private void Parse() {
    if (this._data.Length < 16)
      throw new InvalidDataException("Ghost: file too small to be a Norton Ghost image (need at least 16 bytes).");

    this.LeadingBytes = this._data.AsSpan(0, Math.Min(16, this._data.Length)).ToArray();

    // Try modern parse first; fall back to diagnostic-only entries on failure.
    if (this.TryParseModernContainer())
      this.GenerationHint = GhostGenerationHint.Modern11Plus;
    else
      this.GenerationHint = ClassifyLeadingBytes(this.LeadingBytes);

    var meta = this.BuildMetadata();
    this._entries.Add(new GhostEntry { Name = "metadata.ini", Size = meta.Length, Data = meta });

    if (this.GenerationHint == GhostGenerationHint.Modern11Plus) {
      // Surface MBR + decompressed partitions.
      this.MaterialiseEntries();
    } else {
      // Stage-0 fallback: surface only the raw container bytes.
      var rawName = this.LikelySpannedSegment ? "ghost-image.ghs.bin" : "ghost-image.gho.bin";
      this._entries.Add(new GhostEntry { Name = rawName, Size = this._data.Length, Data = this._data });
    }
  }

  // ── Modern (Ghost 11.x / 12.x) record-container parse ──────────────

  private bool TryParseModernContainer() {
    if (this._data.Length < GhostConstants.HeaderSize) return false;
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan(0, 2));
    if (magic != GhostConstants.FileMagic) return false;

    try {
      var hdr = new GhostFileHeader {
        Magic = magic,
        FileType = this._data[2],
        Compression = this._data[3],
        Id = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(4, 4)),
        Raw = this._data.AsSpan(0, GhostConstants.HeaderSize).ToArray()
      };

      // Validate the compression byte — unknown values would either silently
      // decompress garbage or fail later. Refuse early so callers see a
      // clean error instead.
      if (hdr.Compression > GhostConstants.CompressionHigh9) return false;

      this._header = hdr;

      var offset = (long)GhostConstants.HeaderSize;
      while (true) {
        var recOff = FindNextRecord(this._data, offset);
        if (recOff < 0) break;
        offset = recOff;

        var rec = ParseRecord(this._data, offset);
        if (rec == null) break;

        switch (rec.TypeCode) {
          case GhostConstants.RecordTypeTrack0: {
            // We don't materialise track-0 body here — MaterialiseEntries pulls it later.
            offset += GhostConstants.RecordHeaderSize + rec.BodyLen;
            break;
          }
          case GhostConstants.RecordTypePartition: {
            var bodyOff = offset + GhostConstants.RecordHeaderSize;
            if (bodyOff + rec.BodyLen > this._data.Length) return false;
            var descBody = new byte[20];
            this._data.AsSpan((int)bodyOff, Math.Min(rec.BodyLen, descBody.Length)).CopyTo(descBody);
            offset = bodyOff + rec.BodyLen;

            if (offset + GhostConstants.HeaderSize > this._data.Length) return false;
            var feefRaw = this._data.AsSpan((int)offset, GhostConstants.HeaderSize);
            var feefMagic = BinaryPrimitives.ReadUInt16LittleEndian(feefRaw[..2]);
            if (feefMagic != GhostConstants.FileMagic) return false;
            var feef = new GhostPartitionHeader {
              Magic = feefMagic,
              SubType = feefRaw[2],
              Compression = feefRaw[3],
              Id = BinaryPrimitives.ReadUInt32LittleEndian(feefRaw.Slice(4, 4)),
              Raw = feefRaw.ToArray()
            };
            offset += GhostConstants.HeaderSize;
            var dataStart = offset;

            var nextRec = FindNextRecord(this._data, offset);
            if (nextRec < 0) nextRec = this._data.Length;
            var pInfo = new GhostPartitionInfo { Descriptor = rec, Header = feef, DescBody = descBody };
            pInfo.Spans.Add(new GhostSpan(dataStart, nextRec));
            this._partitions.Add(pInfo);
            offset = nextRec;
            break;
          }
          case GhostConstants.RecordTypeContinuation: {
            offset += GhostConstants.RecordHeaderSize + rec.BodyLen;
            // The continuation body is followed by an optional FEEF header.
            if (offset + 2 <= this._data.Length
                && BinaryPrimitives.ReadUInt16LittleEndian(this._data.AsSpan((int)offset, 2)) == GhostConstants.FileMagic
                && offset + GhostConstants.HeaderSize <= this._data.Length)
              offset += GhostConstants.HeaderSize;

            var dataStart = offset;
            var nextRec = FindNextRecord(this._data, offset);
            if (nextRec < 0) nextRec = this._data.Length;
            if (this._partitions.Count > 0)
              this._partitions[^1].Spans.Add(new GhostSpan(dataStart, nextRec));
            offset = nextRec;
            break;
          }
          case GhostConstants.RecordTypeEnd:
            this._endRecord = rec;
            return true;
          default:
            offset += GhostConstants.RecordHeaderSize + rec.BodyLen;
            break;
        }

        if (offset >= this._data.Length) break;
      }

      // Reached EOF without an explicit End record — accept anyway if we
      // found at least one partition, since some Ghost-written .gho files
      // are truncated at the last block boundary.
      return this._partitions.Count > 0;
    } catch {
      return false;
    }
  }

  private static GhostRecord? ParseRecord(byte[] data, long offset) {
    if (offset < 0 || offset + GhostConstants.RecordHeaderSize > data.Length) return null;
    var s = data.AsSpan((int)offset, GhostConstants.RecordHeaderSize);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(4, 4));
    if (magic != GhostConstants.RecordMagic) return null;
    return new GhostRecord {
      Type = BinaryPrimitives.ReadUInt32LittleEndian(s[..4]),
      Magic = magic,
      BodyLen = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8, 2)),
      Offset = offset
    };
  }

  private static long FindNextRecord(byte[] data, long startOff) {
    // Scan forward looking for the 0x012F18D8 record magic followed by a
    // known type code. Mirrors the nyarime/gho approach — this is needed
    // because compressed-block payloads contain no length-of-payload field
    // that the reader can use to jump to the next record directly.
    var off = (int)startOff;
    while (off + GhostConstants.RecordHeaderSize <= data.Length) {
      var magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4, 4));
      if (magic == GhostConstants.RecordMagic) {
        var recType = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off, 2));
        if (IsKnownRecordType(recType))
          return off;
      }
      off++;
    }
    return -1;
  }

  private static bool IsKnownRecordType(ushort t) => t switch {
    GhostConstants.RecordTypeTrack0 or GhostConstants.RecordTypePartition
      or GhostConstants.RecordTypeContinuation or GhostConstants.RecordTypeEnd => true,
    _ => false
  };

  // ── Entry materialisation ─────────────────────────────────────────

  private void MaterialiseEntries() {
    // Track 0 (MBR).
    var track0 = this.ExtractTrack0Body();
    if (track0.Length > 0)
      this._entries.Add(new GhostEntry { Name = "track0.bin", Size = track0.Length, Data = track0 });

    // Partitions.
    for (var i = 0; i < this._partitions.Count; i++) {
      try {
        var raw = this.DecompressPartition(i);
        this._entries.Add(new GhostEntry {
          Name = $"partition{i + 1}.bin",
          Size = raw.Length,
          Data = raw
        });
      } catch (Exception ex) {
        // Don't let one bad partition kill the listing — surface a diagnostic.
        var note = Encoding.UTF8.GetBytes($"# partition{i + 1} decompression failed: {ex.Message}\n");
        this._entries.Add(new GhostEntry { Name = $"partition{i + 1}.error.txt", Size = note.Length, Data = note });
      }
    }
  }

  private byte[] ExtractTrack0Body() {
    foreach (var (offset, rec) in this.WalkRecords()) {
      if (rec.TypeCode != GhostConstants.RecordTypeTrack0) continue;
      var bodyOff = (int)(offset + GhostConstants.RecordHeaderSize);
      var bodyEnd = bodyOff + rec.BodyLen;
      if (bodyEnd > this._data.Length || rec.BodyLen < 6) return [];
      // First 6 bytes are the Track-0 mini-header (unknown1, sectors, unknown2(uint32)).
      return this._data.AsSpan(bodyOff + 6, rec.BodyLen - 6).ToArray();
    }
    return [];
  }

  private IEnumerable<(long Offset, GhostRecord Rec)> WalkRecords() {
    var offset = (long)GhostConstants.HeaderSize;
    while (true) {
      var recOff = FindNextRecord(this._data, offset);
      if (recOff < 0) yield break;
      var rec = ParseRecord(this._data, recOff);
      if (rec == null) yield break;
      yield return (recOff, rec);
      if (rec.TypeCode == GhostConstants.RecordTypeEnd) yield break;
      offset = recOff + GhostConstants.RecordHeaderSize + rec.BodyLen;
      if (offset >= this._data.Length) yield break;
    }
  }

  private byte[] DecompressPartition(int partIdx) {
    if (partIdx < 0 || partIdx >= this._partitions.Count)
      throw new ArgumentOutOfRangeException(nameof(partIdx));
    if (this._header == null) throw new InvalidOperationException("Ghost: file header not parsed.");

    GhostCrc16Cipher? cipher = null;
    if (this._header.IsEncrypted) {
      if (string.IsNullOrEmpty(this._password))
        throw new InvalidDataException("Ghost: image is encrypted but no password was supplied.");
      cipher = new GhostCrc16Cipher(this._password);
    }

    var output = new List<byte>(GhostConstants.BlockSize * 16);
    var dst = new byte[GhostConstants.BlockSize + 1024];

    foreach (var span in this._partitions[partIdx].Spans) {
      var offset = span.DataStart;
      while (offset + 2 <= span.DataEnd) {
        var lenBuf = this._data.AsSpan((int)offset, 2);
        var storedLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
        if (storedLen == 0) break;
        var compLen = storedLen - 2;
        if (compLen <= 0 || compLen > GhostConstants.MaxStoredLen
            || offset + 2 + compLen > span.DataEnd)
          throw new InvalidDataException($"Ghost: invalid block stored_len={storedLen} at offset 0x{offset:X}.");

        var blockData = this._data.AsSpan((int)(offset + 2), compLen).ToArray();
        if (cipher != null) cipher.Decrypt(blockData);

        var n = this._header.Compression switch {
          GhostConstants.CompressionNone => CopyStored(blockData, compLen, dst),
          GhostConstants.CompressionFast => GhostFastLz.Decompress(blockData, compLen, dst),
          GhostConstants.CompressionHigh3 or GhostConstants.CompressionHigh4 or
            GhostConstants.CompressionHigh5 or GhostConstants.CompressionHigh6 or
            GhostConstants.CompressionHigh7 or GhostConstants.CompressionHigh8 or
            GhostConstants.CompressionHigh9 => GhostZlib.Decompress(blockData, compLen, dst),
          _ => throw new InvalidDataException($"Ghost: unsupported compression byte {this._header.Compression}.")
        };

        for (var k = 0; k < n; k++) output.Add(dst[k]);
        offset += 2 + compLen;
      }
    }

    return output.ToArray();
  }

  private static int CopyStored(byte[] blockData, int compLen, byte[] dst) {
    var n = Math.Min(compLen, dst.Length);
    Array.Copy(blockData, 0, dst, 0, n);
    return n;
  }

  // ── Metadata / classification ──────────────────────────────────────

  private static GhostGenerationHint ClassifyLeadingBytes(ReadOnlySpan<byte> leading) {
    if (leading.Length >= 2 && leading[0] == 0xFE && leading[1] == 0xEF)
      return GhostGenerationHint.PossiblyLegacy4To7;
    if (leading.Length >= 4 && leading[0] == 'S' && leading[1] == 'Y' && leading[2] == 'M' && leading[3] == 'C')
      return GhostGenerationHint.PossiblyModern8Plus;
    return GhostGenerationHint.Unknown;
  }

  private byte[] BuildMetadata() {
    var b = new StringBuilder();
    b.Append("format=Symantec / Norton Ghost backup image\n");
    b.Append(CultureInfo.InvariantCulture, $"role={(this.LikelySpannedSegment ? "spanned-segment(.ghs)" : "primary-image(.gho)")}\n");
    b.Append(CultureInfo.InvariantCulture, $"generation_hint={this.GenerationHint}\n");
    b.Append("leading_bytes_hex=");
    foreach (var x in this.LeadingBytes) b.Append(CultureInfo.InvariantCulture, $"{x:X2}");
    b.Append('\n');
    b.Append(CultureInfo.InvariantCulture, $"image_size={this._data.Length}\n");

    if (this.GenerationHint == GhostGenerationHint.Modern11Plus && this._header != null) {
      b.Append("parse_status=ok\n");
      b.Append("stage=2\n");
      b.Append(CultureInfo.InvariantCulture, $"file_type=0x{this._header.FileType:X2}\n");
      b.Append(CultureInfo.InvariantCulture, $"compression={this._header.Compression}\n");
      b.Append(CultureInfo.InvariantCulture, $"image_id=0x{this._header.Id:X8}\n");
      b.Append(CultureInfo.InvariantCulture, $"is_encrypted={this._header.IsEncrypted}\n");
      b.Append(CultureInfo.InvariantCulture, $"partition_count={this._partitions.Count}\n");
      b.Append(CultureInfo.InvariantCulture, $"end_record_seen={this._endRecord != null}\n");
      b.Append("note=Ghost 11.x/12.x record container parsed via reverse-engineered Fast LZ + zlib codec.\n");
    } else {
      b.Append("parse_status=detection-only\n");
      b.Append("stage=0\n");
      b.Append("promotion_blocked_reason=record-container shape mismatch (legacy DOS-era Ghost 4-7 or unknown variant) — modern parser supports only Ghost 11.x/12.x FE EF + 0x012F18D8 record framing.\n");
      b.Append("note=Stage 0 fallback — extraction surfaces the raw container only. Recovery path: use Symantec Ghost Explorer (ghostexp.exe) or Ghost32.exe.\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  public byte[] Extract(GhostEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}

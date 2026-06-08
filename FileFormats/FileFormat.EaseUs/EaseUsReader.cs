#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileFormat.EaseUs;

/// <summary>
/// Read-only chunk-stream reader for EaseUS Todo Backup container files
/// (<c>.pbd</c> — Personal Backup Disk / EaseUS Backup Disk).
///
/// <para>
/// EaseUS Todo Backup is a proprietary closed-source backup product from
/// CHENGDU Yiwo Tech Development (Sichuan, China). The <c>.pbd</c>
/// container holds a full / incremental / differential image of one or
/// more source volumes plus the backup-job metadata (chain id, snapshot
/// id, parent-chain references, optional AES-256 key envelope). The
/// vendor has never published the on-disk format; what follows is the
/// best-effort reconstruction from community reverse-engineering
/// (R-Studio custom file-type definition on the R-TT data-recovery
/// forum, hex-editor walkthroughs on tenforums / xyplorer / Rune-Server,
/// the file-extensions.com / filext.com / fileinfo.com aggregator
/// pages, and the EaseUS support documentation).
/// </para>
///
/// <para>
/// <b>Chunk-stream R/O promotion.</b> The Rune-Server thread 694189
/// pinned two stable observations: binwalk locates zlib substream
/// headers at predictable positions inside every observed .pbd (first
/// two metadata streams at offsets 0x98 and 0x10F, payload streams from
/// 0xB28 onward), and the offsets are stable across v1 / v2 backup
/// pairs for the metadata streams while the payload streams shift by
/// exactly the payload-delta byte count. That makes the zlib substreams
/// the strongest universally observable landmark inside the body. This
/// reader now walks every <c>0x78 {0x01|0x9C|0xDA}</c> candidate header
/// in the body and runs a trial inflate (the Adler-32 trailer plus the
/// DEFLATE terminal-block flag give a strong false-positive rejection);
/// each confirmed substream is surfaced as a forensic entry with the
/// offset, compressed-length, and decompressed-length stamped into the
/// entry name so downstream consumers can correlate hits across
/// chain-mate files without parsing metadata.ini.
/// </para>
///
/// <para>
/// What this reader parses from the header (R/O, no decryption, no
/// decompression):
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Magic</b> at offset 0: ASCII <c>"IMGF"</c> = 49 4D 47 46
///     (the universal "Image File" marker found in ~85% of real-world
///     .pbd files; R-Studio uses the 12-byte signature
///     <c>49 4D 47 46 2C 05 00 00 00 00 02 00</c> for forensic carving).
///     The byte-reversed variant <c>"FIMG"</c> = 46 49 4D 47 (~15% of
///     files; OEM / older partner builds) is also accepted.
///   </description></item>
///   <item><description>
///     <b>Header word</b> (bytes 4..7, LE u32): observed value
///     <c>0x0000052C</c> = 1324. Interpretation per community RE:
///     header-table or first-zlib-stream offset. Captured as-is for
///     diagnostics; not load-bearing.
///   </description></item>
///   <item><description>
///     <b>Version word</b> (bytes 8..11, LE u32): observed value
///     <c>0x00020000</c>. Captured as a 16/16 split — top half
///     (<c>0x0002</c>) consistently reads as the major version, bottom
///     half (<c>0x0000</c>) as a minor/reserved field.
///   </description></item>
///   <item><description>
///     <b>Embedded source path</b>: a UTF-16LE wide-char run starting
///     after the 12-byte header (e.g. <c>"G:\backup\msi laptop\..."</c>).
///     The reader scans the first 4 KiB after the header for the longest
///     printable wide-char run (>= 6 chars) and surfaces it as the
///     original backup target.
///   </description></item>
///   <item><description>
///     <b>Zlib substreams</b>: the file body is a sequence of zlib
///     deflate streams (binwalk routinely finds the 0x78 0x9C / 0x78 0xDA
///     / 0x78 0x01 markers throughout). The reader walks every
///     <c>0x78 {0x01|0x9C|0xDA}</c> candidate header and runs a trial
///     inflate via <see cref="System.IO.Compression.ZLibStream"/>; the
///     Adler-32 trailer plus the DEFLATE terminal-block flag reject
///     coincidental matches. Each confirmed substream is recorded with
///     its offset, compressed length, decompressed length, and the
///     inflated payload (capped per chunk to bound memory) and surfaced
///     as a per-chunk forensic entry. For password-protected backups the
///     AES-256 envelope wraps every payload-region block, so chunks
///     past the metadata streams (offsets 0x98 and 0x10F) will fail
///     header-validation cleanly and surface as
///     <see cref="EaseUsChunkInflateStatus.FailedHeaderInvalid"/>
///     entries — the chunk-stream inventory remains useful as a forensic
///     landmark even when no body byte is recoverable.
///   </description></item>
///   <item><description>
///     <b>Trailer</b>: real-world .pbd files terminate with another
///     <c>"IMGF"</c> marker followed by variable bytes and a run of
///     <c>0xFF</c> padding to the file's nominal end. The reader walks
///     back from EOF, counts trailing <c>0xFF</c> padding bytes, and
///     captures whether a second IMGF marker is present.
///   </description></item>
/// </list>
///
/// <para>
/// What this reader still does NOT do (these remain blocked by the
/// vendor's closed format):
/// </para>
/// <list type="bullet">
///   <item><description>
///     No decryption — when AES-256 key envelope is present the key
///     derivation and the chunk-cipher framing are vendor-private.
///     Encrypted backups still surface a chunk-stream inventory but the
///     payload-region trial inflate cleanly fails.
///   </description></item>
///   <item><description>
///     No sector reconstruction / volume-image walk — EaseUS uses a
///     proprietary block-allocation table to map logical sectors back
///     to compressed chunks, and that table format has never been
///     published. Even with every zlib substream successfully inflated
///     we have no offset-to-LBA mapping, so we cannot assemble a
///     restorable disk image. Use the EaseUS engine's mount tool for
///     that.
///   </description></item>
///   <item><description>
///     No backup-chain navigation — the parent-snapshot pointers and
///     the per-file-version index live behind the vendor's job-metadata
///     header.
///   </description></item>
/// </list>
///
/// <para>
/// Sources (last verified June 2026):
/// <c>forum.r-tt.com/viewtopic.php?t=11516</c> (R-Studio custom file
/// type — 12-byte IMGF signature),
/// <c>tenforums.com/software-apps/116663-cant-open-pbd-files-easeus-todo-backup.html</c>
/// (hex-editor walk; UTF-16LE source path; 0xFF tail padding),
/// <c>xyplorer.com/xyfc/viewtopic.php?t=28249</c> (FIMG variant — ~15%
/// of files), <c>file-extensions.com/docs/pbd</c>, <c>filext.com/file-extension/PBD</c>,
/// <c>fileinfo.com/extension/pbd</c>, EaseUS support
/// (<c>easeus.com/support/todo-backup/index.html</c> — official mode
/// + filesystem-support documentation), Rune-Server thread 694189
/// (binwalk zlib-substream offsets).
/// </para>
/// </summary>
public sealed class EaseUsReader : IDisposable {

  /// <summary>EaseUS PBD primary magic: ASCII "IMGF" = 0x49 0x4D 0x47 0x46.</summary>
  public static readonly byte[] ImgfMagic = "IMGF"u8.ToArray();

  /// <summary>EaseUS PBD byte-reversed variant magic: ASCII "FIMG" = 0x46 0x49 0x4D 0x47.</summary>
  public static readonly byte[] FimgMagic = "FIMG"u8.ToArray();

  /// <summary>R-Studio 12-byte forensic-carving signature: "IMGF" + 0x2C 0x05 0x00 0x00 0x00 0x00 0x02 0x00.</summary>
  public static readonly byte[] ImgfExtendedSignature = [0x49, 0x4D, 0x47, 0x46, 0x2C, 0x05, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00];

  /// <summary>Header observed at offset 0; minimum bytes the parser needs in front of it.</summary>
  public const int HeaderSize = 12;

  /// <summary>Window after the header in which to scan for the embedded UTF-16LE source-path string.</summary>
  public const int PathScanWindow = 4096;

  /// <summary>Trailer window scanned back from EOF for the closing IMGF marker + 0xFF padding.</summary>
  public const int TrailerScanWindow = 4096;

  /// <summary>Maximum number of inner zlib substream offsets to record for the metadata.ini summary line.</summary>
  public const int MaxZlibOffsetsRecorded = 3;

  /// <summary>
  /// Maximum number of confirmed zlib chunks surfaced as individual forensic
  /// entries. Beyond this cap the chunk inventory in metadata.ini still
  /// reports the full count, but only the first N chunks are exposed as
  /// extractable entries to keep the archive surface bounded for files with
  /// hundreds of chunks (real .pbd backups can easily contain 100+).
  /// </summary>
  public const int MaxChunkEntriesSurfaced = 32;

  /// <summary>
  /// Per-chunk decompressed payload retention cap (passed to the scanner).
  /// Header/metadata chunks at 0x98 / 0x10F are typically &lt; 1 KiB so they
  /// always fit; payload chunks past 0xB28 are usually larger and surface
  /// with <see cref="EaseUsChunkInflateStatus.InflatedOverCap"/> when the
  /// trial inflate succeeds but the payload is dropped on the floor.
  /// </summary>
  public const int MaxRetainedChunkPayloadBytes = EaseUsZlibScanner.DefaultMaxRetainedPayloadBytes;

  private readonly byte[] _data;
  private readonly List<EaseUsEntry> _entries = [];

  public IReadOnlyList<EaseUsEntry> Entries => _entries;

  /// <summary>True once the header magic has been recognised.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>"IMGF" (primary) or "FIMG" (byte-reversed variant).</summary>
  public string MagicVariant { get; private set; } = "";

  /// <summary>True if the file matches the R-Studio 12-byte forensic-carving signature exactly.</summary>
  public bool ExtendedSignatureMatch { get; private set; }

  /// <summary>Raw LE u32 read at bytes 4..7 (community RE calls this the header-table or first-stream offset).</summary>
  public uint HeaderWord { get; private set; }

  /// <summary>Raw LE u32 read at bytes 8..11 (community RE calls this the version word; major in the high half).</summary>
  public uint VersionWord { get; private set; }

  /// <summary>Likely "major version" — high 16 bits of <see cref="VersionWord"/>.</summary>
  public ushort VersionMajor { get; private set; }

  /// <summary>Likely "minor/reserved" — low 16 bits of <see cref="VersionWord"/>.</summary>
  public ushort VersionMinor { get; private set; }

  /// <summary>Longest printable UTF-16LE wide-char run found in the first <see cref="PathScanWindow"/> bytes after the header (empty string if none).</summary>
  public string EmbeddedSourcePath { get; private set; } = "";

  /// <summary>Byte offset (relative to file start) where <see cref="EmbeddedSourcePath"/> starts, or -1 if none.</summary>
  public long EmbeddedSourcePathOffset { get; private set; } = -1;

  /// <summary>
  /// Number of <c>0x78 {0x01|0x9C|0xDA}</c> candidate zlib substream headers
  /// located by linear scan — includes both confirmed and rejected
  /// candidates. Use <see cref="ConfirmedZlibChunkCount"/> for the
  /// trial-inflate confirmed count.
  /// </summary>
  public int ZlibStreamCount { get; private set; }

  /// <summary>Up to <see cref="MaxZlibOffsetsRecorded"/> first zlib-stream offsets, for the metadata.ini summary line.</summary>
  public IReadOnlyList<long> FirstZlibOffsets { get; private set; } = [];

  /// <summary>
  /// Full chunk inventory from the trial-inflate scan: every candidate
  /// zlib substream header in scan order with its offset, FCH byte,
  /// compressed length (when inflate succeeded), decompressed length,
  /// inflate status, and (within the per-chunk retention cap) the
  /// inflated payload bytes.
  /// </summary>
  public IReadOnlyList<EaseUsZlibChunk> Chunks { get; private set; } = [];

  /// <summary>Number of substreams that inflated end-to-end (<see cref="EaseUsChunkInflateStatus.Inflated"/> or <see cref="EaseUsChunkInflateStatus.InflatedOverCap"/>).</summary>
  public int ConfirmedZlibChunkCount { get; private set; }

  /// <summary>Total compressed bytes consumed across all confirmed chunks.</summary>
  public long TotalCompressedChunkBytes { get; private set; }

  /// <summary>Total decompressed bytes produced across all confirmed chunks.</summary>
  public long TotalDecompressedChunkBytes { get; private set; }

  /// <summary>True if a second "IMGF" marker is present in the last <see cref="TrailerScanWindow"/> bytes.</summary>
  public bool TrailerImgfPresent { get; private set; }

  /// <summary>Number of trailing 0xFF padding bytes at EOF.</summary>
  public int TrailingFfPadding { get; private set; }

  public EaseUsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < HeaderSize)
      throw new InvalidDataException(
        $"EaseUS PBD: file too small ({_data.Length} bytes) for the 12-byte IMGF header.");

    // 1) Magic at offset 0 — accept IMGF (primary) or FIMG (byte-reversed variant).
    var head4 = _data.AsSpan(0, 4);
    if (head4.SequenceEqual(ImgfMagic))
      this.MagicVariant = "IMGF";
    else if (head4.SequenceEqual(FimgMagic))
      this.MagicVariant = "FIMG";
    else
      throw new InvalidDataException(
        "EaseUS PBD: missing 'IMGF' / 'FIMG' magic at offset 0 (got " +
        $"0x{head4[0]:X2} 0x{head4[1]:X2} 0x{head4[2]:X2} 0x{head4[3]:X2}).");

    this.ValidHeader = true;
    this.ExtendedSignatureMatch =
      _data.Length >= ImgfExtendedSignature.Length
      && _data.AsSpan(0, ImgfExtendedSignature.Length).SequenceEqual(ImgfExtendedSignature);

    // 2) Header + version words.
    this.HeaderWord = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(4, 4));
    this.VersionWord = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(8, 4));
    this.VersionMajor = (ushort)(this.VersionWord >> 16);
    this.VersionMinor = (ushort)(this.VersionWord & 0xFFFF);

    // 3) Embedded UTF-16LE source path scan (best-effort).
    ScanEmbeddedSourcePath();

    // 4) Zlib-substream scan (count + first N offsets).
    ScanZlibSubstreams();

    // 5) Trailer scan — IMGF marker + 0xFF padding count.
    ScanTrailer();

    // 6) Surface synthetic metadata.ini + per-chunk forensic entries + raw container blob.
    var meta = BuildMetadata();
    _entries.Add(new EaseUsEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
    SurfaceChunkEntries();
    _entries.Add(new EaseUsEntry { Name = "easeus-backup.pbd", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  /// <summary>
  /// Surfaces up to <see cref="MaxChunkEntriesSurfaced"/> per-chunk entries
  /// from the trial-inflate scan: one for the raw zlib substream bytes
  /// (named <c>chunks/chunk_NNNN_off{offset}_clen{compressed}.zlib</c>),
  /// and a second one for the inflated payload when retention succeeded
  /// (<c>chunks/chunk_NNNN_off{offset}_dlen{decompressed}.bin</c>). The
  /// naming convention stamps the offset and length into the entry name
  /// so downstream consumers (forensic tools that diff chunks across
  /// chain-mate .pbd files) can correlate hits without parsing
  /// metadata.ini. Rejected candidates are listed in metadata.ini but
  /// not surfaced as entries.
  /// </summary>
  private void SurfaceChunkEntries() {
    if (this.Chunks.Count == 0) return;

    var surfaced = 0;
    for (var i = 0; i < this.Chunks.Count && surfaced < MaxChunkEntriesSurfaced; i++) {
      var c = this.Chunks[i];
      if (c.InflateStatus is not (EaseUsChunkInflateStatus.Inflated or EaseUsChunkInflateStatus.InflatedOverCap))
        continue;

      var idx = surfaced;
      var rawName = string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"chunks/chunk_{idx:D4}_off{c.Offset}_clen{c.CompressedLength}.zlib");

      var raw = new byte[c.CompressedLength];
      Array.Copy(_data, c.Offset, raw, 0, c.CompressedLength);
      _entries.Add(new EaseUsEntry {
        Name = rawName,
        Size = raw.Length,
        IsDirectory = false,
        Offset = c.Offset,
        Data = raw,
      });

      if (c.PayloadRetained && c.Payload.Length > 0) {
        var inflatedName = string.Create(
          System.Globalization.CultureInfo.InvariantCulture,
          $"chunks/chunk_{idx:D4}_off{c.Offset}_dlen{c.DecompressedLength}.bin");
        _entries.Add(new EaseUsEntry {
          Name = inflatedName,
          Size = c.Payload.Length,
          IsDirectory = false,
          Offset = c.Offset,
          Data = c.Payload,
        });
      }

      surfaced++;
    }
  }

  /// <summary>
  /// Scans the first <see cref="PathScanWindow"/> bytes after the header for a UTF-16LE
  /// run of printable ASCII characters (each char in [0x20..0x7E] followed by a 0x00 byte).
  /// Picks the longest run >= 6 wide-chars (12 raw bytes). Matches the field that
  /// community RE consistently identifies as the original backup-target path.
  /// </summary>
  private void ScanEmbeddedSourcePath() {
    var start = HeaderSize;
    var end = Math.Min(_data.Length - 1, start + PathScanWindow);
    if (end <= start + 12) return;

    var bestOffset = -1;
    var bestLen = 0;
    var i = start;
    while (i + 12 <= end) {
      // Each wide-char = printable ASCII + 0x00 hi byte.
      if (IsPrintableAsciiWide(_data, i)) {
        var runStart = i;
        var runLen = 0;
        while (i + 2 <= end && IsPrintableAsciiWide(_data, i)) {
          i += 2;
          runLen++;
        }
        if (runLen >= 6 && runLen > bestLen) {
          bestLen = runLen;
          bestOffset = runStart;
        }
      } else {
        i++;
      }
    }

    if (bestOffset < 0) return;

    this.EmbeddedSourcePathOffset = bestOffset;
    var sb = new StringBuilder(bestLen);
    for (var k = 0; k < bestLen; k++)
      sb.Append((char)_data[bestOffset + k * 2]);
    this.EmbeddedSourcePath = sb.ToString();
  }

  private static bool IsPrintableAsciiWide(byte[] buf, int idx) {
    if (idx + 2 > buf.Length) return false;
    var lo = buf[idx];
    var hi = buf[idx + 1];
    return hi == 0x00 && lo >= 0x20 && lo <= 0x7E;
  }

  /// <summary>
  /// Trial-inflate scan: locates every <c>0x78 {0x01|0x9C|0xDA}</c>
  /// candidate header from <see cref="HeaderSize"/> onward and runs a
  /// real <see cref="System.IO.Compression.ZLibStream"/> inflate against
  /// each one. Confirmed substreams populate <see cref="Chunks"/> with
  /// the compressed and decompressed sizes and (within the per-chunk
  /// retention cap) the inflated payload; coincidental byte-pattern hits
  /// fail through one of the <see cref="EaseUsChunkInflateStatus"/>
  /// failure codes and remain in the inventory for forensic accounting.
  /// <see cref="ZlibStreamCount"/> and <see cref="FirstZlibOffsets"/> stay
  /// populated for the metadata.ini summary lines downstream consumers
  /// already pin.
  /// </summary>
  private void ScanZlibSubstreams() {
    var chunks = EaseUsZlibScanner.Scan(_data, HeaderSize, MaxRetainedChunkPayloadBytes);
    this.Chunks = chunks;
    this.ZlibStreamCount = chunks.Count;

    var firstOffsets = new List<long>(Math.Min(chunks.Count, MaxZlibOffsetsRecorded));
    var confirmed = 0;
    long totalCompressed = 0;
    long totalDecompressed = 0;
    foreach (var c in chunks) {
      if (firstOffsets.Count < MaxZlibOffsetsRecorded)
        firstOffsets.Add(c.Offset);
      if (c.InflateStatus is EaseUsChunkInflateStatus.Inflated or EaseUsChunkInflateStatus.InflatedOverCap) {
        confirmed++;
        totalCompressed += c.CompressedLength;
        totalDecompressed += c.DecompressedLength;
      }
    }
    this.FirstZlibOffsets = firstOffsets;
    this.ConfirmedZlibChunkCount = confirmed;
    this.TotalCompressedChunkBytes = totalCompressed;
    this.TotalDecompressedChunkBytes = totalDecompressed;
  }

  /// <summary>
  /// Trailer scan: count the run of 0xFF bytes at EOF, then look for a second "IMGF"
  /// marker in the last <see cref="TrailerScanWindow"/> bytes (immediately ahead of the
  /// 0xFF padding). Real-world .pbd files reliably show this layout.
  /// </summary>
  private void ScanTrailer() {
    // Count trailing 0xFF padding bytes.
    var pad = 0;
    for (var i = _data.Length - 1; i >= 0 && _data[i] == 0xFF; i--) {
      pad++;
      if (pad >= int.MaxValue) break;
    }
    this.TrailingFfPadding = pad;

    // Trailer-IMGF marker scan in the last TrailerScanWindow bytes (excluding pure padding).
    var trailerEnd = _data.Length - pad;
    var trailerStart = Math.Max(HeaderSize, trailerEnd - TrailerScanWindow);
    if (trailerEnd - trailerStart < ImgfMagic.Length) return;

    var window = _data.AsSpan(trailerStart, trailerEnd - trailerStart);
    this.TrailerImgfPresent = window.IndexOf(ImgfMagic.AsSpan()) >= 0
                              || window.IndexOf(FimgMagic.AsSpan()) >= 0;
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    var promoted = this.ConfirmedZlibChunkCount > 0;
    bldr.Append(promoted ? "parse_status=chunk-stream\n" : "parse_status=header-metadata\n");
    bldr.Append(promoted ? "stage=ro-chunk-stream\n" : "stage=ro-metadata\n");
    bldr.Append("format=EaseUS Todo Backup container\n");
    bldr.Append("treatment=R/O chunk-stream — IMGF header + UTF-16LE source path + per-zlib-substream trial inflate (sector reconstruction stays Stage-0 due to AES-256 key envelope and proprietary block-allocation table)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_variant={this.MagicVariant}\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append(CultureInfo.InvariantCulture, $"extended_signature_match={(this.ExtendedSignatureMatch ? "true" : "false")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"header_word=0x{this.HeaderWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_word=0x{this.VersionWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_major={this.VersionMajor}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_minor={this.VersionMinor}\n");
    if (this.EmbeddedSourcePathOffset >= 0) {
      bldr.Append(CultureInfo.InvariantCulture, $"source_path={this.EmbeddedSourcePath}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"source_path_offset={this.EmbeddedSourcePathOffset}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"source_path_length_chars={this.EmbeddedSourcePath.Length}\n");
    } else {
      bldr.Append("source_path=(none detected)\n");
    }
    bldr.Append(CultureInfo.InvariantCulture, $"zlib_substream_count={this.ZlibStreamCount}\n");
    if (this.FirstZlibOffsets.Count > 0) {
      var joined = string.Join(",", this.FirstZlibOffsets);
      bldr.Append(CultureInfo.InvariantCulture, $"zlib_first_offsets={joined}\n");
    }
    // Trial-inflate chunk inventory: how many candidates inflated, total
    // compressed/decompressed bytes, plus a per-chunk table of the first
    // 16 entries (offset / FCH byte / status / sizes). Downstream forensic
    // tools diff this list across chain-mate .pbd files to identify which
    // metadata-bank chunks moved and which payload chunks shifted.
    bldr.Append(CultureInfo.InvariantCulture, $"zlib_confirmed_chunk_count={this.ConfirmedZlibChunkCount}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"zlib_total_compressed_bytes={this.TotalCompressedChunkBytes}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"zlib_total_decompressed_bytes={this.TotalDecompressedChunkBytes}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"zlib_chunk_retention_cap_bytes={MaxRetainedChunkPayloadBytes}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"zlib_chunk_entries_surfaced_cap={MaxChunkEntriesSurfaced}\n");
    var listed = Math.Min(this.Chunks.Count, 16);
    for (var i = 0; i < listed; i++) {
      var c = this.Chunks[i];
      bldr.Append(CultureInfo.InvariantCulture,
        $"zlib_chunk_{i:D2}_offset={c.Offset} fch=0x{c.FchByte:X2} status={c.InflateStatus} clen={c.CompressedLength} dlen={c.DecompressedLength}\n");
    }
    bldr.Append(CultureInfo.InvariantCulture, $"trailer_imgf_present={(this.TrailerImgfPresent ? "true" : "false")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_ff_padding={this.TrailingFfPadding}\n");
    bldr.Append("vendor=CHENGDU Yiwo Tech Development (EaseUS)\n");
    bldr.Append("product=EaseUS Todo Backup\n");
    bldr.Append("extension=.pbd\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");
    bldr.Append("note=R/O chunk-stream surfacing. The IMGF header, embedded UTF-16LE source path, ");
    bldr.Append("per-zlib-substream trial inflate (compressed + decompressed sizes; payload bytes within ");
    bldr.Append("the per-chunk retention cap), and trailer IMGF + 0xFF padding are surfaced from community ");
    bldr.Append("reverse-engineering (Rune-Server thread 694189 + R-Studio + tenforums + xyplorer). ");
    bldr.Append("Sector reconstruction (block-allocation table walk), AES-256 key envelope, parent-chain ");
    bldr.Append("navigation, and per-file index traversal still require the EaseUS engine itself; the vendor ");
    bldr.Append("has never published the spec and no open-source reader exists. Encrypted backups still ");
    bldr.Append("surface a chunk-stream inventory but the payload-region trial inflate fails through ");
    bldr.Append("FailedHeaderInvalid because every body block is wrapped by the AES envelope.\n");
    bldr.Append("upgrade_blockers=proprietary-block-tables,aes-key-envelope,vendor-only-engine,no-public-spec\n");
    bldr.Append("references=forum.r-tt.com/viewtopic.php?t=11516,");
    bldr.Append("tenforums.com/software-apps/116663-cant-open-pbd-files-easeus-todo-backup.html,");
    bldr.Append("xyplorer.com/xyfc/viewtopic.php?t=28249,");
    bldr.Append("file-extensions.com/docs/pbd,fileinfo.com/extension/pbd,");
    bldr.Append("easeus.com/support/todo-backup/index.html\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(EaseUsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}

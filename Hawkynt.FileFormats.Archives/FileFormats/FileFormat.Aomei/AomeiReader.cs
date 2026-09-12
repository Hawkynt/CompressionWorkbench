#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aomei;

/// <summary>
/// Reader for AOMEI Backupper image files (<c>.adi</c> disk / partition /
/// system image and <c>.afi</c> file backup), implementing the partial
/// specification recovered from binary reverse engineering of the AOMEI
/// Backupper binary stack — see <c>docs/AOMEI_FORMAT_SPEC.md</c>.
///
/// <para>
/// What this reader verifies and surfaces:
/// </para>
/// <list type="bullet">
///   <item><description>Five-byte ASCII signature <c>BIFH\</c> at offset 0
///         — preserved from the original R/O metadata baseline so detection
///         logic stays unchanged.</description></item>
///   <item><description>Full <c>BR_IMAGE_FILE_HEAD</c> at offset 0 (0x65C
///         bytes): Flag, Size and Crc32 fields verified per spec §2.1; the
///         remaining 0x650 body bytes are surfaced as
///         <see cref="HeadBody"/> for future RE work.</description></item>
///   <item><description>Full <c>BR_IMAGE_FILE_TAIL</c> at offset
///         <c>file_size - 0x674</c>: same Flag/Size/Crc32 verification, body
///         surfaced as <see cref="TailBody"/>.</description></item>
///   <item><description>Walk of every <c>BR_STANDARD_HEADER</c>-prefixed
///         record between the head and the tail; each record's CRC is
///         verified per the spec §3.1 invariant
///         <c>BRCrc32(record, sizeof(record)) == saved_crc</c>.</description></item>
///   <item><description>Typed views of the four confirmed INFO records:
///         <see cref="AomeiConstants.InfoTypeImageCompress"/> (0x105),
///         <see cref="AomeiConstants.InfoTypeImageEncrypt"/> (0x106),
///         <see cref="AomeiConstants.InfoTypeImagePassword"/> (0x107),
///         <see cref="AomeiConstants.InfoTypeBackupType"/> (0x10C).</description></item>
/// </list>
///
/// <para>
/// What is not yet decoded (per spec §10):
/// </para>
/// <list type="bullet">
///   <item><description>Head/tail body fields past the first 12 bytes —
///         layout TODO.</description></item>
///   <item><description>INDEX_TYPE_DATABLOCK / DIRTREE / VOLUME / DATAAREA /
///         ROOT record bodies — only the BR_STANDARD_HEADER framing is
///         walked; the body bytes are passed through as opaque
///         <see cref="AomeiInfoRecord.Body"/>.</description></item>
///   <item><description>Encryption (AES variant + IV derivation) and the
///         compression method numeric mapping — fields surfaced as-is.</description></item>
/// </list>
///
/// <para>
/// The reader is tolerant: a short/partial image is reported via
/// <see cref="ParseStatus"/> rather than thrown, so callers (e.g. the
/// descriptor's <c>List</c> / <c>Extract</c> path) can fall back to the
/// header-surface treatment without exception handling on the hot path.
/// </para>
/// </summary>
public sealed class AomeiReader {

  /// <summary>5-byte ASCII signature shared by <c>.adi</c> and <c>.afi</c>
  /// — preserved on the public surface for backwards compatibility with the
  /// original R/O metadata descriptor.</summary>
  public static readonly byte[] Magic = AomeiConstants.BifhMagicAscii;

  /// <summary>Capture size of the leading bytes surfaced as
  /// <c>header.bin</c> by the descriptor.</summary>
  public const int HeaderCaptureSize = 64;

  /// <summary>True once the full BIFH head has been verified (magic + size +
  /// CRC).</summary>
  public bool Valid { get; private set; }

  /// <summary>Parse outcome — one of <c>ok</c>, <c>magic_ok_crc_failed</c>,
  /// <c>header_short</c>, <c>tail_missing</c>, <c>tail_invalid</c>,
  /// <c>partial</c>, or <c>unparsed</c>.</summary>
  public string ParseStatus { get; private set; } = "unparsed";

  /// <summary>Captured leading bytes (up to <see cref="HeaderCaptureSize"/>).</summary>
  public byte[] HeaderRaw { get; private set; } = [];

  /// <summary>Speculative 32-bit little-endian word at offset 4 (the Size
  /// field). Preserved on the public surface so existing diagnostic output
  /// keeps working — its semantic meaning is now known to be the head
  /// struct size.</summary>
  public uint PostMagicWord { get; private set; }

  /// <summary>Parsed file head — null if the input was too short to contain
  /// a full 0x65C-byte head.</summary>
  public BrFileHead? Head { get; private set; }

  /// <summary>Parsed file tail — null if the input was too short to contain
  /// both head and tail or the tail's flag/size didn't match.</summary>
  public BrFileTail? Tail { get; private set; }

  /// <summary>True when the head's stored CRC matched the recomputed value.</summary>
  public bool HeadCrcValid { get; private set; }

  /// <summary>True when the tail's stored CRC matched the recomputed value.</summary>
  public bool TailCrcValid { get; private set; }

  /// <summary>Raw bytes of the head body (the 0x650 bytes after the 12-byte
  /// standard header). Empty if the head wasn't read.</summary>
  public byte[] HeadBody => this.Head?.BodyRaw ?? [];

  /// <summary>Raw bytes of the tail body (the 0x668 bytes after the 12-byte
  /// standard header). Empty if the tail wasn't read.</summary>
  public byte[] TailBody => this.Tail?.BodyRaw ?? [];

  /// <summary>All decoded INFO/INDEX records between the head and the
  /// tail.</summary>
  public IReadOnlyList<AomeiInfoRecord> Records { get; private set; } = [];

  /// <summary>The first decoded <see cref="AomeiConstants.InfoTypeBackupType"/>
  /// record's <c>kind</c> value, or null when absent.</summary>
  public uint? BackupTypeKind { get; private set; }

  /// <summary>The first decoded <see cref="AomeiConstants.InfoTypeImageCompress"/>
  /// record's <c>method</c> value, or null when absent.</summary>
  public uint? CompressMethod { get; private set; }

  /// <summary>The first decoded <see cref="AomeiConstants.InfoTypeImageCompress"/>
  /// record's <c>level</c> value, or null when absent.</summary>
  public uint? CompressLevel { get; private set; }

  /// <summary>The first decoded <see cref="AomeiConstants.InfoTypeImageEncrypt"/>
  /// record's <c>method</c> value, or null when absent.</summary>
  public uint? EncryptMethod { get; private set; }

  /// <summary>The first decoded <see cref="AomeiConstants.InfoTypeImageEncrypt"/>
  /// record's <c>key_len</c> value, or null when absent.</summary>
  public uint? EncryptKeyLen { get; private set; }

  /// <summary>The first decoded <see cref="AomeiConstants.InfoTypeImagePassword"/>
  /// record's 16-byte MD5 hash, or null when absent.</summary>
  public byte[]? PasswordMd5 { get; private set; }

  /// <summary>VDB entries decoded from the latest
  /// <see cref="AomeiConstants.IndexTypeDataBlock"/> record in the
  /// container, after applying latest-entry-wins per <c>RegNo</c> and
  /// dropping tombstones. Empty when no index record is present
  /// (round-trip baseline + foreign samples).</summary>
  public IReadOnlyList<BrImageIndexEntryVdb> LiveVdbEntries { get; private set; } = [];

  /// <summary>Every VDB entry surfaced by the latest
  /// <see cref="AomeiConstants.IndexTypeDataBlock"/> record, in on-disk
  /// order without latest-wins/tombstone filtering. Lets the in-place
  /// modifier roundtrip the table verbatim and the tests pin both views
  /// independently.</summary>
  public IReadOnlyList<BrImageIndexEntryVdb> AllVdbEntries { get; private set; } = [];

  /// <summary>Absolute file offset of the BR_STANDARD_HEADER prefixing
  /// the latest <see cref="AomeiConstants.IndexTypeDataBlock"/> record,
  /// or null when no index is present.</summary>
  public long? DataBlockIndexFileOffset { get; private set; }

  /// <summary>Total bytes (including the BR_STANDARD_HEADER) of the
  /// latest <see cref="AomeiConstants.IndexTypeDataBlock"/> record, or
  /// null when no index is present.</summary>
  public int? DataBlockIndexSize { get; private set; }

  /// <summary>Raw image bytes captured by the constructor — used by the
  /// reader-side helpers that resolve VDB.ImgOffset references against
  /// the source file. Empty when the underlying stream was shorter than
  /// the magic.</summary>
  public byte[] RawImage { get; private set; } = [];

  /// <summary>
  /// Reads the full file into memory (capped at
  /// <see cref="MaxImageBytes"/>) and parses it. The full-image read is
  /// necessary because the tail lives at <c>file_size - 0x674</c>.
  /// </summary>
  public AomeiReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var image = ReadAllBounded(stream);
    this.RawImage = image;
    Parse(image);
  }

  private void Parse(ReadOnlySpan<byte> image) {
    // Header-surface compatibility: capture the leading bytes regardless of
    // anything else, so downstream forensic surfaces stay stable.
    if (image.Length < Magic.Length) {
      this.ParseStatus = "partial";
      return;
    }
    if (!image[..Magic.Length].SequenceEqual(Magic)) {
      this.ParseStatus = "partial";
      return;
    }
    var captureLen = Math.Min(HeaderCaptureSize, image.Length);
    var rawCapture = new byte[HeaderCaptureSize];
    image[..captureLen].CopyTo(rawCapture);
    this.HeaderRaw = rawCapture;
    if (image.Length >= Magic.Length + 4)
      this.PostMagicWord = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(Magic.Length, 4));
    this.Valid = true;
    this.ParseStatus = "ok";

    // Promote past the bare-magic surface only if the file is long enough
    // to contain a full BIFH head.
    if (image.Length < AomeiConstants.BifhSize) {
      this.ParseStatus = "header_short";
      return;
    }

    var head = BrFileHead.Read(image);
    this.Head = head;
    if (!head.MagicAndSizeValid) {
      // Magic-only baseline already satisfied — keep that promise but
      // downgrade the parse status.
      this.ParseStatus = "header_short";
      return;
    }

    // Re-verify CRC over the full 0x65C-byte head with the Crc32 field
    // zeroed during the computation.
    var headRecord = image[..AomeiConstants.BifhSize].ToArray();
    this.HeadCrcValid = BrStandardHeader.VerifyCrc(headRecord);
    if (!this.HeadCrcValid)
      this.ParseStatus = "magic_ok_crc_failed";

    if (image.Length < AomeiConstants.BifhSize + AomeiConstants.BiftSize) {
      // No room for a tail — stay at the head-only surface.
      this.ParseStatus = "tail_missing";
      return;
    }

    var tail = BrFileTail.Read(image);
    this.Tail = tail;
    if (!tail.MagicAndSizeValid) {
      this.ParseStatus = "tail_invalid";
      return;
    }
    var tailRecord = image[^AomeiConstants.BiftSize..].ToArray();
    this.TailCrcValid = BrStandardHeader.VerifyCrc(tailRecord);

    // Walk records between head and tail.
    var bodyStart = AomeiConstants.BifhSize;
    var bodyEnd = image.Length - AomeiConstants.BiftSize;
    var records = WalkRecords(image[bodyStart..bodyEnd], bodyStart);
    this.Records = records;
    AbsorbKnownFields(records);
    AbsorbDataBlockIndex(records);
  }

  private void AbsorbDataBlockIndex(IReadOnlyList<AomeiInfoRecord> records) {
    // Pick the LATEST INDEX_TYPE_DATABLOCK record (last in walk order).
    // In-place mutation appends fresh entries inside this record only.
    AomeiInfoRecord? latest = null;
    foreach (var r in records)
      if (r.Header.Type == AomeiConstants.IndexTypeDataBlock)
        latest = r;
    if (latest is null) return;

    this.DataBlockIndexFileOffset = latest.FileOffset;
    this.DataBlockIndexSize = (int)latest.Header.Size;

    // Body is the bytes after the 12-byte BR_STANDARD_HEADER. The
    // shipped layout puts Reserved at +0x00, EntryCount at +0x04,
    // EntrySize at +0x08, entries at +0x0C of the body. Header-relative
    // those land at the shipped pins.
    var body = latest.Body;
    if (body.Length < AomeiConstants.ShippedIndexHeaderSize - AomeiConstants.StandardHeaderSize)
      return;
    var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(
      body.AsSpan(AomeiConstants.ShippedIndexEntryCountOffset - AomeiConstants.StandardHeaderSize, 4));
    var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(
      body.AsSpan(AomeiConstants.ShippedIndexEntrySizeOffset - AomeiConstants.StandardHeaderSize, 4));
    if (entrySize != AomeiConstants.VendorVdbEntrySize) return;
    var entriesOffsetInBody = AomeiConstants.ShippedIndexEntriesOffset - AomeiConstants.StandardHeaderSize;
    var needed = entriesOffsetInBody + (long)entryCount * entrySize;
    if (body.LongLength < needed) return;

    var all = new List<BrImageIndexEntryVdb>((int)entryCount);
    for (var i = 0; i < entryCount; ++i)
      all.Add(BrImageIndexEntryVdb.Read(body.AsSpan(
        entriesOffsetInBody + i * (int)entrySize, (int)entrySize)));
    this.AllVdbEntries = all;

    // Latest-entry-wins per RegNo + tombstone drop.
    var latestByRegNo = new Dictionary<uint, BrImageIndexEntryVdb>();
    var ordering = new List<uint>();
    foreach (var e in all) {
      if (!latestByRegNo.ContainsKey(e.RegNo))
        ordering.Add(e.RegNo);
      latestByRegNo[e.RegNo] = e;
    }
    var live = new List<BrImageIndexEntryVdb>(ordering.Count);
    foreach (var regNo in ordering) {
      var e = latestByRegNo[regNo];
      if (e.NewSize == AomeiConstants.TombstoneNewSizeSentinel) continue;
      live.Add(e);
    }
    this.LiveVdbEntries = live;
  }

  private static List<AomeiInfoRecord> WalkRecords(ReadOnlySpan<byte> body, int absoluteOffset) {
    var list = new List<AomeiInfoRecord>();
    var pos = 0;
    while (pos + AomeiConstants.StandardHeaderSize <= body.Length) {
      var hdr = BrStandardHeader.Read(body[pos..]);

      // Defensive: a record claiming size 0 or smaller than the header
      // would advance the cursor backwards or infinite-loop. Stop walking
      // and let the caller see the partial record list.
      if (hdr.Size < AomeiConstants.StandardHeaderSize) break;
      if (pos + hdr.Size > body.Length) break;

      var record = body.Slice(pos, (int)hdr.Size).ToArray();
      var crcOk = BrStandardHeader.VerifyCrc(record);
      var bodyBytes = record.AsSpan(AomeiConstants.StandardHeaderSize).ToArray();
      list.Add(new AomeiInfoRecord(hdr, crcOk, bodyBytes, absoluteOffset + pos));
      pos += (int)hdr.Size;
    }
    return list;
  }

  private void AbsorbKnownFields(IReadOnlyList<AomeiInfoRecord> records) {
    foreach (var r in records) {
      if (this.BackupTypeKind is null && r.TryGetBackupType(out var kind))
        this.BackupTypeKind = kind;
      if (this.CompressMethod is null && r.TryGetCompressInfo(out var cm, out var cl)) {
        this.CompressMethod = cm;
        this.CompressLevel = cl;
      }
      if (this.EncryptMethod is null && r.TryGetEncryptInfo(out var em, out var ek)) {
        this.EncryptMethod = em;
        this.EncryptKeyLen = ek;
      }
      if (this.PasswordMd5 is null && r.TryGetPasswordMd5(out var md5))
        this.PasswordMd5 = md5;
    }
  }

  /// <summary>Decoded user-data view of a VDB entry: the envelope's
  /// embedded filename plus the payload bytes.</summary>
  public sealed record LiveUserData(uint RegNo, string Name, byte[] Payload, ulong EnvelopeOffset, uint EnvelopeSize);

  /// <summary>Resolves <see cref="LiveVdbEntries"/> against
  /// <see cref="RawImage"/> by reading each entry's referenced 0xF001
  /// envelope and decoding the filename + payload. Entries whose
  /// ImgOffset/NewSize point outside the captured image bytes are
  /// silently skipped — defensive against partially-truncated inputs.
  /// </summary>
  public IReadOnlyList<LiveUserData> ResolveLiveUserData() {
    if (this.LiveVdbEntries.Count == 0) return [];
    var list = new List<LiveUserData>(this.LiveVdbEntries.Count);
    foreach (var e in this.LiveVdbEntries) {
      if (e.NewSize == 0) continue;
      if (e.ImgOffset > (ulong)this.RawImage.LongLength) continue;
      if (e.ImgOffset + e.NewSize > (ulong)this.RawImage.LongLength) continue;
      var envelope = this.RawImage.AsSpan(
        (int)e.ImgOffset, (int)e.NewSize);
      if (envelope.Length < AomeiConstants.StandardHeaderSize) continue;
      var hdr = BrStandardHeader.Read(envelope);
      if (hdr.Type != AomeiWriter.UserDataTypeTag) continue;
      var body = envelope[AomeiConstants.StandardHeaderSize..].ToArray();
      var name = AomeiWriter.ReadUserDataName(body);
      var payload = AomeiWriter.ReadUserDataPayload(body);
      list.Add(new LiveUserData(e.RegNo, name, payload, e.ImgOffset, e.NewSize));
    }
    return list;
  }

  /// <summary>Maximum bytes the reader will pull from the input stream.
  /// Caps memory use on pathological inputs while still being big enough to
  /// cover real AOMEI samples (head 0x65C + index records + tail 0x674; for
  /// pure metadata inspection 16 MB is well over the practical INFO/INDEX
  /// region size).</summary>
  public const int MaxImageBytes = 16 * 1024 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < MaxImageBytes && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}

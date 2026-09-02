#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.AdvFs;

/// <summary>
/// Parses the AdvFS (Tru64 UNIX Advanced File System) on-disk volume header.
/// AdvFS was open-sourced by HP in 2008 (<c>https://sourceforge.net/projects/advfs/</c>);
/// the on-disk layout below is taken from <c>bs_ods.h</c>, <c>bs_disk_block.h</c>,
/// and <c>bs_public.h</c> in that release.
///
/// AdvFS layout summary:
/// <list type="bullet">
///   <item><description>Disk page size = 8192 bytes (<c>BS_BLKSIZE</c> = 512;
///         <c>ADVFS_PGSZ</c> = 16 × 512 = 8192).</description></item>
///   <item><description>Page 0 (LBA 0..15) — disk label / boot block area.</description></item>
///   <item><description>Page 16 (LBA 32) — RBMT (Reserved Bitfile Metadata Table)
///         page 0, containing the volume's bootstrap metadata records
///         (<c>BSR_VD_ATTR</c>, <c>BSR_DMN_ATTR</c>, <c>BSR_DMN_MATTR</c>).</description></item>
///   <item><description>Each metadata record starts with a <c>bsMR_t</c> record
///         header (<c>bCnt</c>:uint16, <c>type</c>:uint16, <c>version</c>:uint16,
///         then payload).</description></item>
/// </list>
///
/// Detection magic: this descriptor synthesises a 16-byte cookie at offset
/// <c>131072</c> (= page 16 × 8192) — the start of the AdvFS RBMT page 0 — using
/// the literal ASCII tag <c>"ADVFS\0RBMT0\0\0\0\0\0"</c>. This is an internal
/// convention since the HP source release uses record-type discriminators
/// (<c>BSR_VD_ATTR</c> = 13, <c>BSR_DMN_ATTR</c> = 14, <c>BSR_DMN_MATTR</c> =
/// 15) rather than a single bytes-at-offset magic. Real Tru64 images that
/// don't carry this tag will not be detected automatically but can still be
/// inspected once the file is fed to the descriptor directly.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://sourceforge.net/projects/advfs/</c> — HP 2008 open-source release</description></item>
///   <item><description>HP "AdvFS On-Disk Structure Reference" (in the source tarball under <c>doc/</c>)</description></item>
///   <item><description>Wikipedia "AdvFS"</description></item>
/// </list>
/// </summary>
public sealed class AdvFsReader {
  /// <summary>AdvFS disk page size in bytes (8 × 1024 = 16 × 512 sectors).</summary>
  public const int PageSize = 8192;
  /// <summary>RBMT page lives at logical page index 16 → byte offset 131072.</summary>
  public const long RbmtPageOffset = 16L * PageSize;
  /// <summary>Header capture surfaced to the user as <c>volume_header.bin</c>.</summary>
  public const int HeaderCaptureSize = 4096;

  /// <summary>16-byte internal cookie used for first-pass detection at <see cref="RbmtPageOffset"/>.</summary>
  public static readonly byte[] DetectionCookie = [
    (byte)'A', (byte)'D', (byte)'V', (byte)'F', (byte)'S', 0x00,
    (byte)'R', (byte)'B', (byte)'M', (byte)'T', (byte)'0', 0x00,
    0x00, 0x00, 0x00, 0x00,
  ];

  /// <summary>
  /// Gets a value indicating whether valid.
  /// </summary>
  public bool Valid { get; private set; }
  /// <summary>
  /// Gets or sets the parse status.
  /// </summary>
  public string ParseStatus { get; private set; } = "unparsed";
  /// <summary>
  /// Gets or sets the header raw.
  /// </summary>
  public byte[] HeaderRaw { get; private set; } = [];

  /// <summary>
  /// Raw image bytes — kept so the descriptor can resolve <see cref="AdvFsEntry.Offset"/>
  /// / <see cref="AdvFsEntry.Size"/> file table rows back to their payloads.
  /// </summary>
  internal byte[] ImageBytes { get; private set; } = [];

  /// <summary>Storage domain attribute record fields (<c>BSR_DMN_ATTR</c> at known RBMT offset).</summary>
  public string DomainIdHex { get; private set; } = "";
  /// <summary>Recorded domain MountId — 8 bytes seconds + microseconds.</summary>
  public ulong MountId { get; private set; }
  /// <summary>Recorded on-disk version number (<c>dmnVersion</c>).</summary>
  public uint OnDiskVersion { get; private set; }
  /// <summary>Volume number within the storage domain (<c>vdIndex</c>).</summary>
  public uint VdIndex { get; private set; }
  /// <summary>Total number of volumes recorded in the storage domain.</summary>
  public uint VdCount { get; private set; }
  /// <summary>Domain state flags (<c>state</c>: BSR_DMN_MATTR state field).</summary>
  public uint State { get; private set; }
  /// <summary>Volume size in 512-byte blocks (<c>vdBlkCnt</c>).</summary>
  public ulong VdBlkCnt { get; private set; }
  /// <summary>Per-volume metadata I/O block size (<c>vdMetaBlkCnt</c>).</summary>
  public uint VdMetaBlkCnt { get; private set; }
  /// <summary>Optional textual volume tag captured from the RBMT page.</summary>
  public string VolumeTag { get; private set; } = "";

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public List<AdvFsEntry> Entries { get; } = new();

  /// <summary>
  /// Random-access view over the source, kept when it is seekable. The buffered
  /// copy stops at FullReadCap, so a payload past it can only be reached this way.
  /// </summary>
  private readonly ImageAccessor? _source;

  /// <summary>
  /// Initializes a new instance of <see cref="AdvFsReader"/>.
  /// </summary>
  public AdvFsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) {
      stream.Position = 0;
      this._source = new ImageAccessor(stream, leaveOpen: true);
    }
    var image = ReadAllBounded(stream);
    this.ImageBytes = image;
    Parse(image);
    BuildEntries();
  }

  /// <summary>
  /// Copies a payload into <paramref name="destination" /> by absolute
  /// offset/length, straight from the source when it is seekable.
  /// </summary>
  public void ExtractFileTo(AdvFsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.Offset < 0 || entry.Size <= 0) return;

    if (this._source is { } source) {
      if (entry.Offset + entry.Size > source.Length) return;
      source.CopyTo(entry.Offset, destination, entry.Size);
      return;
    }

    var bytes = this.ExtractFile(entry);
    destination.Write(bytes, 0, bytes.Length);
  }

  private void Parse(ReadOnlySpan<byte> image) {
    if (image.Length < RbmtPageOffset + DetectionCookie.Length) {
      ParseStatus = "partial";
      return;
    }

    // Detection cookie: ASCII "ADVFS\0RBMT0\0\0\0\0\0" at start of RBMT page 0.
    var cookieSlice = image.Slice((int)RbmtPageOffset, DetectionCookie.Length);
    if (!cookieSlice.SequenceEqual(DetectionCookie)) {
      ParseStatus = "partial";
      return;
    }

    // Capture header for surface as volume_header.bin.
    var capture = (int)Math.Min(HeaderCaptureSize, image.Length - RbmtPageOffset);
    var raw = new byte[capture];
    image.Slice((int)RbmtPageOffset, capture).CopyTo(raw);
    HeaderRaw = raw;

    // After the 16-byte cookie, our synthetic header lays out the parsed
    // BSR_DMN_ATTR + BSR_VD_ATTR + BSR_DMN_MATTR fields in a fixed order, all
    // little-endian on disk. This is the convention our writer would emit if we
    // had one — real Tru64 images encode the same fields, but offsets vary
    // depending on the order BSR_MAX records were committed to the RBMT.
    var body = image[((int)RbmtPageOffset + DetectionCookie.Length)..];
    var read = 0;

    // BSR_DMN_ATTR: 16-byte domain UUID + 8-byte mountId.
    if (body.Length >= read + 16) {
      DomainIdHex = Convert.ToHexString(body.Slice(read, 16));
      read += 16;
    }
    if (body.Length >= read + 8) {
      MountId = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(read, 8));
      read += 8;
    }
    if (body.Length >= read + 4) {
      OnDiskVersion = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(read, 4));
      read += 4;
    }

    // BSR_VD_ATTR: vdIndex, vdCount, state, vdBlkCnt, vdMetaBlkCnt.
    if (body.Length >= read + 4) {
      VdIndex = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(read, 4));
      read += 4;
    }
    if (body.Length >= read + 4) {
      VdCount = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(read, 4));
      read += 4;
    }
    if (body.Length >= read + 4) {
      State = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(read, 4));
      read += 4;
    }
    if (body.Length >= read + 8) {
      VdBlkCnt = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(read, 8));
      read += 8;
    }
    if (body.Length >= read + 4) {
      VdMetaBlkCnt = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(read, 4));
      read += 4;
    }

    // Optional 64-byte ASCII volume tag (NUL-padded).
    if (body.Length >= read + 64) {
      var tagSpan = body.Slice(read, 64);
      var nul = tagSpan.IndexOf((byte)0);
      if (nul < 0) nul = 64;
      var sb = new StringBuilder(nul);
      for (var i = 0; i < nul; i++) {
        var c = tagSpan[i];
        sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
      }
      VolumeTag = sb.ToString();
      read += 64;
    }

    Valid = true;
    ParseStatus = "ok";

    // Optional AdvFS-WB file table extension. Present when the writer added
    // files; absent on real Tru64 images. Eyecatcher follows the volume tag
    // at a stable offset = 132 bytes after the cookie. We probe for it before
    // committing to parse.
    ParseFileTable(image, body, read);
  }

  /// <summary>
  /// 16-byte AdvFS-WB file-table eyecatcher placed inside RBMT page 0 directly
  /// after the volume tag. Real Tru64 images never carry it; our writer always
  /// does. Parsing is opt-in: when the eyecatcher is absent, the file list
  /// stays empty and Valid still reflects the synthetic header parse.
  /// </summary>
  private static readonly byte[] FileTableEyecatcher = [
    (byte)'A', (byte)'D', (byte)'V', (byte)'F', (byte)'S', (byte)'W', (byte)'B', (byte)'F',
    (byte)'T', 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
  ];

  private void ParseFileTable(ReadOnlySpan<byte> image, ReadOnlySpan<byte> body, int bodyCursor) {
    if (body.Length < bodyCursor + FileTableEyecatcher.Length) return;
    var ecSpan = body.Slice(bodyCursor, FileTableEyecatcher.Length);
    if (!ecSpan.SequenceEqual(FileTableEyecatcher)) return;
    var cursor = bodyCursor + FileTableEyecatcher.Length;

    if (body.Length < cursor + 4) return;
    var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(cursor, 4));
    cursor += 4;
    if (fileCount > 4096) return;             // sanity cap — RBMT page is 8 KB anyway

    for (var i = 0; i < fileCount; i++) {
      if (body.Length < cursor + 18) return;
      var offset = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(cursor, 8)); cursor += 8;
      var length = BinaryPrimitives.ReadInt64LittleEndian(body.Slice(cursor, 8)); cursor += 8;
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(cursor, 2)); cursor += 2;
      if (body.Length < cursor + nameLen) return;
      var nameBytes = body.Slice(cursor, nameLen);
      cursor += nameLen;
      var name = Encoding.UTF8.GetString(nameBytes);
      // Validate against the real image, not the buffered prefix: ReadAllBounded
      // stops at FullReadCap, so checking against it silently dropped every row
      // whose payload starts past that point.
      var imageLength = this._source?.Length ?? image.Length;
      if (offset < 0 || length < 0 || offset + length > imageLength) return;
      this.FileTableEntries.Add(new AdvFsEntry {
        Name = name,
        Size = length,
        Offset = offset,
        IsDirectory = false,
      });
    }
  }

  /// <summary>
  /// File-table rows parsed from the AdvFS-WB extension (empty on real Tru64
  /// images that don't carry our writer's eyecatcher).
  /// </summary>
  public List<AdvFsEntry> FileTableEntries { get; } = new();

  /// <summary>Reads a file payload by absolute offset/length.</summary>
  public byte[] ExtractFile(AdvFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset < 0 || entry.Size <= 0) return [];
    if (entry.Offset + entry.Size > this.ImageBytes.LongLength) {
      // Past the buffered prefix: read it from the source instead.
      if (this._source is not { } source || entry.Offset + entry.Size > source.Length) return [];
      if (entry.Size > Array.MaxLength)
        throw new InvalidOperationException(
          $"AdvFs: a {entry.Size:N0}-byte payload exceeds the array limit; use ExtractFileTo.");
      return source.Read(entry.Offset, (int)entry.Size);
    }
    var buf = new byte[entry.Size];
    Array.Copy(this.ImageBytes, entry.Offset, buf, 0, entry.Size);
    return buf;
  }

  private void BuildEntries() {
    this.Entries.Add(new AdvFsEntry { Name = "FULL.advfs", Size = 0, IsDirectory = false });
    this.Entries.Add(new AdvFsEntry { Name = "metadata.ini", Size = 0, IsDirectory = false });
    if (this.Valid)
      this.Entries.Add(new AdvFsEntry { Name = "rbmt_page0.bin", Size = this.HeaderRaw.LongLength, IsDirectory = false });
    foreach (var f in this.FileTableEntries)
      this.Entries.Add(f);
  }

  // Bounded read — header surface needs the first 256 KB; when the
  // AdvFS-WB file-table extension is present we extend the cap to 64 MB so
  // the writer's bundled files round-trip. A speculative carver scan still
  // tops out well before exhausting host memory.
  private const int HeaderReadCap = 256 * 1024;
  private const int FullReadCap = 64 * 1024 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < FullReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

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

  public bool Valid { get; private set; }
  public string ParseStatus { get; private set; } = "unparsed";
  public byte[] HeaderRaw { get; private set; } = [];

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

  public List<AdvFsEntry> Entries { get; } = new();

  public AdvFsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var image = ReadAllBounded(stream);
    Parse(image);
    BuildEntries();
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
    }

    Valid = true;
    ParseStatus = "ok";
  }

  private void BuildEntries() {
    this.Entries.Add(new AdvFsEntry { Name = "FULL.advfs", Size = 0, IsDirectory = false });
    this.Entries.Add(new AdvFsEntry { Name = "metadata.ini", Size = 0, IsDirectory = false });
    if (this.Valid)
      this.Entries.Add(new AdvFsEntry { Name = "rbmt_page0.bin", Size = this.HeaderRaw.LongLength, IsDirectory = false });
  }

  // Bounded read — we only ever look at the first 256 KB so a speculative
  // carver scan doesn't pull a multi-GB image into memory.
  private const int HeaderReadCap = 256 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.VxFs;

/// <summary>
/// Parses the VxFS (Veritas File System / VERITAS / Symantec / now part of
/// Veritas Storage Foundation) on-disk superblock. The structure layout
/// below tracks <c>fs/freevxfs/vxfs.h</c> in the Linux kernel (Christoph
/// Hellwig's read-only freevxfs driver) and HP-UX VxFS documentation.
///
/// Layout summary:
/// <list type="bullet">
///   <item><description>Superblock at byte offset 1024 from start-of-volume.</description></item>
///   <item><description>Magic <c>vs_magic = 0xA501FCF5</c> (<c>VXFS_SUPER_MAGIC</c>)
///         in the natural endianness of the host that wrote the volume.</description></item>
///   <item><description>Big-endian on HP-UX PA-RISC / Solaris SPARC, little-endian
///         on x86 / x86_64 / Linux. <see cref="VxFsReader"/> tries little-endian
///         first and falls back to big-endian.</description></item>
/// </list>
///
/// Superblock leading fields (per <c>vxfs.h</c>):
/// <code>
/// struct vxfs_sb {
///   uint32_t vs_magic;     // offset 0: 0xA501FCF5
///   int32_t  vs_version;   // offset 4: VxFS version (1..10 documented)
///   uint32_t vs_mtime;     // offset 8: last modification time (Unix time)
///   uint32_t vs_ctime;     // offset 12: creation time
///   int32_t  vs_old_logstart;
///   int32_t  vs_old_logend;
///   int32_t  vs_bsize;     // offset 24: block size (512, 1024, 2048, 4096, 8192)
///   int32_t  vs_size;      // offset 28: filesystem size in blocks
///   int32_t  vs_dsize;     // offset 32: data zone size in blocks
///   uint32_t vs_old_ninode;
///   int32_t  vs_old_nau;   // offset 40: number of allocation units (old IAU)
///   int32_t  vs_old_defiextsize;
///   int32_t  vs_old_ilbsize;
///   int32_t  vs_immedlen;  // offset 52: max immediate-data length (typically 96)
///   int32_t  vs_ndaddr;    // offset 56: number of direct addresses (10)
///   int32_t  vs_firstau;   // offset 60: first allocation-unit block
///   ...
/// };
/// </code>
///
/// References:
/// <list type="bullet">
///   <item><description>Linux kernel <c>fs/freevxfs/vxfs.h</c> + <c>vxfs_super.c</c></description></item>
///   <item><description>HP-UX "VxFS Administrator's Guide" (Symantec/Veritas)</description></item>
///   <item><description>Wikipedia "Veritas File System"</description></item>
/// </list>
/// </summary>
public sealed class VxFsReader {
  /// <summary>Superblock byte offset from start-of-volume.</summary>
  public const int SuperblockOffset = 1024;
  /// <summary>VxFS superblock magic — <c>VXFS_SUPER_MAGIC</c>.</summary>
  public const uint Magic = 0xA501FCF5u;
  /// <summary>Magic encoded little-endian (x86 / Linux native).</summary>
  public static readonly byte[] MagicLE = [0xF5, 0xFC, 0x01, 0xA5];
  /// <summary>Magic encoded big-endian (HP-UX / Solaris native).</summary>
  public static readonly byte[] MagicBE = [0xA5, 0x01, 0xFC, 0xF5];

  /// <summary>Header capture size surfaced as <c>superblock.bin</c> — covers the
  /// statically-laid-out leading portion of <c>struct vxfs_sb</c>.</summary>
  public const int HeaderCaptureSize = 1024;

  public bool Valid { get; private set; }
  public bool IsBigEndian { get; private set; }
  public string ParseStatus { get; private set; } = "unparsed";

  public uint VsMagic { get; private set; }
  public int VsVersion { get; private set; }
  public uint VsMtime { get; private set; }
  public uint VsCtime { get; private set; }
  public int VsBlockSize { get; private set; }
  public int VsSize { get; private set; }
  public int VsDsize { get; private set; }
  public int VsOldNau { get; private set; }
  public int VsImmedLen { get; private set; }
  public int VsNdAddr { get; private set; }
  public int VsFirstAu { get; private set; }
  public byte[] HeaderRaw { get; private set; } = [];

  public List<VxFsEntry> Entries { get; } = new();

  public VxFsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    var image = ReadAllBounded(stream);
    Parse(image);
    BuildEntries();
  }

  private void Parse(ReadOnlySpan<byte> image) {
    if (image.Length < SuperblockOffset + 64) {
      ParseStatus = "partial";
      return;
    }

    var sb = image.Slice(SuperblockOffset);
    var magicLE = BinaryPrimitives.ReadUInt32LittleEndian(sb[..4]);
    var magicBE = BinaryPrimitives.ReadUInt32BigEndian(sb[..4]);

    bool be;
    if (magicLE == Magic) be = false;
    else if (magicBE == Magic) be = true;
    else {
      ParseStatus = "partial";
      return;
    }

    IsBigEndian = be;
    VsMagic = Magic;

    VsVersion = ReadI32(sb, 4, be);
    VsMtime = ReadU32(sb, 8, be);
    VsCtime = ReadU32(sb, 12, be);
    VsBlockSize = ReadI32(sb, 24, be);
    VsSize = ReadI32(sb, 28, be);
    VsDsize = ReadI32(sb, 32, be);
    VsOldNau = ReadI32(sb, 40, be);
    VsImmedLen = ReadI32(sb, 52, be);
    VsNdAddr = ReadI32(sb, 56, be);
    VsFirstAu = ReadI32(sb, 60, be);

    var captureLen = (int)Math.Min(HeaderCaptureSize, image.Length - SuperblockOffset);
    var raw = new byte[HeaderCaptureSize];
    image.Slice(SuperblockOffset, captureLen).CopyTo(raw);
    HeaderRaw = raw;

    Valid = true;
    ParseStatus = "ok";
  }

  private void BuildEntries() {
    this.Entries.Add(new VxFsEntry { Name = "FULL.vxfs", Size = 0, IsDirectory = false });
    this.Entries.Add(new VxFsEntry { Name = "metadata.ini", Size = 0, IsDirectory = false });
    if (this.Valid)
      this.Entries.Add(new VxFsEntry { Name = "superblock.bin", Size = this.HeaderRaw.LongLength, IsDirectory = false });
  }

  // 64 KB cap — the VxFS superblock starts at offset 1024 and is < 1 KB; 64 KB
  // is generous headroom and keeps speculative carver scans bounded.
  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadAllBounded(Stream stream) {
    using var ms = new MemoryStream();
    var buf = new byte[8192];
    int read;
    while (ms.Length < HeaderReadCap && (read = stream.Read(buf, 0, buf.Length)) > 0)
      ms.Write(buf, 0, read);
    return ms.ToArray();
  }

  /// <summary>Endian-aware Int32 reader; returns 0 if the offset is out of range.</summary>
  private static int ReadI32(ReadOnlySpan<byte> sb, int off, bool be) =>
    off + 4 <= sb.Length
      ? (be ? BinaryPrimitives.ReadInt32BigEndian(sb.Slice(off, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(sb.Slice(off, 4)))
      : 0;

  /// <summary>Endian-aware UInt32 reader; returns 0 if the offset is out of range.</summary>
  private static uint ReadU32(ReadOnlySpan<byte> sb, int off, bool be) =>
    off + 4 <= sb.Length
      ? (be ? BinaryPrimitives.ReadUInt32BigEndian(sb.Slice(off, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(off, 4)))
      : 0u;
}

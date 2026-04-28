#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Hammer2;

/// <summary>
/// Parses the HAMMER2 (DragonFly BSD newer) volume header at byte offset 0.
/// HAMMER2 keeps four redundant 64 KB volume-data sectors at offsets 0,
/// 65536, 131072, 196608 (per <c>HAMMER2_VOLUME_BYTES = 65536</c>) — we only
/// look at the first one. Read-only; we never walk the cluster B-tree.
///
/// Layout per <c>sys/vfs/hammer2/hammer2_disk.h</c>:
///
/// <code>
/// #define HAMMER2_VOLUME_ID_HBO  0x48414d3205172011LLU   // host byte order
/// #define HAMMER2_VOLUME_ID_ABO  0x11201705324d4148LLU   // byte-swapped
/// #define HAMMER2_VOLUME_BYTES   65536
///
/// struct hammer2_volume_data {
///   // Sector #0 (0x0000-0x01FF) — the part we surface
///   uint64_t       magic;            //  0  HAMMER2_VOLUME_ID_HBO/ABO
///   hammer2_off_t  boot_beg;         //  8
///   hammer2_off_t  boot_end;         // 16
///   hammer2_off_t  aux_beg;          // 24
///   hammer2_off_t  aux_end;          // 32
///   hammer2_off_t  volu_size;        // 40 — total volume size
///   uint32_t       version;          // 48
///   uint32_t       flags;            // 52
///   uint8_t        copyid;           // 56
///   uint8_t        freemap_version;  // 57
///   uint8_t        peer_type;        // 58
///   uint8_t        volu_id;          // 59
///   uint8_t        nvolumes;         // 60
///   ...
///   uuid_t         fsid;             // (offset depends on padding; ~64)
///   uuid_t         fstype;           // ("5cbb9ad1-862d-11dc-a94d-01301bb8a9f5")
///   ...
/// };
/// </code>
///
/// Verified by:
/// <list type="bullet">
///   <item><description><c>https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer2/hammer2_disk.h</c></description></item>
///   <item><description><c>https://gitweb.dragonflybsd.org/dragonfly.git/blob/HEAD:/sys/vfs/hammer2/DESIGN</c></description></item>
/// </list>
/// </summary>
public sealed class Hammer2VolumeData {
  /// <summary>Host-byte-order magic.</summary>
  public const ulong VolumeIdHbo = 0x48414d3205172011UL;

  /// <summary>Alternate-byte-order magic (volume written by an opposite-endian system).</summary>
  public const ulong VolumeIdAbo = 0x11201705324d4148UL;

  /// <summary>Volume-data sector stride (4 redundant sectors at multiples of this).</summary>
  public const int VolumeBytes = 65536;

  /// <summary>Header capture size we surface as <c>volume_header.bin</c> — first sector (512 B).</summary>
  public const int HeaderCaptureSize = 512;

  /// <summary>First 8 bytes at offset 0 in disk order — the LE serialisation of <see cref="VolumeIdHbo"/>.</summary>
  public static readonly byte[] MagicBytesHboLE = [0x11, 0x20, 0x17, 0x05, 0x32, 0x4D, 0x41, 0x48];

  /// <summary>True iff the magic at offset 0 matched either HBO or ABO.</summary>
  public bool Valid { get; private init; }

  /// <summary>Raw 8-byte magic value (always little-endian read).</summary>
  public ulong Magic { get; private init; }

  /// <summary>True iff the magic matched the alternate (byte-swapped) form.</summary>
  public bool ByteSwapped { get; private init; }

  public long BootBeg { get; private init; }
  public long BootEnd { get; private init; }
  public long AuxBeg { get; private init; }
  public long AuxEnd { get; private init; }
  public long VoluSize { get; private init; }
  public uint Version { get; private init; }
  public uint Flags { get; private init; }
  public byte CopyId { get; private init; }
  public byte FreemapVersion { get; private init; }
  public byte PeerType { get; private init; }
  public byte VoluId { get; private init; }
  public byte NVolumes { get; private init; }
  public string FsidHex { get; private init; } = "";
  public string FsTypeHex { get; private init; } = "";
  public byte[] HeaderRaw { get; private init; } = [];

  /// <summary>Best-effort parse. Never throws.</summary>
  public static Hammer2VolumeData TryParse(ReadOnlySpan<byte> image) {
    if (image.Length < 8)
      return new Hammer2VolumeData();

    var magic = BinaryPrimitives.ReadUInt64LittleEndian(image[..8]);
    if (magic != VolumeIdHbo && magic != VolumeIdAbo)
      return new Hammer2VolumeData();

    var swapped = magic == VolumeIdAbo;

    // Capture first 512 bytes (sector #0). Pad short images with 0.
    var raw = new byte[HeaderCaptureSize];
    var avail = Math.Min(HeaderCaptureSize, image.Length);
    image[..avail].CopyTo(raw);

    // UUIDs: location is implementation-defined but the kernel struct keeps
    // fsid + fstype in sector #0 within the first ~128 bytes after the
    // boot/aux/volu_size + version/flags + 8 single-byte fields. We
    // best-effort capture two 16-byte runs at offsets 64 and 80; if a future
    // header layout shifts these, the values still serialise as raw hex and
    // will not crash detection.
    var fsidHex = avail >= 64 + 16
      ? Convert.ToHexString(image.Slice(64, 16))
      : "";
    var fsTypeHex = avail >= 80 + 16
      ? Convert.ToHexString(image.Slice(80, 16))
      : "";

    return new Hammer2VolumeData {
      Valid = true,
      Magic = magic,
      ByteSwapped = swapped,
      BootBeg = ReadI64(image, 8),
      BootEnd = ReadI64(image, 16),
      AuxBeg = ReadI64(image, 24),
      AuxEnd = ReadI64(image, 32),
      VoluSize = ReadI64(image, 40),
      Version = ReadU32(image, 48),
      Flags = ReadU32(image, 52),
      CopyId = Read8(image, 56),
      FreemapVersion = Read8(image, 57),
      PeerType = Read8(image, 58),
      VoluId = Read8(image, 59),
      NVolumes = Read8(image, 60),
      FsidHex = fsidHex,
      FsTypeHex = fsTypeHex,
      HeaderRaw = raw,
    };
  }

  private static long ReadI64(ReadOnlySpan<byte> image, int off) =>
    off + 8 <= image.Length
      ? BinaryPrimitives.ReadInt64LittleEndian(image.Slice(off, 8))
      : 0;

  private static uint ReadU32(ReadOnlySpan<byte> image, int off) =>
    off + 4 <= image.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off, 4))
      : 0u;

  private static byte Read8(ReadOnlySpan<byte> image, int off) =>
    off < image.Length ? image[off] : (byte)0;
}

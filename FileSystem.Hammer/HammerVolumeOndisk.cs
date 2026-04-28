#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hammer;

/// <summary>
/// Parses the HAMMER (DragonFly BSD original) volume header at byte offset 0 of
/// each volume. Read-only; we never walk the B-tree, only surface the header
/// fields and the <c>vol0_blockmap</c> array's byte slice.
///
/// Layout per <c>sys/vfs/hammer/hammer_disk.h</c>:
///
/// <code>
/// #define HAMMER_FSBUF_VOLUME  0xC8414D4DC5523031ULL   // "HAMMER01" (LE)
///
/// typedef struct hammer_volume_ondisk {
///   uint64_t      vol_signature;   //  0  = HAMMER_FSBUF_VOLUME
///   int64_t       vol_bot_beg;     //  8
///   int64_t       vol_mem_beg;     // 16
///   int64_t       vol_buf_beg;     // 24
///   int64_t       vol_buf_end;     // 32
///   int64_t       vol_reserved01;  // 40
///   uuid_t        vol_fsid;        // 48 (16 bytes)
///   uuid_t        vol_fstype;      // 64 (16 bytes — "HAMMER" type UUID)
///   char          vol_label[64];   // 80
///   int32_t       vol_no;          // 144
///   int32_t       vol_count;       // 148
///   uint32_t      vol_version;     // 152
///   hammer_crc_t  vol_crc;         // 156 (uint32)
///   uint32_t      vol_flags;       // 160
///   uint32_t      vol_rootvol;     // 164
///   uint32_t      vol_reserved[8]; // 168
///   int64_t       vol0_stat_bigblocks;     // 200
///   int64_t       vol0_stat_freebigblocks; // 208
///   int64_t       vol0_reserved01;         // 216
///   int64_t       vol0_stat_inodes;        // 224
///   int64_t       vol0_reserved02;         // 232
///   hammer_off_t  vol0_btree_root;         // 240
///   hammer_tid_t  vol0_next_tid;           // 248
///   hammer_off_t  vol0_reserved03;         // 256
///   ...
/// } hammer_volume_ondisk;
/// </code>
///
/// Verified by:
/// <list type="bullet">
///   <item><description><c>https://github.com/DragonFlyBSD/DragonFlyBSD/blob/master/sys/vfs/hammer/hammer_disk.h</c> — <c>HAMMER_FSBUF_VOLUME</c> + struct definition</description></item>
///   <item><description>DragonFly Wiki <c>https://www.dragonflybsd.org/hammer/</c></description></item>
/// </list>
/// </summary>
public sealed class HammerVolumeOndisk {
  /// <summary>
  /// HAMMER volume magic: <c>0xC8414D4DC5523031</c> ("HAMMER01" — first byte
  /// '1' = 0x31 because the 64-bit constant lives little-endian on disk).
  /// </summary>
  public const ulong VolumeSignature = 0xC8414D4DC5523031UL;

  /// <summary>First 8 bytes at offset 0, in disk order (LE serialisation of <see cref="VolumeSignature"/>).</summary>
  public static readonly byte[] MagicBytesLE = [0x31, 0x30, 0x52, 0xC5, 0x4D, 0x4D, 0x41, 0xC8];

  /// <summary>Header capture size we surface as <c>volume_header.bin</c> (1898 bytes covers the static fields).</summary>
  public const int HeaderCaptureSize = 1928;

  /// <summary>True iff <see cref="VolumeSignature"/> matched at offset 0.</summary>
  public bool Valid { get; private init; }

  public ulong VolSignature { get; private init; }
  public long VolBotBeg { get; private init; }
  public long VolMemBeg { get; private init; }
  public long VolBufBeg { get; private init; }
  public long VolBufEnd { get; private init; }
  public string VolFsidHex { get; private init; } = "";
  public string VolFsTypeHex { get; private init; } = "";
  public string VolLabel { get; private init; } = "";
  public int VolNo { get; private init; }
  public int VolCount { get; private init; }
  public uint VolVersion { get; private init; }
  public uint VolCrc { get; private init; }
  public uint VolFlags { get; private init; }
  public uint VolRootVol { get; private init; }
  public long Vol0StatBigblocks { get; private init; }
  public long Vol0StatFreeBigblocks { get; private init; }
  public long Vol0StatInodes { get; private init; }
  public long Vol0BtreeRoot { get; private init; }
  public long Vol0NextTid { get; private init; }
  public byte[] HeaderRaw { get; private init; } = [];

  /// <summary>Best-effort parse. Never throws.</summary>
  public static HammerVolumeOndisk TryParse(ReadOnlySpan<byte> image) {
    // Need at least 8 bytes to check the signature.
    if (image.Length < 8)
      return new HammerVolumeOndisk();

    var sig = BinaryPrimitives.ReadUInt64LittleEndian(image[..8]);
    if (sig != VolumeSignature)
      return new HammerVolumeOndisk();

    // Zero-pad header capture if image is shorter than 1928 bytes.
    var raw = new byte[HeaderCaptureSize];
    var avail = Math.Min(HeaderCaptureSize, image.Length);
    image[..avail].CopyTo(raw);

    var fsidHex = avail >= 48 + 16
      ? Convert.ToHexString(image.Slice(48, 16))
      : "";
    var fsTypeHex = avail >= 64 + 16
      ? Convert.ToHexString(image.Slice(64, 16))
      : "";

    var label = "";
    if (avail >= 80 + 64) {
      var labelSpan = image.Slice(80, 64);
      var n = labelSpan.IndexOf((byte)0);
      if (n < 0) n = 64;
      // Filter to printable ASCII to avoid embedding control bytes in metadata.ini.
      var sb = new StringBuilder(n);
      for (var i = 0; i < n; i++) {
        var c = labelSpan[i];
        sb.Append(c is >= 0x20 and < 0x7F ? (char)c : '.');
      }
      label = sb.ToString();
    }

    return new HammerVolumeOndisk {
      Valid = true,
      VolSignature = sig,
      VolBotBeg = ReadI64(image, 8),
      VolMemBeg = ReadI64(image, 16),
      VolBufBeg = ReadI64(image, 24),
      VolBufEnd = ReadI64(image, 32),
      VolFsidHex = fsidHex,
      VolFsTypeHex = fsTypeHex,
      VolLabel = label,
      VolNo = ReadI32(image, 144),
      VolCount = ReadI32(image, 148),
      VolVersion = ReadU32(image, 152),
      VolCrc = ReadU32(image, 156),
      VolFlags = ReadU32(image, 160),
      VolRootVol = ReadU32(image, 164),
      Vol0StatBigblocks = ReadI64(image, 200),
      Vol0StatFreeBigblocks = ReadI64(image, 208),
      Vol0StatInodes = ReadI64(image, 224),
      Vol0BtreeRoot = ReadI64(image, 240),
      Vol0NextTid = ReadI64(image, 248),
      HeaderRaw = raw,
    };
  }

  private static long ReadI64(ReadOnlySpan<byte> image, int off) =>
    off + 8 <= image.Length
      ? BinaryPrimitives.ReadInt64LittleEndian(image.Slice(off, 8))
      : 0;

  private static int ReadI32(ReadOnlySpan<byte> image, int off) =>
    off + 4 <= image.Length
      ? BinaryPrimitives.ReadInt32LittleEndian(image.Slice(off, 4))
      : 0;

  private static uint ReadU32(ReadOnlySpan<byte> image, int off) =>
    off + 4 <= image.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(off, 4))
      : 0u;
}

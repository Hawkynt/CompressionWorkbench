#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// Parses the OCFS2 (Oracle Cluster Filesystem 2) superblock. The superblock
/// is stored as the <c>id2.i_super</c> sub-structure of an
/// <c>ocfs2_dinode</c> at <c>OCFS2_SUPER_BLOCK_BLKNO = 2</c>. With a 4 KB
/// block size (the mkfs.ocfs2 default and far the most common), that block
/// starts at byte offset <c>0x2000</c> (8192). The dinode's
/// <c>i_signature[8]</c> at offset 0 holds the ASCII string
/// <c>OCFSV2</c> (NUL-padded to 8). The superblock fields begin at
/// <c>id2 = +0xC0</c> within the dinode block. Read-only.
///
/// Layout per <c>fs/ocfs2/ocfs2_fs.h</c>:
///
/// <code>
/// #define OCFS2_SUPER_BLOCK_SIGNATURE  "OCFSV2"
/// #define OCFS2_SUPER_BLOCK_BLKNO      2
/// #define OCFS2_MAX_VOL_LABEL_LEN      64
/// #define OCFS2_VOL_UUID_LEN           16
///
/// struct ocfs2_dinode {
///   /*00*/ __u8   i_signature[8];          // "OCFSV2\0\0"
///          __le32 i_generation;
///          __le16 i_suballoc_slot;
///          __le16 i_suballoc_bit;
///   /*10*/ ...
///   /*C0*/ union { struct ocfs2_super_block i_super; ... } id2;
/// };
///
/// struct ocfs2_super_block {        // located at dinode + 0xC0
///   /*00*/ __le16 s_major_rev_level;
///          __le16 s_minor_rev_level;
///          __le16 s_mnt_count;
///          __le16 s_max_mnt_count;
///          __le16 s_state;
///          __le16 s_errors;
///          __le32 s_checkinterval;
///   /*10*/ __le64 s_lastcheck;
///          __le32 s_creator_os;
///          __le32 s_feature_compat;
///   /*20*/ __le32 s_feature_incompat;
///          __le32 s_feature_ro_compat;
///          __le64 s_root_blkno;
///   /*30*/ __le64 s_system_dir_blkno;
///          __le32 s_blocksize_bits;
///          __le32 s_clustersize_bits;
///   /*40*/ __le16 s_max_slots;
///          __le16 s_tunefs_flag;
///          __le32 s_uuid_hash;
///          __le64 s_first_cluster_group;
///   /*50*/ __u8   s_label[64];
///   /*90*/ __u8   s_uuid[16];
/// };
/// </code>
///
/// Verified by:
/// <list type="bullet">
///   <item><description><c>https://github.com/torvalds/linux/blob/master/fs/ocfs2/ocfs2_fs.h</c></description></item>
/// </list>
/// </summary>
public sealed class Ocfs2Superblock {
  /// <summary>OCFS2_SUPER_BLOCK_SIGNATURE — 6 ASCII bytes at dinode offset 0.</summary>
  public static readonly byte[] SignatureBytes = "OCFSV2"u8.ToArray();

  /// <summary>OCFS2_SUPER_BLOCK_BLKNO — superblock lives at this *block* index, not byte.</summary>
  public const int SuperBlockBlkno = 2;

  /// <summary>Default mkfs.ocfs2 block size (and far the most common — many tools assume this).</summary>
  public const int DefaultBlockSize = 4096;

  /// <summary>Byte offset of the superblock dinode when blocksize = 4 KB (the default).</summary>
  public const long DefaultSuperBlockOffset = SuperBlockBlkno * DefaultBlockSize; // 0x2000 = 8192

  /// <summary>Bytes we surface as <c>superblock.bin</c> — one full 4 KB dinode block.</summary>
  public const int HeaderCaptureSize = 4096;

  /// <summary>Offset of <c>id2.i_super</c> within the dinode.</summary>
  public const int SuperOffsetInDinode = 0xC0;

  /// <summary>Plausible block sizes we'll try when scanning for the SB. OCFS2 supports 512 → 4096.</summary>
  internal static readonly int[] PlausibleBlockSizes = [512, 1024, 2048, 4096];

  /// <summary>True iff the OCFSV2 signature was found at a recognised block-2 offset.</summary>
  public bool Valid { get; private init; }

  /// <summary>Detected block size in bytes (matched offset / 2).</summary>
  public int DetectedBlockSize { get; private init; }

  /// <summary>Byte offset where the superblock dinode was found.</summary>
  public long SuperBlockOffset { get; private init; }

  public ushort MajorRev { get; private init; }
  public ushort MinorRev { get; private init; }
  public uint BlocksizeBits { get; private init; }
  public uint ClustersizeBits { get; private init; }
  public ushort MaxSlots { get; private init; }
  public ulong RootBlkno { get; private init; }
  public ulong SystemDirBlkno { get; private init; }
  public ulong FirstClusterGroup { get; private init; }
  public string Label { get; private init; } = "";
  public string UuidHex { get; private init; } = "";

  /// <summary>Raw 4 KB capture of the superblock dinode block (pad with 0 if image was shorter).</summary>
  public byte[] HeaderRaw { get; private init; } = [];

  /// <summary>
  /// Best-effort parse. Tries plausible block sizes (512, 1024, 2048, 4096) for
  /// the OCFSV2 signature at block 2; falls back to a free-form scan of the
  /// first 64 KB if none of the canonical offsets match. Never throws.
  /// </summary>
  public static Ocfs2Superblock TryParse(ReadOnlySpan<byte> image) {
    // Phase 1: try canonical block-2 offsets.
    foreach (var bs in PlausibleBlockSizes) {
      var off = (long)bs * SuperBlockBlkno;
      if (off + SignatureBytes.Length > image.Length) continue;
      if (!image.Slice((int)off, SignatureBytes.Length).SequenceEqual(SignatureBytes)) continue;
      return ParseAt(image, off, bs);
    }

    // Phase 2: free-form scan within first 64 KB. Catches off-default block
    // sizes and partition-aligned images. We intentionally don't go past the
    // bounded read cap.
    var scanLimit = Math.Min(image.Length, 64 * 1024);
    for (var i = 0; i + SignatureBytes.Length <= scanLimit; i += 512) {
      if (!image.Slice(i, SignatureBytes.Length).SequenceEqual(SignatureBytes)) continue;
      // Heuristic: derived blocksize = i / 2 if i is in our plausible set.
      var derived = i / SuperBlockBlkno;
      if (Array.IndexOf(PlausibleBlockSizes, derived) < 0)
        derived = DefaultBlockSize;
      return ParseAt(image, i, derived);
    }

    return new Ocfs2Superblock();
  }

  private static Ocfs2Superblock ParseAt(ReadOnlySpan<byte> image, long offset, int blockSize) {
    var blockLen = Math.Min(blockSize, image.Length - (int)offset);
    if (blockLen < SuperOffsetInDinode + 0xA0)
      // Block doesn't even hold the full superblock fields — surface only the magic match.
      return new Ocfs2Superblock {
        Valid = true,
        DetectedBlockSize = blockSize,
        SuperBlockOffset = offset,
        HeaderRaw = SafeCapture(image, offset, blockLen),
      };

    var sb = image.Slice((int)offset + SuperOffsetInDinode);

    var major = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(0x00, 2));
    var minor = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(0x02, 2));
    var rootBlkno = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x28, 8));
    var sysDirBlkno = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x30, 8));
    var blocksizeBits = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x38, 4));
    var clustersizeBits = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x3C, 4));
    var maxSlots = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(0x40, 2));
    var firstClusterGroup = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x48, 8));

    // s_label[64] @ +0x50 — NUL-trimmed ASCII.
    var labelSpan = sb.Slice(0x50, 64);
    var labelLen = labelSpan.IndexOf((byte)0);
    if (labelLen < 0) labelLen = 64;
    var label = labelLen == 0 ? "" : Encoding.ASCII.GetString(labelSpan[..labelLen]);

    // s_uuid[16] @ +0x90.
    var uuidHex = Convert.ToHexString(sb.Slice(0x90, 16));

    return new Ocfs2Superblock {
      Valid = true,
      DetectedBlockSize = blockSize,
      SuperBlockOffset = offset,
      MajorRev = major,
      MinorRev = minor,
      BlocksizeBits = blocksizeBits,
      ClustersizeBits = clustersizeBits,
      MaxSlots = maxSlots,
      RootBlkno = rootBlkno,
      SystemDirBlkno = sysDirBlkno,
      FirstClusterGroup = firstClusterGroup,
      Label = label,
      UuidHex = uuidHex,
      HeaderRaw = SafeCapture(image, offset, blockLen),
    };
  }

  private static byte[] SafeCapture(ReadOnlySpan<byte> image, long offset, int requested) {
    var cap = Math.Min(HeaderCaptureSize, requested);
    if (cap <= 0) return [];
    var avail = Math.Min(cap, image.Length - (int)offset);
    var buf = new byte[HeaderCaptureSize];
    if (avail > 0) image.Slice((int)offset, avail).CopyTo(buf);
    return buf;
  }
}

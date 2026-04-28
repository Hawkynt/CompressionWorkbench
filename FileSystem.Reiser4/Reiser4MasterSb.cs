#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Reiser4;

/// <summary>
/// Parses the Reiser4 master superblock at byte offset 65536 (16 * 4 KB) and the
/// adjacent format40 superblock that immediately follows it. Read-only.
///
/// Layout per <c>edward6/reiser4progs</c> <c>plugin/format/format40/format40.h</c>
/// and the kernel reiser4 source <c>plugin/disk_format/disk_format40.h</c>:
///
/// <code>
/// struct reiser4_master_sb {       // 52 bytes total at offset 65536
///   char     ms_magic[16];         // "ReIsEr4" (NUL-padded to 16)
///   uint16_t ms_format;            // disk-format plugin id (0 = format40)
///   uint16_t ms_blksize;           // 4096 typical
///   char     ms_uuid[16];          // pool UUID
///   char     ms_label[16];         // volume label
/// };
///
/// struct format40_super_t {        // 512 bytes at master_block + 1
///   uint64_t sb_block_count;       // offset  0
///   uint64_t sb_free_blocks;       // offset  8
///   uint64_t sb_root_block;        // offset 16
///   uint64_t sb_oid[2];            // offset 24/32
///   uint64_t sb_flushes;           // offset 40
///   uint32_t sb_mkfs_id;           // offset 48
///   char     sb_magic[16];         // offset 52  "ReIsEr40FoRmAt"
///   uint16_t sb_tree_height;       // offset 68
///   uint16_t sb_policy;            // offset 70
///   uint64_t sb_flags;             // offset 72
///   uint32_t sb_version;           // offset 80
///   uint32_t node_pid;             // offset 84
///   char     sb_unused[424];
/// };
/// </code>
///
/// Verified by:
/// <list type="bullet">
///   <item><description><c>https://github.com/edward6/reiser4/blob/master/vfs_ops.c</c> — <c>REISER4_MAGIC_OFFSET = 16 * 4096</c></description></item>
///   <item><description><c>https://github.com/edward6/reiser4progs/blob/master/plugin/format/format40/format40.h</c> — format40 layout + <c>"ReIsEr40FoRmAt"</c></description></item>
///   <item><description><c>fsck.reiser4</c> output: <c>"Master super block (16): magic: ReIsEr4 blksize: 4096 format: 0x0 (format40)"</c></description></item>
/// </list>
/// </summary>
public sealed class Reiser4MasterSb {
  /// <summary>Disk byte offset of the master superblock.</summary>
  public const long MasterOffset = 65536;

  /// <summary>Master SB size on disk (16 + 2 + 2 + 16 + 16).</summary>
  public const int MasterStructSize = 52;

  /// <summary>Reserved 480-byte "page-ish" slice we surface from the master block.</summary>
  public const int MasterRawCapture = 480;

  /// <summary>Format40 superblock size when present.</summary>
  public const int Format40StructSize = 480;

  /// <summary>"ReIsEr4" — 7 bytes, NUL-padded to 16. Master magic.</summary>
  public static readonly byte[] MasterMagic = "ReIsEr4"u8.ToArray();

  /// <summary>"ReIsEr40FoRmAt" — 14 bytes, NUL-padded to 16. Format40 SB magic.</summary>
  public static readonly byte[] Format40Magic = "ReIsEr40FoRmAt"u8.ToArray();

  /// <summary>True iff the master magic was recognised at offset 65536.</summary>
  public bool Valid { get; private init; }

  /// <summary>Disk-format plugin id (0 = format40).</summary>
  public ushort DiskPluginId { get; private init; }

  /// <summary>Filesystem block size in bytes (4096 typical).</summary>
  public ushort BlockSize { get; private init; }

  /// <summary>Pool UUID hex (32 hex chars, no dashes).</summary>
  public string UuidHex { get; private init; } = "";

  /// <summary>Volume label (NUL-trimmed, ASCII).</summary>
  public string Label { get; private init; } = "";

  /// <summary>Raw 480-byte capture from the master block (0-padded if image was shorter).</summary>
  public byte[] MasterRaw { get; private init; } = [];

  /// <summary>Whether a recognisable format40 superblock followed the master block.</summary>
  public bool Format40Present { get; private init; }

  /// <summary>Raw 480-byte capture of the format40 superblock (or empty).</summary>
  public byte[] Format40Raw { get; private init; } = [];

  // Format40 fields
  public ulong BlockCount { get; private init; }
  public ulong FreeBlocks { get; private init; }
  public ulong RootBlock { get; private init; }
  public ulong FileCount { get; private init; }
  public uint MkfsId { get; private init; }
  public ushort TreeHeight { get; private init; }
  public ushort Policy { get; private init; }
  public uint Format40Version { get; private init; }

  /// <summary>
  /// Best-effort parse. Never throws — invalid / short images return a sentinel
  /// instance with <see cref="Valid"/> == false.
  /// </summary>
  public static Reiser4MasterSb TryParse(ReadOnlySpan<byte> image) {
    // Bail before we touch indices that don't exist.
    if (image.Length < MasterOffset + MasterStructSize)
      return new Reiser4MasterSb();

    var master = image.Slice((int)MasterOffset);

    // Magic at master+0 — must start with "ReIsEr4". The remaining 9 bytes of
    // ms_magic[16] are usually NULs but real images sometimes pad with spaces;
    // we don't enforce them.
    if (!master[..MasterMagic.Length].SequenceEqual(MasterMagic))
      return new Reiser4MasterSb();

    var diskPlugin = BinaryPrimitives.ReadUInt16LittleEndian(master.Slice(16, 2));
    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(master.Slice(18, 2));

    // UUID 16 bytes at offset 20.
    var uuidBytes = master.Slice(20, 16);
    var uuidHex = Convert.ToHexString(uuidBytes);

    // Label 16 bytes at offset 36 — NUL-trim.
    var labelSpan = master.Slice(36, 16);
    var labelLen = labelSpan.IndexOf((byte)0);
    if (labelLen < 0) labelLen = 16;
    var label = labelLen == 0 ? "" : Encoding.ASCII.GetString(labelSpan[..labelLen]);

    // Raw capture — clamp to whatever's on disk; pad with 0 if the image is
    // shorter than 480 bytes from the master offset.
    var masterRaw = new byte[MasterRawCapture];
    var availMaster = Math.Min(MasterRawCapture, image.Length - (int)MasterOffset);
    if (availMaster > 0)
      image.Slice((int)MasterOffset, availMaster).CopyTo(masterRaw);

    // Format40 superblock lives in the next block. A 4 KB block size is by far
    // the dominant case; if blocksize is 0 / nonsensical, default to 4096 so we
    // still try to surface the next page.
    var bs = blockSize is 512 or 1024 or 2048 or 4096 or 8192 ? blockSize : (ushort)4096;
    var format40Offset = (long)MasterOffset + bs;

    var format40Raw = Array.Empty<byte>();
    var format40Present = false;
    ulong blockCount = 0, freeBlocks = 0, rootBlock = 0, fileCount = 0;
    uint mkfsId = 0;
    ushort treeHeight = 0, policy = 0;
    uint format40Version = 0;

    if (image.Length >= format40Offset + Format40StructSize) {
      var f40 = image.Slice((int)format40Offset, Format40StructSize);
      // sb_magic at offset 52.
      var maybeMagic = f40.Slice(52, Math.Min(Format40Magic.Length, f40.Length - 52));
      if (maybeMagic.SequenceEqual(Format40Magic)) {
        format40Present = true;
        blockCount = BinaryPrimitives.ReadUInt64LittleEndian(f40.Slice(0, 8));
        freeBlocks = BinaryPrimitives.ReadUInt64LittleEndian(f40.Slice(8, 8));
        rootBlock = BinaryPrimitives.ReadUInt64LittleEndian(f40.Slice(16, 8));
        // sb_oid[0] = root_dir_oid, sb_oid[1] = oid_max — surface oid_max as "FileCount" proxy.
        fileCount = BinaryPrimitives.ReadUInt64LittleEndian(f40.Slice(32, 8));
        mkfsId = BinaryPrimitives.ReadUInt32LittleEndian(f40.Slice(48, 4));
        treeHeight = BinaryPrimitives.ReadUInt16LittleEndian(f40.Slice(68, 2));
        policy = BinaryPrimitives.ReadUInt16LittleEndian(f40.Slice(70, 2));
        format40Version = BinaryPrimitives.ReadUInt32LittleEndian(f40.Slice(80, 4));
      }
      format40Raw = f40.ToArray();
    }

    return new Reiser4MasterSb {
      Valid = true,
      DiskPluginId = diskPlugin,
      BlockSize = blockSize,
      UuidHex = uuidHex,
      Label = label,
      MasterRaw = masterRaw,
      Format40Present = format40Present,
      Format40Raw = format40Raw,
      BlockCount = blockCount,
      FreeBlocks = freeBlocks,
      RootBlock = rootBlock,
      FileCount = fileCount,
      MkfsId = mkfsId,
      TreeHeight = treeHeight,
      Policy = policy,
      Format40Version = format40Version,
    };
  }
}

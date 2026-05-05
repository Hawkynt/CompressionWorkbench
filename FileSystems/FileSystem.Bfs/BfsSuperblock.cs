#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Bfs;

/// <summary>
/// Parsed BFS (BeOS / Haiku) disk_super_block. Fields are populated best-effort;
/// any field that can't be read keeps its default zero value. The caller decides
/// what to do with an invalid superblock.
/// </summary>
internal sealed class BfsSuperblock {
  public const uint Magic1 = 0x42465331;    // '1SFB' little-endian when read as u32
  public const uint Magic2 = 0xDD121031;
  public const uint Magic3 = 0x15B6830E;

  public string Name { get; init; } = "";
  public uint Magic1Value { get; init; }
  public uint FsByteOrder { get; init; }
  public uint BlockSize { get; init; }
  public uint BlockShift { get; init; }
  public long NumBlocks { get; init; }
  public long UsedBlocks { get; init; }
  public uint InodeSize { get; init; }
  public uint Magic2Value { get; init; }
  public uint BlocksPerAg { get; init; }
  public uint AgShift { get; init; }
  public uint NumAgs { get; init; }
  public uint Flags { get; init; }
  public long LogBlocksRunIno { get; init; }
  public long LogStart { get; init; }
  public long LogEnd { get; init; }
  public uint Magic3Value { get; init; }
  public long RootDirIno { get; init; }
  public long IndicesDirIno { get; init; }
  public int SuperblockOffset { get; init; }
  public bool Valid { get; init; }
  public byte[] RawBytes { get; init; } = [];

  /// <summary>
  /// Try parsing a BFS superblock. Checks offset 512 first, then offset 0.
  /// Returns a superblock with <see cref="Valid"/>=false if neither location holds a valid magic.
  /// </summary>
  public static BfsSuperblock TryParse(ReadOnlySpan<byte> image) {
    foreach (var offset in (int[])[512, 0]) {
      if (offset + 1024 > image.Length) continue;
      if (offset + 36 > image.Length) continue;
      var magic1 = BinaryPrimitives.ReadUInt32LittleEndian(image.Slice(offset + 32, 4));
      if (magic1 != Magic1) continue;
      return Parse(image, offset);
    }
    return new BfsSuperblock();
  }

  private static BfsSuperblock Parse(ReadOnlySpan<byte> image, int offset) {
    // Layout (all LE):
    //  0  name[32]
    // 32  magic1 u32
    // 36  fs_byte_order u32
    // 40  block_size u32
    // 44  block_shift u32
    // 48  num_blocks i64
    // 56  used_blocks i64
    // 64  inode_size u32
    // 68  magic2 u32
    // 72  blocks_per_ag u32
    // 76  ag_shift u32
    // 80  num_ags u32
    // 84  flags u32
    // 88  log_blocks_run (block_run: allocation_group u32 + start u16 + length u16 = 8 bytes) —
    //     treated here as i64 inode id for simplicity
    // 96  log_start i64
    //104  log_end i64
    //112  magic3 u32
    //116  root_dir_run (i64 id — 8 bytes)
    //124  indices_dir_run (i64 id — 8 bytes)
    // Total up to 132; BFS reserves 1024 bytes total.

    var name = Encoding.ASCII.GetString(image.Slice(offset, 32)).TrimEnd('\0', ' ');
    var raw = image.Slice(offset, Math.Min(1024, image.Length - offset)).ToArray();
    // If we got fewer than 1024 bytes, pad to 1024 for consistent output.
    if (raw.Length < 1024) {
      var padded = new byte[1024];
      raw.CopyTo(padded, 0);
      raw = padded;
    }

    return new BfsSuperblock {
      Name = name,
      Magic1Value = ReadU32(image, offset + 32),
      FsByteOrder = ReadU32(image, offset + 36),
      BlockSize = ReadU32(image, offset + 40),
      BlockShift = ReadU32(image, offset + 44),
      NumBlocks = ReadI64(image, offset + 48),
      UsedBlocks = ReadI64(image, offset + 56),
      InodeSize = ReadU32(image, offset + 64),
      Magic2Value = ReadU32(image, offset + 68),
      BlocksPerAg = ReadU32(image, offset + 72),
      AgShift = ReadU32(image, offset + 76),
      NumAgs = ReadU32(image, offset + 80),
      Flags = ReadU32(image, offset + 84),
      LogBlocksRunIno = ReadI64(image, offset + 88),
      LogStart = ReadI64(image, offset + 96),
      LogEnd = ReadI64(image, offset + 104),
      Magic3Value = ReadU32(image, offset + 112),
      RootDirIno = ReadI64(image, offset + 116),
      IndicesDirIno = ReadI64(image, offset + 124),
      SuperblockOffset = offset,
      Valid = true,
      RawBytes = raw,
    };
  }

  private static uint ReadU32(ReadOnlySpan<byte> s, int off) =>
    off + 4 <= s.Length ? BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(off, 4)) : 0u;

  private static long ReadI64(ReadOnlySpan<byte> s, int off) =>
    off + 8 <= s.Length ? BinaryPrimitives.ReadInt64LittleEndian(s.Slice(off, 8)) : 0L;
}

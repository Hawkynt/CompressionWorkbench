#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Wafl;

/// <summary>
/// Decoder for the block-pointer tree used by the published classic 128-byte
/// WAFL inode profile.
/// </summary>
/// <remarks>
/// The original WAFL patents define a 128-byte on-disk inode whose final 64
/// bytes are either inline data (level 0) or sixteen 32-bit VBNs at one common
/// indirection level. A 4 KiB indirect block contains 1024 32-bit VBNs. This
/// type implements only those published mechanics; it deliberately does not
/// guess where an inode lives inside fsinfo or where type/size/level fields are
/// packed in the inode metadata half.
/// </remarks>
internal static class WaflClassicBlockTree {
  internal const int InodeSize = 128;
  internal const int InodeMetadataSize = 64;
  internal const int InlineDataCapacity = 64;
  internal const int RootPointerCount = 16;
  internal const int PointerSize = sizeof(uint);
  internal const int PointersPerIndirectBlock = WaflReader.BlockSize / PointerSize;
  internal const int MaxLevel = 3;

  /// <summary>Copies level-0 inline payload bytes from the inode's final 64 bytes.</summary>
  internal static byte[] ReadInlineData(ReadOnlySpan<byte> inode, int length) {
    ValidateInode(inode);
    if ((uint)length > InlineDataCapacity)
      throw new ArgumentOutOfRangeException(nameof(length), length, $"Classic WAFL inline data is limited to {InlineDataCapacity} bytes.");

    return inode.Slice(InodeMetadataSize, length).ToArray();
  }

  /// <summary>
  /// Enumerates logical file blocks for a classic WAFL inode pointer tree.
  /// Zero pointers are represented as sparse holes. The caller supplies the
  /// already-decoded inode level and logical block count because their metadata
  /// byte offsets are not established by the public format material.
  /// </summary>
  internal static IEnumerable<WaflClassicDataBlock> EnumerateDataBlocks(
      ReadOnlySpan<byte> inode,
      int level,
      bool littleEndian,
      long logicalBlockCount,
      ulong volumeBlockCount,
      Func<uint, byte[]> readBlock) {
    ValidateInode(inode);
    ArgumentNullException.ThrowIfNull(readBlock);

    if ((uint)level > MaxLevel)
      throw new ArgumentOutOfRangeException(nameof(level), level, $"Classic WAFL inode levels are 0 through {MaxLevel}.");
    if (logicalBlockCount < 0)
      throw new ArgumentOutOfRangeException(nameof(logicalBlockCount));
    if (volumeBlockCount == 0)
      throw new ArgumentOutOfRangeException(nameof(volumeBlockCount));

    if (level == 0) {
      if (logicalBlockCount != 0)
        throw new InvalidDataException("WAFL: level-0 classic inode stores data inline and cannot describe logical data blocks.");
      return [];
    }

    var capacity = checked(RootPointerCount * SubtreeCapacity(level));
    if (logicalBlockCount > capacity)
      throw new InvalidDataException($"WAFL: {logicalBlockCount} logical blocks exceed level-{level} classic inode capacity {capacity}.");

    var rootPointers = new uint[RootPointerCount];
    var pointerArea = inode.Slice(InodeMetadataSize, InlineDataCapacity);
    for (var i = 0; i < rootPointers.Length; ++i)
      rootPointers[i] = ReadUInt32(pointerArea.Slice(i * PointerSize, PointerSize), littleEndian);

    return EnumerateRoots(rootPointers, level, littleEndian, logicalBlockCount, volumeBlockCount, readBlock);
  }

  private static IEnumerable<WaflClassicDataBlock> EnumerateRoots(
      uint[] rootPointers,
      int level,
      bool littleEndian,
      long logicalBlockCount,
      ulong volumeBlockCount,
      Func<uint, byte[]> readBlock) {
    var activeIndirectBlocks = new HashSet<uint>();
    var perRoot = SubtreeCapacity(level);
    var remaining = logicalBlockCount;
    long fileBlock = 0;

    foreach (var pointer in rootPointers) {
      if (remaining == 0)
        yield break;

      var count = Math.Min(remaining, perRoot);
      foreach (var block in EnumeratePointer(
                 pointer,
                 level,
                 fileBlock,
                 count,
                 littleEndian,
                 volumeBlockCount,
                 readBlock,
                 activeIndirectBlocks))
        yield return block;

      fileBlock += count;
      remaining -= count;
    }
  }

  private static IEnumerable<WaflClassicDataBlock> EnumeratePointer(
      uint pointer,
      int level,
      long firstFileBlock,
      long logicalBlockCount,
      bool littleEndian,
      ulong volumeBlockCount,
      Func<uint, byte[]> readBlock,
      HashSet<uint> activeIndirectBlocks) {
    if (logicalBlockCount == 0)
      yield break;

    if (pointer == 0) {
      for (long i = 0; i < logicalBlockCount; ++i)
        yield return new WaflClassicDataBlock(firstFileBlock + i, null);
      yield break;
    }

    ValidateVbn(pointer, volumeBlockCount);

    if (level == 1) {
      if (logicalBlockCount != 1)
        throw new InvalidDataException("WAFL: a level-1 classic inode pointer can describe only one logical block.");
      yield return new WaflClassicDataBlock(firstFileBlock, pointer);
      yield break;
    }

    if (!activeIndirectBlocks.Add(pointer))
      throw new InvalidDataException($"WAFL: classic block tree contains an indirect-block cycle at VBN {pointer}.");

    try {
      var block = readBlock(pointer);
      if (block is null || block.Length != WaflReader.BlockSize)
        throw new InvalidDataException($"WAFL: indirect VBN {pointer} did not yield exactly {WaflReader.BlockSize} bytes.");

      var childCapacity = SubtreeCapacity(level - 1);
      var remaining = logicalBlockCount;
      var fileBlock = firstFileBlock;

      for (var i = 0; i < PointersPerIndirectBlock && remaining > 0; ++i) {
        var childCount = Math.Min(remaining, childCapacity);
        var childPointer = ReadUInt32(block.AsSpan(i * PointerSize, PointerSize), littleEndian);

        foreach (var child in EnumeratePointer(
                   childPointer,
                   level - 1,
                   fileBlock,
                   childCount,
                   littleEndian,
                   volumeBlockCount,
                   readBlock,
                   activeIndirectBlocks))
          yield return child;

        fileBlock += childCount;
        remaining -= childCount;
      }
    } finally {
      activeIndirectBlocks.Remove(pointer);
    }
  }

  private static long SubtreeCapacity(int level)
    => level switch {
      1 => 1,
      2 => PointersPerIndirectBlock,
      3 => (long)PointersPerIndirectBlock * PointersPerIndirectBlock,
      _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

  private static void ValidateInode(ReadOnlySpan<byte> inode) {
    if (inode.Length != InodeSize)
      throw new ArgumentException($"Classic WAFL inode must be exactly {InodeSize} bytes.", nameof(inode));
  }

  private static void ValidateVbn(uint vbn, ulong volumeBlockCount) {
    if ((ulong)vbn >= volumeBlockCount)
      throw new InvalidDataException($"WAFL: VBN {vbn} lies outside a logical image containing {volumeBlockCount} complete blocks.");
  }

  private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian)
    => littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
      : BinaryPrimitives.ReadUInt32BigEndian(bytes);
}

/// <summary>A logical file block reached through a classic WAFL block-pointer tree.</summary>
internal readonly record struct WaflClassicDataBlock(long FileBlockNumber, uint? Vbn) {
  internal bool IsHole => this.Vbn is null;
}

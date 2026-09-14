#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Wafl;

/// <summary>Byte-precise decoders for the allocation maps disclosed for classic WAFL.</summary>
internal static class WaflClassicAllocationMaps {
  internal const int BlockMapEntrySize = sizeof(uint);
  internal const int InodesPerInodeFileBlock = WaflReader.BlockSize / WaflClassicBlockTree.InodeSize;

  private const uint ActiveFileSystemMask = 1u << 0;
  private const uint SnapshotMask = 0x000F_FFFEu; // bits 1..20
  private const uint ReservedMask = 0x7FE0_0000u; // bits 21..30
  private const uint ConsistencyPointMask = 1u << 31;

  /// <summary>Decodes one classic 32-bit block-map entry.</summary>
  internal static WaflClassicBlockMapEntry ReadBlockMapEntry(ReadOnlySpan<byte> bytes, bool littleEndian) {
    if (bytes.Length != BlockMapEntrySize)
      throw new ArgumentException($"Classic WAFL block-map entry must be exactly {BlockMapEntrySize} bytes.", nameof(bytes));

    var raw = littleEndian
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
      : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    return new WaflClassicBlockMapEntry(
      raw,
      (raw & ActiveFileSystemMask) != 0,
      (raw & SnapshotMask) >> 1,
      (raw & ReservedMask) >> 21,
      (raw & ConsistencyPointMask) != 0);
  }

  /// <summary>Decodes a contiguous sequence of classic block-map entries.</summary>
  internal static WaflClassicBlockMapEntry[] ReadBlockMap(ReadOnlySpan<byte> bytes, bool littleEndian) {
    if (bytes.Length % BlockMapEntrySize != 0)
      throw new InvalidDataException($"WAFL: classic block-map length {bytes.Length} is not divisible by {BlockMapEntrySize}.");

    var result = new WaflClassicBlockMapEntry[bytes.Length / BlockMapEntrySize];
    for (var i = 0; i < result.Length; ++i)
      result[i] = ReadBlockMapEntry(bytes.Slice(i * BlockMapEntrySize, BlockMapEntrySize), littleEndian);
    return result;
  }

  /// <summary>
  /// Decodes one classic inode-map byte. The patent defines it as the number of
  /// allocated 128-byte inodes in the corresponding 4 KiB inode-file block.
  /// </summary>
  internal static WaflClassicInodeMapEntry ReadInodeMapEntry(byte allocatedInodes) {
    if (allocatedInodes > InodesPerInodeFileBlock)
      throw new InvalidDataException($"WAFL: inode-map value {allocatedInodes} exceeds {InodesPerInodeFileBlock} classic inodes per 4 KiB inode-file block.");

    return new WaflClassicInodeMapEntry(allocatedInodes, InodesPerInodeFileBlock - allocatedInodes);
  }

  /// <summary>Decodes a contiguous classic inode-map byte sequence.</summary>
  internal static WaflClassicInodeMapEntry[] ReadInodeMap(ReadOnlySpan<byte> bytes) {
    var result = new WaflClassicInodeMapEntry[bytes.Length];
    for (var i = 0; i < result.Length; ++i)
      result[i] = ReadInodeMapEntry(bytes[i]);
    return result;
  }
}

/// <summary>Decoded classic WAFL blkmap entry.</summary>
internal readonly record struct WaflClassicBlockMapEntry(
    uint RawValue,
    bool InActiveFileSystem,
    uint SnapshotMask,
    uint ReservedBits,
    bool ConsistencyPointBit) {
  /// <summary>
  /// The classic patent declares a block free only when all 32 allocation bits
  /// are zero, including active-file-system, snapshot and consistency-point bits.
  /// </summary>
  internal bool IsFree => this.RawValue == 0;

  internal bool IsReferencedBySnapshot(int snapshotIndex) {
    if ((uint)snapshotIndex >= 20)
      throw new ArgumentOutOfRangeException(nameof(snapshotIndex), snapshotIndex, "Classic WAFL blkmap exposes snapshot bits 1 through 20.");
    return (this.SnapshotMask & (1u << snapshotIndex)) != 0;
  }
}

/// <summary>Decoded classic WAFL inomap entry for one 4 KiB inode-file block.</summary>
internal readonly record struct WaflClassicInodeMapEntry(int AllocatedInodes, int FreeInodes) {
  internal bool IsEmpty => this.AllocatedInodes == 0;
  internal bool IsFull => this.FreeInodes == 0;
}

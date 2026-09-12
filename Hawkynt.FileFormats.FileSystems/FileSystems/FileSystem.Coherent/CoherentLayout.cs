#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Coherent;

/// <summary>
/// Walks a Coherent volume's inodes and notes every block a file occupies,
/// together with the three bytes that name it.
/// </summary>
/// <remarks>
/// A zone address is three bytes in the order a PDP-11 wrote them — the high
/// byte first, then the low two little-endian — and a file's length is a
/// 32-bit number stored as two 16-bit halves, high half first. Both are read
/// here the way <see cref="CoherentReader" /> reads them, so what this
/// describes and what that extracts are the same volume.
/// </remarks>
internal static class CoherentLayout {

  private const int InodeSize = 64;
  private const int DirectZones = 10;
  private const int ZoneCount = 13;

  /// <summary>One block a file occupies, and where the pointer naming it sits.</summary>
  /// <param name="Block">The block itself.</param>
  /// <param name="PointerOffset">Absolute offset of the three bytes that name it.</param>
  /// <param name="Owner">The file it belongs to.</param>
  /// <param name="IsIndirect">Whether the block holds pointers rather than payload.</param>
  internal readonly record struct BlockPointer(
    uint Block, long PointerOffset, string Owner, bool IsIndirect);

  /// <summary>What a volume is made of.</summary>
  internal sealed class Layout {
    public int BlockSize { get; init; }
    public long InodeTableOffset { get; init; }

    /// <summary>Lowest byte a file's block occupies, which is where the head ends.</summary>
    public long FirstDataOffset { get; set; }

    public List<BlockPointer> Pointers { get; } = [];
  }

  /// <summary>Walks the volume, or returns null when it is not one this reads.</summary>
  public static Layout? Read(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || image.Length < 1024 + InodeSize) return null;

    List<CoherentEntry> entries;
    int blockSize;
    try {
      image.Position = 0;
      var reader = new CoherentReader(image);
      entries = reader.Entries.Where(e => !e.IsDirectory).ToList();
      blockSize = reader.BlockSize;
    } catch {
      return null;
    }

    if (blockSize <= 0) return null;

    var inodeTableOffset = 2L * blockSize;
    var layout = new Layout {
      BlockSize = blockSize,
      InodeTableOffset = inodeTableOffset,
      FirstDataOffset = inodeTableOffset,
    };

    var lowest = long.MaxValue;
    foreach (var entry in entries) {
      var inode = ReadInode(image, inodeTableOffset, (uint)entry.InodeNumber);
      if (inode == null) continue;

      var size = ReadPdp32(inode.AsSpan(8));
      long remaining = size;

      for (var i = 0; i < DirectZones && remaining > 0; ++i) {
        var at = inodeTableOffset + (long)(entry.InodeNumber - 1) * InodeSize + 12 + i * 3;
        var block = Read24(inode.AsSpan(12 + i * 3));
        if (block == 0) break;

        layout.Pointers.Add(new BlockPointer(block, at, entry.Name, IsIndirect: false));
        lowest = Math.Min(lowest, (long)block * blockSize);
        remaining -= Math.Min(remaining, blockSize);
      }

      for (var level = 1; level <= 3 && remaining > 0; ++level) {
        var slot = DirectZones + level - 1;
        if (slot >= ZoneCount) break;

        var at = inodeTableOffset + (long)(entry.InodeNumber - 1) * InodeSize + 12 + slot * 3;
        var block = Read24(inode.AsSpan(12 + slot * 3));
        if (block == 0) continue;

        layout.Pointers.Add(new BlockPointer(block, at, entry.Name, IsIndirect: true));
        lowest = Math.Min(lowest, (long)block * blockSize);
        WalkIndirect(image, layout, block, level, entry.Name, blockSize, ref remaining, ref lowest);
      }
    }

    layout.FirstDataOffset = lowest == long.MaxValue ? inodeTableOffset : lowest;
    return layout;
  }

  private static void WalkIndirect(Stream image, Layout layout, uint block, int level, string owner,
      int blockSize, ref long remaining, ref long lowest) {
    if (block == 0 || remaining <= 0) return;

    var at = (long)block * blockSize;
    if (at < 0 || at + blockSize > image.Length) return;

    var bytes = new byte[blockSize];
    image.Position = at;
    image.ReadExactly(bytes);

    for (var i = 0; i < blockSize / 3 && remaining > 0; ++i) {
      var pointer = Read24(bytes.AsSpan(i * 3));
      if (pointer == 0) break;

      var pointerAt = at + i * 3;
      if (level == 1) {
        layout.Pointers.Add(new BlockPointer(pointer, pointerAt, owner, IsIndirect: false));
        lowest = Math.Min(lowest, (long)pointer * blockSize);
        remaining -= Math.Min(remaining, blockSize);
        continue;
      }

      layout.Pointers.Add(new BlockPointer(pointer, pointerAt, owner, IsIndirect: true));
      lowest = Math.Min(lowest, (long)pointer * blockSize);
      WalkIndirect(image, layout, pointer, level - 1, owner, blockSize, ref remaining, ref lowest);
    }
  }

  private static byte[]? ReadInode(Stream image, long inodeTableOffset, uint number) {
    if (number == 0) return null;
    var at = inodeTableOffset + (long)(number - 1) * InodeSize;
    if (at < 0 || at + InodeSize > image.Length) return null;

    var inode = new byte[InodeSize];
    image.Position = at;
    image.ReadExactly(inode);
    return inode;
  }

  /// <summary>A zone address: the high byte first, then the low two little-endian.</summary>
  internal static uint Read24(ReadOnlySpan<byte> bytes) =>
    bytes[1] | ((uint)bytes[2] << 8) | ((uint)bytes[0] << 16);

  /// <summary>Writes a zone address the same way round.</summary>
  internal static void Write24(Span<byte> bytes, uint block) {
    bytes[0] = (byte)(block >> 16);
    bytes[1] = (byte)block;
    bytes[2] = (byte)(block >> 8);
  }

  /// <summary>A 32-bit number stored as two 16-bit halves, high half first.</summary>
  private static uint ReadPdp32(ReadOnlySpan<byte> bytes) =>
    bytes[2] | ((uint)bytes[3] << 8) | ((uint)bytes[0] << 16) | ((uint)bytes[1] << 24);
}

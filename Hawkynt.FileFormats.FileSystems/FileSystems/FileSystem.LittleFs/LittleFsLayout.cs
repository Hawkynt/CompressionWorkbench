#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.LittleFs.LittleFsFormat;

namespace FileSystem.LittleFs;

/// <summary>
/// Finds, for every file, the blocks it is made of and the two places its
/// position is written down.
/// </summary>
/// <remarks>
/// <para>A file's blocks are a skip-list threaded backwards through the blocks
/// themselves: block <c>i</c> opens with pointers to <c>i-1</c>, <c>i-2</c>,
/// <c>i-4</c> and so on, as many as the trailing zeros of <c>i</c> allow. So
/// the pointers to a block live inside other blocks of the same file — nothing
/// outside names it except the head.</para>
///
/// <para>The head is the last block, and it is named by a tag inside a metadata
/// pair, which is a log of commits with a checksum over each. Changing it means
/// rewriting that commit's checksum as well.</para>
/// </remarks>
internal static class LittleFsLayout {

  /// <summary>One file, and everything a move of its blocks has to touch.</summary>
  /// <param name="Path">The file, for naming a run.</param>
  /// <param name="Blocks">Its blocks, in file order.</param>
  /// <param name="HeadField">Where the tag naming its last block sits.</param>
  /// <param name="MetadataBlock">The metadata block holding that tag.</param>
  internal readonly record struct FileChain(
    string Path, IReadOnlyList<uint> Blocks, long HeadField, long MetadataBlock);

  /// <summary>What a volume is made of.</summary>
  internal sealed class Layout {
    public uint BlockSize { get; init; }
    public List<uint> MetadataBlocks { get; } = [];
    public List<FileChain> Files { get; } = [];
  }

  /// <summary>Walks the volume, or returns null when it is not one this reads.</summary>
  public static Layout? Read(Stream image) {
    ArgumentNullException.ThrowIfNull(image);

    List<LittleFsFileEntry> files;
    uint blockSize;
    List<uint> metadata;
    try {
      image.Position = 0;
      using var reader = new LittleFsReader(image);
      blockSize = reader.BlockSize;
      metadata = reader.MetadataBlocks.ToList();
      files = reader.Files.Where(f => f.IsCtz && f.Size > 0).ToList();
    } catch {
      return null;
    }

    if (blockSize == 0) return null;

    var layout = new Layout { BlockSize = blockSize };
    layout.MetadataBlocks.AddRange(metadata);

    foreach (var file in files) {
      var blocks = Chain(image, blockSize, file.CtzHead, (uint)file.Size);
      if (blocks.Count == 0) continue;

      var (field, block) = FindHeadField(image, blockSize, metadata, file.CtzHead, (uint)file.Size);
      if (field < 0) continue;

      layout.Files.Add(new FileChain(file.Path, blocks, field, block));
    }

    return layout;
  }

  /// <summary>The blocks of a file, in file order, as the skip-list threads them.</summary>
  internal static List<uint> Chain(Stream image, uint blockSize, uint head, uint size) {
    var indices = new List<uint>();
    var count = BlockCount(size, blockSize);
    var current = head;

    for (var i = (int)count - 1; i >= 0; --i) {
      indices.Add(current);
      if (i == 0) break;

      var at = (long)current * blockSize;
      if (at + 4 > image.Length) break;

      var pointer = new byte[4];
      image.Position = at;
      image.ReadExactly(pointer);
      current = BinaryPrimitives.ReadUInt32LittleEndian(pointer);
    }

    indices.Reverse();
    return indices;
  }

  /// <summary>How many blocks a file of this length occupies.</summary>
  internal static uint BlockCount(uint size, uint blockSize) {
    uint blocks = 0, written = 0;
    var i = 0;
    while (written < size) {
      var pointers = i == 0 ? 0 : TrailingZeros((uint)i) + 1;
      var room = blockSize - (uint)pointers * 4;
      if (room == 0) break;
      written += Math.Min(room, size - written);
      ++blocks;
      ++i;
    }

    return blocks == 0 ? 1 : blocks;
  }

  /// <summary>How many pointers block <paramref name="index" /> opens with.</summary>
  internal static int PointerCount(int index) => index == 0 ? 0 : TrailingZeros((uint)index) + 1;

  internal static int TrailingZeros(uint value) {
    if (value == 0) return 32;
    var count = 0;
    while ((value & 1) == 0) { value >>= 1; ++count; }
    return count;
  }

  /// <summary>
  /// Finds the tag that names a file's head block, and the block it lives in.
  /// </summary>
  private static (long Field, long Block) FindHeadField(Stream image, uint blockSize,
      IEnumerable<uint> metadataBlocks, uint head, uint size) {
    foreach (var block in metadataBlocks) {
      var at = (long)block * blockSize;
      if (at < 0 || at + blockSize > image.Length) continue;

      var bytes = new byte[blockSize];
      image.Position = at;
      image.ReadExactly(bytes);

      foreach (var (tag, dataAt) in Tags(bytes)) {
        if (TagType(tag) != TypeCtzStruct || TagLength(tag) < 8) continue;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataAt)) != head) continue;
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(dataAt + 4)) != size) continue;

        return (at + dataAt, at);
      }
    }

    return (-1, -1);
  }

  /// <summary>Every tag of a commit block, with where its data sits.</summary>
  internal static IEnumerable<(uint Tag, int DataOffset)> Tags(byte[] block) {
    var offset = 4;
    var previous = 0xFFFFFFFFu;

    while (offset + 4 <= block.Length) {
      var onDisk = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(offset));
      var tag = onDisk ^ previous;
      var length = TagLength(tag);
      if (length == LengthMax) yield break;
      if (offset + 4 + length > block.Length) yield break;

      yield return (tag, offset + 4);
      if (TagType(tag) == TypeCrc) yield break;

      offset += 4 + (int)length;
      previous = tag;
    }
  }

  /// <summary>
  /// Takes a commit's checksum again, over everything up to and including the
  /// tag that carries it.
  /// </summary>
  internal static void RestampCommit(byte[] block) {
    var offset = 4;
    var previous = 0xFFFFFFFFu;

    while (offset + 4 <= block.Length) {
      var onDisk = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(offset));
      var tag = onDisk ^ previous;
      var length = TagLength(tag);
      if (length == LengthMax) return;
      if (offset + 4 + length > block.Length) return;

      if (TagType(tag) == TypeCrc) {
        var crc = Crc(0xFFFFFFFFu, block.AsSpan(0, offset + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 4), crc);
        return;
      }

      offset += 4 + (int)length;
      previous = tag;
    }
  }
}

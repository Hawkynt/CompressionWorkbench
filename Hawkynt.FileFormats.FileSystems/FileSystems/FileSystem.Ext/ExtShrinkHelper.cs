#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ext;

/// <summary>
/// Shrinks an ext2/3/4 filesystem image by defragmenting (consolidate at start) and
/// then truncating trailing free blocks. Updates the superblock s_blocks_count and
/// the BGD free-block count to reflect the reduced geometry.
/// </summary>
public static class ExtShrinkHelper {

  /// <summary>
  /// Result of an ext shrink operation: original and new sizes, plus whether the
  /// image was actually reduced.
  /// </summary>
  public sealed record ShrinkResult(long OriginalSize, long NewSize, bool WasReduced);

  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;

  /// <summary>
  /// Shrinks an ext2/3/4 image by extracting all files, then rebuilding with a
  /// minimal total-blocks count, and finally updating the superblock metadata.
  /// This is simpler and more reliable than defrag-then-truncate because the
  /// ExtWriter always produces a tightly-packed image.
  /// </summary>
  /// <param name="image">Readable/writable/seekable stream containing the ext image.</param>
  /// <returns>Shrink result with before/after sizes.</returns>
  public static ShrinkResult Shrink(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var originalSize = image.Length;

    // Step 1: Read the superblock to determine block size
    image.Position = 0;
    var headerBuf = new byte[SuperblockOffset + 264];
    if (image.Length < headerBuf.Length)
      throw new InvalidDataException("ext: image too small for superblock.");
    image.ReadExactly(headerBuf);

    var sbSpan = headerBuf.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sbSpan[56..]);
    if (magic != ExtMagic)
      throw new InvalidDataException($"ext: invalid magic 0x{magic:X4}, expected 0xEF53.");

    var logBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(sbSpan[24..]);
    var blockSize = 1024 << (int)logBlockSize;
    var currentBlocks = BinaryPrimitives.ReadUInt32LittleEndian(sbSpan[4..]);

    // Step 2: Extract all files from the image
    image.Position = 0;
    var reader = new ExtReader(image);
    var files = reader.Entries
      .Where(e => !e.IsDirectory)
      .Select(e => (e.Name, Data: reader.Extract(e)))
      .ToList();

    // Step 3: Compute the minimum number of blocks needed
    // Metadata overhead: superblock(1) + BGD(1) + block_bitmap(1) + inode_bitmap(1)
    //   + inode_table(variable) + root_dir(1)
    var firstDataBlock = blockSize == 1024 ? 1 : 0;
    const int inodeSize = 128;
    const int inodesPerGroup = 128;
    var inodeTableBlocks = (inodesPerGroup * inodeSize + blockSize - 1) / blockSize;
    var metadataBlocks = firstDataBlock + 4 + inodeTableBlocks + 1; // +1 for root dir
    var dataBlocks = 0;
    foreach (var (_, data) in files)
      dataBlocks += data.Length > 0 ? (data.Length + blockSize - 1) / blockSize : 0;
    // Add 2 blocks of headroom for metadata growth
    var minBlocks = metadataBlocks + dataBlocks + 2;

    if (minBlocks >= (int)currentBlocks)
      return new ShrinkResult(originalSize, originalSize, false);

    // Step 4: Rebuild the image with the minimal block count
    var w = new ExtWriter();
    foreach (var (name, data) in files)
      w.AddFile(name, data);
    var rebuilt = w.Build(blockSize: blockSize, totalBlocks: minBlocks);

    // Step 5: Write the rebuilt image back
    image.Position = 0;
    image.Write(rebuilt);
    image.SetLength(rebuilt.Length);

    return new ShrinkResult(originalSize, rebuilt.Length, true);
  }
}

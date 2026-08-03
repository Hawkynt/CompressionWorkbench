#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Where an APFS container keeps its own structures, and where each file's
/// extent record says its bytes are.
/// </summary>
/// <remarks>
/// <para>The container's blocks are not all at the front. Every change made in
/// place allocates from the image's tail — new B-tree nodes, new object map
/// entries — so a volume that has been written to since it was made has its
/// trees scattered past the file data. Anything that treats "past the last
/// file" as free space is therefore writing over the map of the volume.</para>
///
/// <para>A file's position is one field: <c>phys_block_num</c> in its file
/// extent record, in a leaf of the filesystem tree. Each block carries its own
/// Fletcher-64, so rewriting that field means rewriting one leaf's checksum and
/// nothing else — no tree rebuild, and no growth.</para>
/// </remarks>
internal static class ApfsLayout {

  private const int BtnHeaderEnd = 56;
  private const int BtreeInfoSize = 40;
  private const int TocEntrySize = 8;

  /// <summary>Where one file extent record sits, and what it currently names.</summary>
  /// <param name="PhysBlock">The block the record says the file's bytes start at.</param>
  /// <param name="LengthBytes">How many bytes the extent covers.</param>
  /// <param name="LeafBlock">The leaf the record lives in.</param>
  /// <param name="FieldOffset">Absolute offset of the record's block-number field.</param>
  internal readonly record struct Extent(
    ulong PhysBlock, ulong LengthBytes, ulong LeafBlock, long FieldOffset);

  /// <summary>What a container is made of.</summary>
  internal sealed class Container {
    public uint BlockSize { get; init; }
    public long ImageLength { get; init; }

    /// <summary>Lowest block a file's bytes occupy, which is where the head ends.</summary>
    public ulong FirstDataBlock =>
      this.Extents.Count == 0 ? 12 : this.Extents.Min(e => e.PhysBlock);

    /// <summary>Every block the container's own structures occupy.</summary>
    public HashSet<ulong> MetadataBlocks { get; } = [];

    /// <summary>Every file extent record the filesystem tree holds.</summary>
    public List<Extent> Extents { get; } = [];
  }

  /// <summary>Walks the container, or returns null when it is not one this reads.</summary>
  /// <remarks>
  /// Block by block rather than in one gulp: a container is half a gigabyte
  /// before it holds anything, and this is on the path a wipe and the layout
  /// view both take.
  /// </remarks>
  public static Container? Read(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || image.Length < 12 * DEFAULT_BLOCK_SIZE) return null;

    var blockSize = DEFAULT_BLOCK_SIZE;
    var superblock = ReadBlock(image, 0, blockSize);
    if (superblock == null) return null;
    if (BinaryPrimitives.ReadUInt32LittleEndian(superblock.AsSpan(32)) != 0x4253584EU) return null;

    var containerOmap = BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(3072));
    if (containerOmap == 0) return null;

    var container = new Container { BlockSize = blockSize, ImageLength = image.Length };

    // The superblock and the copy the format keeps beside it. The rest of the
    // head — the checkpoint area — is covered by the reserve in front of the
    // first file; what matters here is everything the trees reach, wherever it
    // ended up.
    container.MetadataBlocks.Add(0);
    container.MetadataBlocks.Add(1);
    container.MetadataBlocks.Add(2);
    container.MetadataBlocks.Add(containerOmap);

    var omap = ReadBlock(image, containerOmap, blockSize);
    if (omap == null) return null;
    var containerOmapTree = BinaryPrimitives.ReadUInt64LittleEndian(omap.AsSpan(48));
    CollectNodes(image, blockSize, containerOmapTree, container.MetadataBlocks);

    var volumeSuperblockOid = BinaryPrimitives.ReadUInt64LittleEndian(superblock.AsSpan(184));
    var volumeSuperblock = ResolveOid(image, blockSize, containerOmapTree, volumeSuperblockOid);
    if (volumeSuperblock == 0) return null;
    container.MetadataBlocks.Add(volumeSuperblock);

    var apsb = ReadBlock(image, volumeSuperblock, blockSize);
    if (apsb == null) return null;
    var volumeOmap = BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(392));
    var fsTreeOid = BinaryPrimitives.ReadUInt64LittleEndian(apsb.AsSpan(400));
    container.MetadataBlocks.Add(volumeOmap);

    var volumeOmapBlock = ReadBlock(image, volumeOmap, blockSize);
    if (volumeOmapBlock == null) return null;
    var volumeOmapTree = BinaryPrimitives.ReadUInt64LittleEndian(volumeOmapBlock.AsSpan(48));
    CollectNodes(image, blockSize, volumeOmapTree, container.MetadataBlocks);

    var fsTree = ResolveOid(image, blockSize, volumeOmapTree, fsTreeOid);
    if (fsTree == 0) return null;
    CollectNodes(image, blockSize, fsTree, container.MetadataBlocks);

    CollectFileExtents(image, blockSize, fsTree, container.Extents);
    return container;
  }

  /// <summary>One block of the image, or null when it is not there.</summary>
  private static byte[]? ReadBlock(Stream image, ulong block, uint blockSize) {
    var at = (long)block * blockSize;
    if (at < 0 || at + blockSize > image.Length) return null;

    var bytes = new byte[blockSize];
    image.Position = at;
    image.ReadExactly(bytes);
    return bytes;
  }

  /// <summary>
  /// The physical block an object map gives for a virtual object id. Where an
  /// id appears more than once, the newest transaction wins.
  /// </summary>
  private static ulong ResolveOid(Stream image, uint blockSize, ulong tree, ulong oid) {
    var pending = new Stack<(ulong Block, bool IsRoot)>();
    var visited = new HashSet<ulong>();
    pending.Push((tree, true));

    ulong best = 0, bestXid = 0;
    while (pending.Count > 0) {
      var (block, isRoot) = pending.Pop();
      if (!visited.Add(block)) continue;

      var node = ReadBlock(image, block, blockSize);
      if (node == null) continue;
      var level = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(34));

      foreach (var (keyAt, keyLength, valueAt, valueLength) in Slots(node, isRoot)) {
        if (level > 0) {
          if (valueLength >= 8)
            pending.Push((BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(valueAt)), false));
          continue;
        }

        if (keyLength < 16 || valueLength < 16) continue;
        if (BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(keyAt)) != oid) continue;

        var xid = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(keyAt + 8));
        if (best != 0 && xid < bestXid) continue;
        best = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(valueAt + 8));
        bestXid = xid;
      }
    }

    return best;
  }

  /// <summary>Adds every node of the tree rooted here to <paramref name="blocks" />.</summary>
  private static void CollectNodes(Stream image, uint blockSize, ulong root, HashSet<ulong> blocks) {
    var pending = new Stack<(ulong Block, bool IsRoot)>();
    pending.Push((root, true));

    while (pending.Count > 0) {
      var (block, isRoot) = pending.Pop();
      if (!blocks.Add(block)) continue;

      var node = ReadBlock(image, block, blockSize);
      if (node == null) continue;
      if (BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(34)) == 0) continue;   // a leaf

      foreach (var (_, _, valueAt, valueLength) in Slots(node, isRoot)) {
        if (valueLength < 8) continue;
        pending.Push((BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(valueAt)), false));
      }
    }
  }

  /// <summary>Notes every file extent record in the tree, and where its field sits.</summary>
  private static void CollectFileExtents(Stream image, uint blockSize, ulong root, List<Extent> extents) {
    var pending = new Stack<(ulong Block, bool IsRoot)>();
    var visited = new HashSet<ulong>();
    pending.Push((root, true));

    while (pending.Count > 0) {
      var (block, isRoot) = pending.Pop();
      if (!visited.Add(block)) continue;

      var node = ReadBlock(image, block, blockSize);
      if (node == null) continue;
      var at = (long)block * blockSize;
      var level = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(34));

      foreach (var (keyAt, keyLength, valueAt, valueLength) in Slots(node, isRoot)) {
        if (level > 0) {
          if (valueLength >= 8)
            pending.Push((BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(valueAt)), false));
          continue;
        }

        if (keyLength < 16 || valueLength < 16) continue;
        var oid = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(keyAt));
        if ((oid >> 60) != APFS_TYPE_FILE_EXTENT) continue;

        var length = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(valueAt)) & 0x00FFFFFFFFFFFFFFUL;
        var physical = BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(valueAt + 8));
        if (physical == 0) continue;

        extents.Add(new Extent(physical, length, block, at + valueAt + 8));
      }
    }
  }

  /// <summary>
  /// Where each slot's key and value sit inside a node, as the format lays them
  /// out: a table of contents after the header, keys after that, and values
  /// measured back from the end.
  /// </summary>
  private static List<(int KeyAt, int KeyLength, int ValueAt, int ValueLength)> Slots(
      byte[] nodeBytes, bool isRoot) {
    var node = nodeBytes.AsSpan();
    var slots = new List<(int, int, int, int)>();
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(node[32..]);
    var keyCount = BinaryPrimitives.ReadUInt32LittleEndian(node[36..]);
    var tableOffset = BinaryPrimitives.ReadUInt16LittleEndian(node[40..]);
    var tableLength = BinaryPrimitives.ReadUInt16LittleEndian(node[42..]);

    var table = BtnHeaderEnd + tableOffset;
    var keyArea = table + tableLength;
    var valueArea = isRoot || (flags & BTNODE_ROOT) != 0 ? node.Length - BtreeInfoSize : node.Length;
    if ((flags & BTNODE_FIXED_KV_SIZE) != 0) return slots;   // not a shape this walks

    for (var i = 0u; i < keyCount; ++i) {
      var entry = table + (int)i * TocEntrySize;
      if (entry + TocEntrySize > node.Length) break;

      var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(node[entry..]);
      var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(node[(entry + 2)..]);
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(node[(entry + 4)..]);
      var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(node[(entry + 6)..]);
      if (keyLength <= 0 || valueLength <= 0) continue;

      var keyAt = keyArea + keyOffset;
      var valueAt = valueArea - valueOffset;
      if (keyAt + keyLength > node.Length || valueAt + valueLength > node.Length) continue;
      slots.Add((keyAt, keyLength, valueAt, valueLength));
    }

    return slots;
  }
}

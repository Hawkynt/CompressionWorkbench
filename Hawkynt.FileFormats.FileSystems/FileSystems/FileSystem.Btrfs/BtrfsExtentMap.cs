#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Btrfs;

/// <summary>
/// Walks a Btrfs image (single-device, non-RAID) and yields its actual
/// on-disk byte layout. Targets the WORM-minimal writer profile: a single
/// fs-tree leaf with INODE_ITEM + DIR_INDEX + (mostly inline) EXTENT_DATA
/// items per file, plus a populated chunk tree for logical→physical
/// translation. Inline extents surface as MetadataReserved (they live
/// inside the metadata leaf); regular extents surface as Used runs.
///
/// <para>Streaming: reads go through a <see cref="SectorCache"/> so a 50 TB
/// Btrfs image needs only a few MB of working set, not 50 TB of RAM. Only the
/// 4 KiB superblock + a handful of node-sized reads (chunk tree, root tree,
/// fs-tree leaf) actually hit the disk.</para>
/// </summary>
public static class BtrfsExtentMap {

  // Key types
  private const byte InodeItem = 1;
  private const byte DirIndex = 96;
  private const byte ExtentData = 108;
  private const byte RootItem = 132;
  private const byte ChunkItem = 228;

  private const long FsTreeObjectId = 5;
  private const long FirstFreeObjectId = 256;

  private const int SbOffset = 0x10000;
  private const int SbSize = 4096;

  /// <summary>
  /// Single-pass walker. Parses the superblock at 0x10000, the
  /// <c>sys_chunk_array</c> for the boot chunk map, the chunk tree to
  /// extend it, the root tree to find the FS tree, and finally the FS
  /// tree leaves to emit per-file EXTENT_DATA runs. Reads flow through a
  /// <see cref="SectorCache"/> — the image is never loaded whole.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < SbOffset + SbSize) yield break;

    // Read only the superblock — 4 KiB regardless of image size.
    var sb = new byte[SbSize];
    image.Position = SbOffset;
    image.ReadExactly(sb);

    var magic = "_BHRfS_M"u8;
    if (!sb.AsSpan(0x40, 8).SequenceEqual(magic)) yield break;

    var rootTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(0x50));
    var chunkTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(0x58));
    var nodeSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x94));
    var sysChunkArraySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0xA0));
    var nodeSize = nodeSizeRaw is 0 or > 65536 ? 16384u : nodeSizeRaw;

    // First 64 KiB are reserved by the spec (boot loader area, unused), then SB at 0x10000.
    yield return new DefragBlockInfo(0, SbOffset, DefragBlockKind.MetadataReserved,
      FileName: "Btrfs reserved area");
    yield return new DefragBlockInfo(SbOffset, 0x1000, DefragBlockKind.MetadataReserved,
      FileName: "Btrfs superblock");

    var chunkMap = new List<(long logical, long physical, long length)>();
    ParseSysChunkArray(sb.AsSpan(0x32B), sysChunkArraySize, chunkMap);

    // SectorCache absorbs all subsequent node reads. For a defragmented image
    // most node reads land in the same 64 KB chunk so the cache hit rate is
    // very high; for fragmented metadata the LRU policy keeps the working set
    // bounded.
    using var cache = new SectorCache(image);

    // Walk chunk tree to extend the chunk map (and emit its node as metadata).
    var chunkTreePhys = LogicalToPhysical(chunkMap, chunkTreeLogical, image.Length, nodeSize);
    if (chunkTreePhys >= 0) {
      yield return new DefragBlockInfo(chunkTreePhys, nodeSize, DefragBlockKind.MetadataReserved,
        FileName: "Btrfs chunk tree");
      WalkChunkTreeNode(cache, chunkTreePhys, chunkMap, nodeSize);
    }

    // Yield root tree node as metadata.
    var rootTreePhys = LogicalToPhysical(chunkMap, rootTreeLogical, image.Length, nodeSize);
    if (rootTreePhys >= 0)
      yield return new DefragBlockInfo(rootTreePhys, nodeSize, DefragBlockKind.MetadataReserved,
        FileName: "Btrfs root tree");

    // Find FS tree root (leaf node holding fs-tree).
    var fsTreeLogical = FindFsTreeRoot(cache, rootTreePhys, chunkMap, nodeSize);
    if (fsTreeLogical < 0) yield break;
    var fsTreePhys = LogicalToPhysical(chunkMap, fsTreeLogical, image.Length, nodeSize);
    if (fsTreePhys < 0 || fsTreePhys + nodeSize > image.Length) yield break;
    yield return new DefragBlockInfo(fsTreePhys, nodeSize, DefragBlockKind.MetadataReserved,
      FileName: "Btrfs fs tree");

    // An fs tree is one leaf only while it is small. Past roughly fourteen files
    // it grows a level, and the node the root points at then holds key pointers
    // rather than items — so reading that one node found no file at all and the
    // volume was reported as holding nothing. Every consumer of this map reads
    // that as free space.
    var leaves = new List<long>();
    CollectFsLeaves(cache, fsTreePhys, chunkMap, nodeSize, leaves, 0);

    // Names first, across every leaf: a file's name and its extents need not be
    // in the same one.
    var inodeNames = new Dictionary<long, string>();
    var buffer = new byte[nodeSize];
    foreach (var leaf in leaves) {
      cache.Read(leaf, buffer);
      CollectDirNamesBuf(buffer, FirstFreeObjectId, "", inodeNames);
    }

    foreach (var leaf in leaves) {
      // The leaf itself is metadata wherever it sits.
      if (leaf != fsTreePhys)
        yield return new DefragBlockInfo(leaf, nodeSize, DefragBlockKind.MetadataReserved,
          FileName: "Btrfs fs tree");

      var leafBuf = new byte[nodeSize];
      cache.Read(leaf, leafBuf);
      foreach (var ext in WalkExtentDataItemsBuf(leafBuf, leaf, inodeNames, chunkMap, nodeSize, image.Length))
        yield return ext;
    }
  }

  /// <summary>
  /// Every leaf under <paramref name="nodePhys" />, which is the node itself
  /// when the tree has not grown past one.
  /// </summary>
  /// <remarks>
  /// A node says which it is: level zero holds items, anything above holds
  /// pointers to children. Walking only the node the root names works until the
  /// tree gains a level and then finds nothing, because what it reads is no
  /// longer items.
  /// </remarks>
  private static void CollectFsLeaves(SectorCache cache, long nodePhys,
      List<(long logical, long physical, long length)> chunkMap, uint nodeSize,
      List<long> sink, int depth) {
    if (depth > 8 || nodePhys < 0 || nodePhys + nodeSize > cache.Length) return;
    if (sink.Count > 4096) return;                 // a malformed tree must not spin

    var node = new byte[nodeSize];
    cache.Read(nodePhys, node);
    if (node.Length < 101) return;

    var level = node[100];
    if (level == 0) {
      if (!sink.Contains(nodePhys)) sink.Add(nodePhys);
      return;
    }

    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    for (uint i = 0; i < nritems && i < 1000; ++i) {
      var itemOff = 101 + (int)i * 33;
      if (itemOff + 33 > node.Length) break;
      var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff + 17));
      var childPhys = LogicalToPhysical(chunkMap, childLogical, cache.Length, nodeSize);
      if (childPhys >= 0) CollectFsLeaves(cache, childPhys, chunkMap, nodeSize, sink, depth + 1);
    }
  }

  private static long LogicalToPhysical(List<(long logical, long physical, long length)> chunkMap,
      long logical, long imageLen, uint nodeSize) {
    foreach (var (l, p, len) in chunkMap)
      if (logical >= l && logical < l + len) return p + (logical - l);
    // Identity fallback for synthetic test images that don't populate sys_chunk_array.
    if (chunkMap.Count == 0 && logical >= 0 && logical + nodeSize <= imageLen) return logical;
    return -1;
  }

  private static void ParseSysChunkArray(ReadOnlySpan<byte> arr, int size,
      List<(long, long, long)> chunkMap) {
    var end = Math.Min(size, arr.Length);
    var pos = 0;
    while (pos + 48 < end) {
      var logicalAddr = BinaryPrimitives.ReadInt64LittleEndian(arr[(pos + 9)..]);
      pos += 17;
      if (pos + 48 > end) break;
      var chunkLength = BinaryPrimitives.ReadInt64LittleEndian(arr[pos..]);
      var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(arr[(pos + 44)..]);
      pos += 48;
      if (numStripes > 0 && pos + 32 <= end) {
        var physOff = BinaryPrimitives.ReadInt64LittleEndian(arr[(pos + 8)..]);
        chunkMap.Add((logicalAddr, physOff, chunkLength));
      }
      pos += numStripes * 32;
    }
  }

  private static void WalkChunkTreeNode(SectorCache cache, long phys,
      List<(long, long, long)> chunkMap, uint nodeSize) {
    if (phys < 0 || phys + nodeSize > cache.Length) return;
    var node = new byte[nodeSize];
    cache.Read(phys, node);
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];

    if (level > 0) {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = 101 + (int)i * 33;
        if (itemOff + 33 > node.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff + 17));
        var childPhys = LogicalToPhysical(chunkMap, childLogical, cache.Length, nodeSize);
        if (childPhys >= 0) WalkChunkTreeNode(cache, childPhys, chunkMap, nodeSize);
      }
    } else {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = 101 + (int)i * 25;
        if (itemOff + 25 > node.Length) break;
        var keyType = node[itemOff + 8];
        var keyOffset = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff + 9));
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(itemOff + 17));
        if (keyType != ChunkItem) continue;
        var dataPos = 101 + (int)dataOffset;
        if (dataPos < 0 || dataPos + 48 > node.Length) continue;
        var chunkLength = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos));
        var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(dataPos + 44));
        if (numStripes > 0 && dataPos + 48 + 32 <= node.Length) {
          var physOff = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos + 48 + 8));
          if (LogicalToPhysical(chunkMap, keyOffset, cache.Length, nodeSize) < 0)
            chunkMap.Add((keyOffset, physOff, chunkLength));
        }
      }
    }
  }

  private static long FindFsTreeRoot(SectorCache cache, long phys,
      List<(long, long, long)> chunkMap, uint nodeSize) {
    if (phys < 0 || phys + nodeSize > cache.Length) return -1;
    var node = new byte[nodeSize];
    cache.Read(phys, node);
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];

    if (level > 0) {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = 101 + (int)i * 33;
        if (itemOff + 33 > node.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff + 17));
        var childPhys = LogicalToPhysical(chunkMap, childLogical, cache.Length, nodeSize);
        var result = FindFsTreeRoot(cache, childPhys, chunkMap, nodeSize);
        if (result >= 0) return result;
      }
    } else {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = 101 + (int)i * 25;
        if (itemOff + 25 > node.Length) break;
        var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff));
        var keyType = node[itemOff + 8];
        if (keyObjId == FsTreeObjectId && keyType == RootItem) {
          var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(itemOff + 17));
          var dataPos = 101 + (int)dataOffset;
          if (dataPos + 184 <= node.Length)
            return BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos + 176));
        }
      }
    }
    return -1;
  }

  private static void CollectDirNamesBuf(byte[] node, long dirObjectId, string path,
      Dictionary<long, string> inodeNames) {
    if (node.Length < 101) return;
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];
    if (level > 0) return; // WORM writer emits a single leaf — multi-level not supported here.

    for (uint i = 0; i < nritems && i < 1000; i++) {
      var itemOff = 101 + (int)i * 25;
      if (itemOff + 25 > node.Length) break;
      var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff));
      var keyType = node[itemOff + 8];
      if (keyObjId != dirObjectId || keyType != DirIndex) continue;

      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(itemOff + 17));
      var dataPos = 101 + (int)dataOffset;
      if (dataPos + 30 > node.Length) continue;
      var childInode = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos));
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(dataPos + 27));
      if (dataPos + 30 + nameLen > node.Length) continue;
      var name = Encoding.UTF8.GetString(node, dataPos + 30, nameLen);
      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      inodeNames[childInode] = fullPath;
    }
  }

  private static IEnumerable<DefragBlockInfo> WalkExtentDataItemsBuf(byte[] node, long phys,
      Dictionary<long, string> inodeNames, List<(long, long, long)> chunkMap,
      uint nodeSize, long imageLen) {
    _ = phys;
    if (node.Length < 101) yield break;
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];
    if (level > 0) yield break;

    for (uint i = 0; i < nritems && i < 1000; i++) {
      var itemOff = 101 + (int)i * 25;
      if (itemOff + 25 > node.Length) break;
      var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff));
      var keyType = node[itemOff + 8];
      if (keyType != ExtentData) continue;

      var name = inodeNames.TryGetValue(keyObjId, out var n) ? n : $"inode#{keyObjId}";
      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(itemOff + 17));
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(itemOff + 21));
      var dataPos = 101 + (int)dataOffset;
      if (dataPos < 0 || dataPos + 21 > node.Length) continue;

      var extentType = node[dataPos + 20];
      if (extentType == 0) {
        // Inline extent — bytes live inside the metadata leaf, not on a data extent.
        // Surface as a small MetadataReserved tile carrying the file name so users
        // can see "this file is inlined into metadata".
        var inlineLen = (int)dataSize - 21;
        if (inlineLen > 0) {
          // Compute the absolute on-disk byte offset of the inline payload by
          // resolving (phys + dataPos + 21) back through the chunk map. The
          // leaf was read from `phys` which is already a physical offset, so
          // we add the in-leaf offset directly.
          yield return new DefragBlockInfo(phys + dataPos + 21, inlineLen,
            DefragBlockKind.MetadataReserved, FileName: $"inline:{name}");
        }
      } else if (extentType == 1) {
        if (dataPos + 53 > node.Length) continue;
        var diskBytenr = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos + 21));
        var extOffset = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos + 37));
        var numBytes = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos + 45));
        if (diskBytenr == 0 || numBytes <= 0) continue; // sparse — no on-disk run
        var physOff = LogicalToPhysical(chunkMap, diskBytenr, imageLen, nodeSize);
        if (physOff < 0) continue;
        yield return new DefragBlockInfo(physOff + extOffset, numBytes,
          DefragBlockKind.Used, name);
      }
    }
  }
}

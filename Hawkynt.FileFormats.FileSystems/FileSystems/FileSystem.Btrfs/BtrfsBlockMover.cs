#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Btrfs;

/// <summary>
/// In-place Btrfs block mover for the WORM writer profile. Moves data extents
/// within a Btrfs image and patches the fs-tree leaf's <c>EXTENT_DATA</c> item
/// (<c>disk_bytenr</c>) so the file remains reachable at its new location, then
/// recomputes the CRC-32C checksum on every metadata block that was modified.
///
/// <para>The bundled <see cref="BtrfsWriter"/> uses <b>identity logical→physical
/// mapping</b> (logical == physical for all chunks), so only the fs-tree leaf
/// needs patching — no chunk-tree updates are required.</para>
///
/// <para>Inline extents (type 0) cannot be moved because their data lives inside
/// the metadata leaf itself. The extent map surfaces them as
/// <see cref="DefragBlockKind.MetadataReserved"/>, and the planner never
/// schedules them for moves.</para>
///
/// <para>Streaming: the image is never loaded whole. <see cref="Init(Stream)"/>
/// reads only the 4 KiB superblock to cache <c>nodeSize</c>, the boot
/// <c>sys_chunk_array</c> map, and the logical addresses of the chunk + root
/// trees. <see cref="UpdateAllocationAfterMove"/> walks the chunk tree, root
/// tree, and fs-tree leaf via a <see cref="SectorCache"/> and writes back only
/// the patched fs-tree leaf (one node-sized write). A 50 TB image needs a few
/// MB of cache, not 50 TB of RAM.</para>
/// </summary>
public sealed class BtrfsBlockMover : IFilesystemBlockMover {

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

  // Cached superblock fields (populated by Init).
  private int _nodeSize = 16384;
  private long _rootTreeLogical;
  private long _chunkTreeLogical;
  private readonly List<(long logical, long physical, long length)> _bootChunkMap = [];

  // ── Init ───────────────────────────────────────────────────────────────

  /// <summary>
  /// Streaming initialiser. Reads only the 4 KiB superblock and parses the
  /// <c>sys_chunk_array</c> for the boot chunk map. Subsequent moves walk the
  /// rest of the chunk tree through a <see cref="SectorCache"/> on demand.
  /// </summary>
  /// <summary>A sector, which is what an extent's address is aligned to.</summary>
  public int BlockSize => this._sectorSize > 0 ? this._sectorSize : 4096;

  /// <summary>
  /// First byte a file's extent may occupy: past the superblock and the trees
  /// the writer lays down in front of the data chunk.
  /// </summary>
  public long FirstDataByte => this._firstDataByte;

  /// <summary>
  /// Each call repoints the item naming the extent it is given and leaves the
  /// leaf's other items alone, so a file in several extents is several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// An extent may be held outside the image while the rest of the layout
  /// moves, which is what lets a full image be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <summary>Where the data chunk begins, which is as low as an extent may go.</summary>
  private long _firstDataByte = SbOffset + SbSize;

  /// <summary>Every address that changed, and what it changed to.</summary>
  private readonly Dictionary<long, long> _moved = [];

  /// <summary>Bytes an extent's address is aligned to, as the superblock says.</summary>
  private int _sectorSize = 4096;

  /// <summary>
  /// Performs the init operation.
  /// </summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    Span<byte> sb = stackalloc byte[SbSize];
    image.Position = SbOffset;
    image.ReadExactly(sb);
    ParseSuperblock(sb);
  }

  private void ParseSuperblock(ReadOnlySpan<byte> sb) {
    _rootTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(sb[0x50..]);
    _chunkTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(sb[0x58..]);
    _nodeSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[0x94..]);
    if (_nodeSize is 0 or > 65536) _nodeSize = 16384;
    this._sectorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[0x90..]);
    if (this._sectorSize is 0 or > 65536) this._sectorSize = 4096;
    var sysChunkArraySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[0xA0..]);
    _bootChunkMap.Clear();
    ParseSysChunkArraySpan(sb[0x32B..], sysChunkArraySize, _bootChunkMap);

    // The chunks the boot map names are the volume's own; a file's extent
    // starts past the last of them.
    foreach (var (logical, _, length) in this._bootChunkMap)
      this._firstDataByte = Math.Max(this._firstDataByte, logical + length);
  }

  // ── IFilesystemBlockMover ──────────────────────────────────────────────

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Streaming three-step update — image is never loaded whole. Reads flow
  /// through a <see cref="SectorCache"/> (a few MB of working set even for a
  /// 50 TB image); the only writes are the single patched fs-tree leaf node
  /// and a CRC recompute.
  /// <list type="number">
  ///   <item><b>Walk metadata</b>: parse chunk tree (extends the boot map),
  ///   resolve fs-tree-root via root tree.</item>
  ///   <item><b>Patch leaf</b>: read the fs-tree leaf into a node-sized buffer,
  ///   patch matching <c>EXTENT_DATA.disk_bytenr</c> entries, recompute CRC-32C,
  ///   write the leaf back, flush.</item>
  ///   <item><b>Done</b>: no extent-tree update is required for the WORM writer
  ///   profile (identity logical→physical mapping; the EXTENT_ITEM entries in
  ///   the extent tree continue to cover the same logical range).</item>
  /// </list>
  /// <para>If <see cref="Init(Stream)"/> was not called the method auto-
  /// initialises from the stream — callers may pass an uninitialised mover and
  /// it still works, mirroring the FAT/ext patterns.</para>
  /// </remarks>
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    if (_nodeSize <= 0) Init(image);
    if (oldOffset != newOffset) this._moved[oldOffset] = newOffset;

    using var cache = new SectorCache(image);

    // Extend the boot chunk map by walking the chunk tree on demand.
    var chunkMap = new List<(long, long, long)>(_bootChunkMap);
    var chunkTreePhys = LogicalToPhysical(chunkMap, _chunkTreeLogical, cache.Length, _nodeSize);
    if (chunkTreePhys >= 0)
      WalkChunkTreeNodeStream(cache, chunkTreePhys, chunkMap);

    // Find FS tree root via the root tree.
    var rootTreePhys = LogicalToPhysical(chunkMap, _rootTreeLogical, cache.Length, _nodeSize);
    if (rootTreePhys < 0) return;

    var fsTreeLogical = FindFsTreeRootStream(cache, rootTreePhys, chunkMap);
    if (fsTreeLogical < 0) return;
    var fsTreePhys = LogicalToPhysical(chunkMap, fsTreeLogical, cache.Length, _nodeSize);
    if (fsTreePhys < 0) return;

    // Read the fs-tree leaf into a node-sized buffer for in-memory patching.
    if (fsTreePhys + _nodeSize > image.Length) return;
    var leaf = ArrayPool<byte>.Shared.Rent(_nodeSize);
    try {
      cache.Read(fsTreePhys, leaf.AsSpan(0, _nodeSize));

      // Collect inode → name mapping from DIR_INDEX items in the leaf.
      var inodeNames = new Dictionary<long, string>();
      CollectDirNamesBuf(leaf, FirstFreeObjectId, "", inodeNames);

      var patched = PatchExtentDataItemsBuf(leaf, fileName, oldOffset, newOffset, length,
        inodeNames, chunkMap);
      if (!patched) return;

      // Recompute CRC-32C on the modified leaf and write it back as one node-
      // sized targeted write, then flush.
      RecomputeBlockChecksum(leaf, 0, _nodeSize);
      image.Position = fsTreePhys;
      image.Write(leaf, 0, _nodeSize);
      image.Flush();
      cache.Invalidate(fsTreePhys, _nodeSize);
    } finally {
      ArrayPool<byte>.Shared.Return(leaf);
    }
  }

  // ── Extent patching (in-memory node buffer) ───────────────────────────

  /// <summary>
  /// Walks the fs-tree leaf's EXTENT_DATA items and patches <c>disk_bytenr</c>
  /// for regular extents (type 1) whose resolved physical address matches
  /// <paramref name="oldOffset"/>. Operates on a node-sized buffer (the leaf
  /// has already been read from disk).
  /// </summary>
  private bool PatchExtentDataItemsBuf(byte[] leaf, string fileName,
      long oldOffset, long newOffset, long length,
      Dictionary<long, string> inodeNames,
      List<(long logical, long physical, long length)> chunkMap) {
    if (leaf.Length < 101) return false;
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(96));
    var level = leaf[100];
    if (level != 0) return false; // Only leaf nodes

    var patched = false;
    for (uint i = 0; i < nritems && i < 1000; i++) {
      var itemOff = 101 + (int)i * 25;
      if (itemOff + 25 > leaf.Length) break;

      var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(leaf.AsSpan(itemOff));
      var keyType = leaf[itemOff + 8];
      if (keyType != ExtentData) continue;

      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(itemOff + 17));
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(itemOff + 21));
      _ = dataSize;
      var dataPos = 101 + (int)dataOffset;
      if (dataPos < 0 || dataPos + 21 > leaf.Length) continue;

      var extentType = leaf[dataPos + 20];
      if (extentType != 1) continue; // Only regular extents (type 1), skip inline (type 0)

      if (dataPos + 53 > leaf.Length) continue;
      var diskBytenr = BinaryPrimitives.ReadInt64LittleEndian(leaf.AsSpan(dataPos + 21));
      var extOffset = BinaryPrimitives.ReadInt64LittleEndian(leaf.AsSpan(dataPos + 37));

      // Resolve the extent's physical address through the chunk map.
      var physOff = LogicalToPhysical(chunkMap, diskBytenr, long.MaxValue, _nodeSize);
      if (physOff < 0) continue;
      var extentPhysStart = physOff + extOffset;

      // Check if this extent's physical location matches the old offset.
      if (extentPhysStart != oldOffset) continue;

      // Optionally filter by file name.
      if (!fileName.Equals("*", StringComparison.Ordinal)) {
        var name = inodeNames.TryGetValue(keyObjId, out var n) ? n : $"inode#{keyObjId}";
        if (!name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
      }

      // Identity mapping: logical == physical. The new disk_bytenr is simply
      // newOffset (physical) minus the extent's internal offset, since
      // physical == logical in this WORM profile.
      var newDiskBytenr = newOffset - extOffset;
      BinaryPrimitives.WriteInt64LittleEndian(leaf.AsSpan(dataPos + 21), newDiskBytenr);
      patched = true;
    }

    return patched;
  }

  // ── CRC-32C recomputation ─────────────────────────────────────────────

  /// <summary>
  /// Recomputes CRC-32C over bytes [blockOff+32..blockOff+blockSize) and writes
  /// it as a little-endian u32 at blockOff+0, zeroing bytes [4..32).
  /// Matches <see cref="BtrfsWriter"/>'s WriteBlockChecksum layout.
  /// </summary>
  private static void RecomputeBlockChecksum(byte[] data, int blockOff, int blockSize) {
    var payload = data.AsSpan(blockOff + 32, blockSize - 32);
    var crc = Crc32.Compute(payload, Crc32.Castagnoli);
    data.AsSpan(blockOff, 32).Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(blockOff, 4), crc);
  }

  // ── Chunk map helpers ─────────────────────────────────────────────────

  private static long LogicalToPhysical(List<(long logical, long physical, long length)> chunkMap,
      long logical, long imageLen, int nodeSize) {
    foreach (var (l, p, len) in chunkMap)
      if (logical >= l && logical < l + len) return p + (logical - l);
    // Identity fallback for synthetic test images without sys_chunk_array.
    if (chunkMap.Count == 0 && logical >= 0 && logical + nodeSize <= imageLen) return logical;
    return -1;
  }

  private static void ParseSysChunkArraySpan(ReadOnlySpan<byte> arr, int size,
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

  // ── Stream-based tree walks (read via SectorCache) ─────────────────────

  /// <summary>
  /// Reads a single tree node (header + items area, <see cref="_nodeSize"/>
  /// bytes) from the cache. The first 101 bytes are the header; items follow.
  /// </summary>
  private byte[] ReadNode(SectorCache cache, long phys) {
    var node = new byte[_nodeSize];
    cache.Read(phys, node);
    return node;
  }

  private void WalkChunkTreeNodeStream(SectorCache cache, long phys,
      List<(long, long, long)> chunkMap) {
    if (phys < 0 || phys + _nodeSize > cache.Length) return;
    var node = ReadNode(cache, phys);
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];

    if (level > 0) {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = 101 + (int)i * 33;
        if (itemOff + 33 > node.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff + 17));
        var childPhys = LogicalToPhysical(chunkMap, childLogical, cache.Length, _nodeSize);
        if (childPhys >= 0) WalkChunkTreeNodeStream(cache, childPhys, chunkMap);
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
          if (LogicalToPhysical(chunkMap, keyOffset, cache.Length, _nodeSize) < 0)
            chunkMap.Add((keyOffset, physOff, chunkLength));
        }
      }
    }
  }

  /// <summary>
  /// Brings the extent tree along with the extents it accounts for.
  /// </summary>
  /// <remarks>
  /// Its items are keyed by the address of what they describe, so an extent
  /// that moved leaves an item naming an address nothing occupies — and the
  /// next allocation reads that as free and writes over live data. The keys are
  /// rewritten here and checked to be in order afterwards, because a leaf whose
  /// items are out of order is a leaf nothing can search.
  /// </remarks>
  public void SettleExtentTree(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (this._moved.Count == 0) return;
    if (this._nodeSize <= 0) this.Init(image);

    using var cache = new SectorCache(image);
    var chunkMap = new List<(long, long, long)>(this._bootChunkMap);
    var chunkTreePhys = LogicalToPhysical(chunkMap, this._chunkTreeLogical, cache.Length, this._nodeSize);
    if (chunkTreePhys >= 0) WalkChunkTreeNodeStream(cache, chunkTreePhys, chunkMap);

    var rootTreePhys = LogicalToPhysical(chunkMap, this._rootTreeLogical, cache.Length, this._nodeSize);
    if (rootTreePhys < 0) return;

    var extentTreeLogical = FindTreeRootStream(cache, rootTreePhys, chunkMap, ExtentTreeObjectId);
    if (extentTreeLogical < 0) return;

    var extentTreePhys = LogicalToPhysical(chunkMap, extentTreeLogical, cache.Length, this._nodeSize);
    if (extentTreePhys < 0 || extentTreePhys + this._nodeSize > image.Length) return;

    var leaf = new byte[this._nodeSize];
    image.Position = extentTreePhys;
    image.ReadExactly(leaf);
    if (leaf[100] != 0) return;                       // only a single leaf is described

    var count = BinaryPrimitives.ReadUInt32LittleEndian(leaf.AsSpan(96));
    var changed = false;
    for (var i = 0u; i < count && i < 4096; ++i) {
      var itemOff = 101 + (int)i * 25;
      if (itemOff + 25 > leaf.Length) break;
      if (leaf[itemOff + 8] != ExtentItemType) continue;

      var key = BinaryPrimitives.ReadInt64LittleEndian(leaf.AsSpan(itemOff));
      if (!this._moved.TryGetValue(key, out var moved)) continue;

      BinaryPrimitives.WriteInt64LittleEndian(leaf.AsSpan(itemOff), moved);
      changed = true;
    }

    if (!changed) return;

    long previous = long.MinValue;
    for (var i = 0u; i < count && i < 4096; ++i) {
      var itemOff = 101 + (int)i * 25;
      if (itemOff + 25 > leaf.Length) break;

      var key = BinaryPrimitives.ReadInt64LittleEndian(leaf.AsSpan(itemOff));
      if (key < previous)
        throw new NotSupportedException(
          "Btrfs: the layout would leave the extent tree's items out of order, and a leaf that " +
          "is not in order is one nothing can search.");
      previous = key;
    }

    RecomputeBlockChecksum(leaf, 0, this._nodeSize);
    image.Position = extentTreePhys;
    image.Write(leaf, 0, this._nodeSize);
    image.Flush();
  }

  /// <summary>The extent tree, which accounts for every allocated address.</summary>
  private const long ExtentTreeObjectId = 2;

  private const byte ExtentItemType = 168;

  /// <summary>Finds the root of the tree with this object id, as the root tree names it.</summary>
  private long FindTreeRootStream(SectorCache cache, long phys,
      List<(long, long, long)> chunkMap, long objectId) {
    if (phys < 0 || phys + this._nodeSize > cache.Length) return -1;

    var node = ReadNode(cache, phys);
    var count = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    if (node[100] > 0) {
      for (var i = 0u; i < count && i < 1000; ++i) {
        var itemOff = 101 + (int)i * 33;
        if (itemOff + 33 > node.Length) break;

        var child = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff + 17));
        var result = FindTreeRootStream(cache,
          LogicalToPhysical(chunkMap, child, cache.Length, this._nodeSize), chunkMap, objectId);
        if (result >= 0) return result;
      }

      return -1;
    }

    for (var i = 0u; i < count && i < 1000; ++i) {
      var itemOff = 101 + (int)i * 25;
      if (itemOff + 25 > node.Length) break;
      if (BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff)) != objectId) continue;
      if (node[itemOff + 8] != RootItem) continue;

      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(itemOff + 17));
      var dataPos = 101 + (int)dataOffset;
      if (dataPos + 184 <= node.Length)
        return BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(dataPos + 176));
    }

    return -1;
  }

  private long FindFsTreeRootStream(SectorCache cache, long phys,
      List<(long, long, long)> chunkMap) {
    if (phys < 0 || phys + _nodeSize > cache.Length) return -1;
    var node = ReadNode(cache, phys);
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];

    if (level > 0) {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = 101 + (int)i * 33;
        if (itemOff + 33 > node.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(itemOff + 17));
        var childPhys = LogicalToPhysical(chunkMap, childLogical, cache.Length, _nodeSize);
        var result = FindFsTreeRootStream(cache, childPhys, chunkMap);
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
    if (level > 0) return; // WORM writer emits a single leaf

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
}

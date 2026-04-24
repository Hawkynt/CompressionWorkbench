#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Btrfs;

/// <summary>
/// Reads Btrfs filesystem images (single-device, non-RAID).
/// Parses superblock, builds chunk map (logical-to-physical translation),
/// traverses B-trees to enumerate files and extract uncompressed extents.
/// </summary>
public sealed class BtrfsReader : IDisposable {
  private static readonly byte[] Magic = "_BHRfS_M"u8.ToArray();

  // Key types
  private const byte InodeItem = 1;
  private const byte DirItem = 84;
  private const byte DirIndex = 96;
  private const byte ExtentData = 108;
  private const byte RootItem = 132;
  private const byte ChunkItem = 228;
  private const byte RootRef = 156;

  // Well-known object IDs
  private const long RootTreeObjectId = 1;
  private const long ChunkTreeObjectId = 3;
  private const long FsTreeObjectId = 5;
  private const long FirstFreeObjectId = 256;

  private readonly byte[] _data;
  private readonly List<BtrfsEntry> _entries = [];
  private readonly Dictionary<long, long> _inodeSizes = new();

  // Superblock fields
  private long _rootTreeLogical;
  private long _chunkTreeLogical;
  private uint _nodeSize;
  private uint _sectorSize;
  private int _sysChunkArraySize;

  // Chunk map: logical address -> (physical offset, length)
  private readonly List<(long logical, long physical, long length)> _chunkMap = [];

  public IReadOnlyList<BtrfsEntry> Entries => _entries;

  public BtrfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    // Superblock is at offset 0x10000 (65536). Canonical offsets per
    // fs/btrfs/ctree.h btrfs_super_block:
    //   0x40 magic[8], 0x50 root, 0x58 chunk_root,
    //   0x90 sectorsize, 0x94 nodesize, 0xA0 sys_chunk_array_size,
    //   0x32B sys_chunk_array[2048].
    // Historical versions of this reader inspected sbOffset+{80,88,128,132,
    // 196,299}; those offsets collided with csum_type (0xC4) and were replaced
    // with the spec-compliant locations below.
    const int sbOffset = 0x10000;
    const int sysChunkArrayOffset = sbOffset + 0x32B;

    if (_data.Length < sysChunkArrayOffset + 4)
      throw new InvalidDataException("Btrfs: image too small for superblock.");

    // Validate magic at superblock + 0x40
    if (!_data.AsSpan(sbOffset + 0x40, 8).SequenceEqual(Magic))
      throw new InvalidDataException("Btrfs: invalid magic.");

    // Parse superblock fields at canonical offsets
    _rootTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(sbOffset + 0x50));
    _chunkTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(sbOffset + 0x58));
    _sectorSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sbOffset + 0x90));
    _nodeSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sbOffset + 0x94));
    _sysChunkArraySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sbOffset + 0xA0));

    if (_nodeSize == 0 || _nodeSize > 65536)
      _nodeSize = 16384;
    if (_sectorSize == 0)
      _sectorSize = 4096;

    // Step 1: Parse sys_chunk_array from superblock to build initial chunk map
    ParseSysChunkArray(sysChunkArrayOffset);

    // Step 2: Read chunk tree to extend the chunk map
    ReadChunkTree();

    // Step 3: Read root tree to find FS tree root
    var fsTreeLogical = FindFsTreeRoot();
    if (fsTreeLogical < 0) return;

    // Step 4: Read FS tree to enumerate files
    // First pass: collect inode sizes
    CollectInodeSizes(fsTreeLogical);

    // Second pass: enumerate directory entries
    EnumerateDirectory(fsTreeLogical, FirstFreeObjectId, "");
  }

  // ── Chunk map ────────────────────────────────────────────────────────────

  private void ParseSysChunkArray(int offset) {
    var end = offset + _sysChunkArraySize;
    if (end > _data.Length) end = _data.Length;

    var pos = offset;
    while (pos + 48 < end) {
      // Key: objectid(8) + type(1) + offset(8) = 17 bytes
      // The offset in the key is the logical address
      var logicalAddr = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(pos + 9));
      pos += 17; // skip key

      if (pos + 48 > end) break;

      // Chunk item: length(8), owner(8), stripe_len(8), type(8), io_align(4),
      //             io_width(4), sector_size(4), num_stripes(2), sub_stripes(2)
      var chunkLength = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(pos));
      var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos + 44));
      pos += 48; // chunk item header

      if (numStripes > 0 && pos + 32 <= end) {
        // First stripe: devid(8) + offset(8) + dev_uuid(16)
        var physicalOffset = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(pos + 8));
        _chunkMap.Add((logicalAddr, physicalOffset, chunkLength));
      }

      // Skip all stripes (each is 32 bytes)
      pos += numStripes * 32;
    }
  }

  private void ReadChunkTree() {
    var physical = LogicalToPhysical(_chunkTreeLogical);
    if (physical < 0) return;

    ReadChunkTreeNode(physical);
  }

  private void ReadChunkTreeNode(long physical) {
    if (physical < 0 || physical + 101 > _data.Length) return;
    var offset = (int)physical;

    // btrfs_header: nritems (u32) at offset 96, level (u8) at offset 100.
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 96));
    var level = _data[offset + 100];

    if (level > 0) {
      // Internal node: key(17) + blockptr(8) + generation(8) = 33 bytes per item
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 33;
        if (itemOff + 33 > _data.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff + 17));
        var childPhysical = LogicalToPhysical(childLogical);
        if (childPhysical >= 0)
          ReadChunkTreeNode(childPhysical);
      }
    } else {
      // Leaf node
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 25;
        if (itemOff + 25 > _data.Length) break;

        var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff));
        var keyType = _data[itemOff + 8];
        var keyOffset = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff + 9));

        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 17));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 21));

        if (keyType != ChunkItem) continue;

        // Data is at end of header + items area, offset is relative to data start
        var dataPos = offset + 101 + (int)dataOffset;
        if (dataPos < 0 || dataPos + 48 > _data.Length) continue;

        var chunkLength = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos));
        var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dataPos + 44));

        if (numStripes > 0 && dataPos + 48 + 32 <= _data.Length) {
          var physOff = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos + 48 + 8));
          // Only add if not already mapped
          var logical = keyOffset;
          if (LogicalToPhysical(logical) < 0)
            _chunkMap.Add((logical, physOff, chunkLength));
        }
      }
    }
  }

  /// <summary>
  /// Resolves a logical address to a physical offset in the image using
  /// the chunk map built from the superblock's <c>sys_chunk_array</c> and
  /// the chunk tree. If a lookup misses the chunk map, we fall back to
  /// identity mapping only when the chunk map is entirely empty — that
  /// path exists for the synthetic test corpus (<c>BuildMinimalBtrfs</c>)
  /// which predates sys_chunk_array support. Real writer output always
  /// populates the map, so the chunk tree path is taken.
  /// </summary>
  private long LogicalToPhysical(long logical) {
    foreach (var (l, p, len) in _chunkMap) {
      if (logical >= l && logical < l + len)
        return p + (logical - l);
    }
    // Identity mapping is used only when no chunk map entries exist — e.g.
    // for synthetic test images that never populated sys_chunk_array. Real
    // writer output always supplies a populated map above and never hits
    // this branch.
    if (this._chunkMap.Count == 0 && logical >= 0 && logical + _nodeSize <= _data.Length)
      return logical;
    return -1;
  }

  /// <summary>
  /// Diagnostic: indicates whether the chunk map used during reading was
  /// non-empty, i.e. this image carries a real chunk tree as opposed to
  /// relying on identity-mapping fallback for synthetic test data.
  /// </summary>
  public bool UsedRealChunkTree => this._chunkMap.Count > 0;

  // ── B-tree traversal ─────────────────────────────────────────────────────

  private long FindFsTreeRoot() {
    var physical = LogicalToPhysical(_rootTreeLogical);
    if (physical < 0) return -1;
    return FindFsTreeInNode(physical);
  }

  private long FindFsTreeInNode(long physical) {
    if (physical < 0 || physical + 101 > _data.Length) return -1;
    var offset = (int)physical;

    // btrfs_header: nritems (u32) at offset 96, level (u8) at offset 100.
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 96));
    var level = _data[offset + 100];

    if (level > 0) {
      // Internal node: search children
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 33;
        if (itemOff + 33 > _data.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff + 17));
        var childPhysical = LogicalToPhysical(childLogical);
        var result = FindFsTreeInNode(childPhysical);
        if (result >= 0) return result;
      }
    } else {
      // Leaf: look for ROOT_ITEM with objectid == FsTreeObjectId
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 25;
        if (itemOff + 25 > _data.Length) break;

        var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff));
        var keyType = _data[itemOff + 8];

        if (keyObjId == FsTreeObjectId && keyType == RootItem) {
          var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 17));
          var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 21));

          var dataPos = offset + 101 + (int)dataOffset;
          if (dataPos < 0 || dataPos + 176 > _data.Length) continue;

          // ROOT_ITEM contains an embedded key at offset 0 that we skip;
          // the bytenr (logical address of the FS tree root) is at offset 176
          // Actually, ROOT_ITEM structure: inode (160 bytes) + generation(8) + root_dirid(8)
          // + bytenr(8) at offset 176
          var fsRoot = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos + 176));
          return fsRoot;
        }
      }
    }
    return -1;
  }

  // ── Inode size collection ────────────────────────────────────────────────

  private void CollectInodeSizes(long treeLogical) {
    var physical = LogicalToPhysical(treeLogical);
    if (physical < 0) return;
    CollectInodeSizesInNode(physical);
  }

  private void CollectInodeSizesInNode(long physical) {
    if (physical < 0 || physical + 101 > _data.Length) return;
    var offset = (int)physical;

    // btrfs_header: nritems (u32) at offset 96, level (u8) at offset 100.
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 96));
    var level = _data[offset + 100];

    if (level > 0) {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 33;
        if (itemOff + 33 > _data.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff + 17));
        var childPhysical = LogicalToPhysical(childLogical);
        CollectInodeSizesInNode(childPhysical);
      }
    } else {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 25;
        if (itemOff + 25 > _data.Length) break;

        var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff));
        var keyType = _data[itemOff + 8];

        if (keyType != InodeItem) continue;

        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 17));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 21));

        var dataPos = offset + 101 + (int)dataOffset;
        if (dataPos < 0 || dataPos + 24 > _data.Length) continue;

        // INODE_ITEM: size is at offset 16 (int64 LE)
        var size = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos + 16));
        _inodeSizes[keyObjId] = size;
      }
    }
  }

  // ── Directory enumeration ────────────────────────────────────────────────

  private void EnumerateDirectory(long treeLogical, long dirObjectId, string path) {
    var physical = LogicalToPhysical(treeLogical);
    if (physical < 0) return;

    var dirItems = new List<(string name, long childInode, bool isDir)>();
    CollectDirItems(physical, dirObjectId, dirItems);

    foreach (var (name, childInode, isDir) in dirItems) {
      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      var size = isDir ? 0L : (_inodeSizes.TryGetValue(childInode, out var s) ? s : 0);

      _entries.Add(new BtrfsEntry {
        Name = fullPath,
        Size = size,
        IsDirectory = isDir,
        Inode = childInode,
      });

      if (isDir)
        EnumerateDirectory(treeLogical, childInode, fullPath);
    }
  }

  private void CollectDirItems(long physical, long dirObjectId,
      List<(string name, long childInode, bool isDir)> results) {
    if (physical < 0 || physical + 101 > _data.Length) return;
    var offset = (int)physical;

    // btrfs_header: nritems (u32) at offset 96, level (u8) at offset 100.
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 96));
    var level = _data[offset + 100];

    if (level > 0) {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 33;
        if (itemOff + 33 > _data.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff + 17));
        var childPhysical = LogicalToPhysical(childLogical);
        CollectDirItems(childPhysical, dirObjectId, results);
      }
    } else {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 25;
        if (itemOff + 25 > _data.Length) break;

        var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff));
        var keyType = _data[itemOff + 8];

        if (keyObjId != dirObjectId || keyType != DirIndex) continue;

        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 17));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 21));

        var dataPos = offset + 101 + (int)dataOffset;
        if (dataPos < 0 || dataPos + 30 > _data.Length) continue;

        // DIR_INDEX structure:
        // child key: objectid(8) + type(1) + offset(8) = 17 bytes
        // transid: 8 bytes (offset 17)
        // data_len: 2 bytes (offset 25)
        // name_len: 2 bytes (offset 27)
        // type: 1 byte (offset 29)
        // name: name_len bytes (offset 30)
        var childInode = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos));
        var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(dataPos + 27));
        var dirType = _data[dataPos + 29];

        if (dataPos + 30 + nameLen > _data.Length) continue;
        var name = Encoding.UTF8.GetString(_data, dataPos + 30, nameLen);

        // type: 1=regular file, 2=directory
        var isDir = dirType == 2;
        results.Add((name, childInode, isDir));
      }
    }
  }

  // ── File extraction ──────────────────────────────────────────────────────

  public byte[] Extract(BtrfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    var physical = LogicalToPhysical(_rootTreeLogical);
    if (physical < 0) return [];

    var fsTreeLogical = FindFsTreeRoot();
    if (fsTreeLogical < 0) return [];

    var fsPhysical = LogicalToPhysical(fsTreeLogical);
    if (fsPhysical < 0) return [];

    using var ms = new MemoryStream();
    CollectExtentData(fsPhysical, entry.Inode, ms);

    var result = ms.ToArray();
    if (result.Length > entry.Size)
      return result.AsSpan(0, (int)entry.Size).ToArray();
    return result;
  }

  private void CollectExtentData(long physical, long inodeId, MemoryStream output) {
    if (physical < 0 || physical + 101 > _data.Length) return;
    var offset = (int)physical;

    // btrfs_header: nritems (u32) at offset 96, level (u8) at offset 100.
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset + 96));
    var level = _data[offset + 100];

    if (level > 0) {
      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 33;
        if (itemOff + 33 > _data.Length) break;
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff + 17));
        var childPhysical = LogicalToPhysical(childLogical);
        CollectExtentData(childPhysical, inodeId, output);
      }
    } else {
      // Collect EXTENT_DATA items sorted by file offset (key.offset)
      var extents = new SortedList<long, (int dataPos, int dataSize)>();

      for (uint i = 0; i < nritems && i < 1000; i++) {
        var itemOff = offset + 101 + (int)i * 25;
        if (itemOff + 25 > _data.Length) break;

        var keyObjId = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff));
        var keyType = _data[itemOff + 8];
        var keyOffset = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(itemOff + 9));

        if (keyObjId != inodeId || keyType != ExtentData) continue;

        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 17));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(itemOff + 21));

        var dataPos = offset + 101 + (int)dataOffset;
        if (dataPos < 0 || dataPos + 21 > _data.Length) continue;

        extents[keyOffset] = (dataPos, (int)dataSize);
      }

      foreach (var (fileOffset, (dataPos, dataSize)) in extents) {
        // EXTENT_DATA: generation(8) + ram_bytes(8) + compression(1) + encryption(1)
        //              + other_encoding(2) + type(1) = 21 bytes header
        var compression = _data[dataPos + 16];
        var extentType = _data[dataPos + 20];

        if (extentType == 0) {
          // Inline extent: data follows the 21-byte header
          var inlineLen = dataSize - 21;
          if (inlineLen > 0 && dataPos + 21 + inlineLen <= _data.Length)
            output.Write(_data, dataPos + 21, inlineLen);
        } else if (extentType == 1 && compression == 0) {
          // Regular (non-compressed) extent
          if (dataPos + 53 > _data.Length) continue;
          var diskBytenr = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos + 21));
          var diskNumBytes = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos + 29));
          var extOffset = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos + 37));
          var numBytes = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(dataPos + 45));

          if (diskBytenr == 0) {
            // Sparse extent — write zeros
            output.Write(new byte[(int)numBytes]);
          } else {
            var extPhysical = LogicalToPhysical(diskBytenr);
            if (extPhysical >= 0 && extPhysical + extOffset + numBytes <= _data.Length) {
              output.Write(_data, (int)(extPhysical + extOffset), (int)numBytes);
            }
          }
        }
        // Compressed extents are not supported (skip silently)
      }
    }
  }

  public void Dispose() { }
}

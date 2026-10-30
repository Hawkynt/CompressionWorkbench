#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Reads Apple File System (APFS) images per Apple's "Apple File System
/// Reference" (public spec). Walks the NXSB → container OMAP → APSB →
/// volume OMAP → filesystem B-tree chain and extracts file data via
/// <c>FILE_EXTENT</c> records.
/// </summary>
public sealed class ApfsReader : IDisposable {
  private const uint NxMagicLE = 0x4253584E; // "NXSB" stored LE
  private const uint ApsbMagicLE = 0x42535041; // "APSB" stored LE

  /// <summary>Random-access view; an APFS container is not bounded by what an array holds.</summary>
  private readonly ImageAccessor _data;
  private readonly List<ApfsEntry> _entries = [];
  private uint _blockSize = DEFAULT_BLOCK_SIZE;
  private bool _hashedNames;

  /// <summary>Container block size from the superblock.</summary>
  public uint BlockSize => this._blockSize;

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<ApfsEntry> Entries => this._entries;

  /// <summary>
  /// Initializes a new instance of <see cref="ApfsReader"/>.
  /// </summary>
  public ApfsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._data = new ImageAccessor(stream, leaveOpen: true);
    this.Parse();
  }

  private Span<byte> BlockSpan(long blockNum) {
    var off = blockNum * this._blockSize;
    if (off < 0 || off + this._blockSize > this._data.Length)
      throw new InvalidDataException($"APFS: block {blockNum} out of range.");
    return this._data.Read(off, (int)this._blockSize).AsSpan();
  }

  private void Parse() {
    if (this._data.Length < 4096)
      throw new InvalidDataException("APFS: image too small.");

    // NX superblock at block 0.
    var nxMagic = this._data.ReadUInt32(32);
    if (nxMagic != NxMagicLE)
      throw new InvalidDataException("APFS: invalid container superblock magic.");

    this._blockSize = this._data.ReadUInt32(36);
    if (this._blockSize == 0) this._blockSize = DEFAULT_BLOCK_SIZE;

    // Read container OMAP phys address. The spec has `nx_omap_oid` at offset 160
    // (container OMAP ephemeral OID). To enable an on-disk round-trip without a
    // checkpoint-resolver, our writer also stashes the physical block at offset
    // 3072 (reader-specific). We use that when present; otherwise we fall back
    // to scanning for an OMAP object.
    // Block 0 is only a copy: the container superblock in force is the newest valid
    // one in the checkpoint descriptor area (Apple File System Reference, "Mounting
    // an Apple File System Partition"). Its nx_omap_oid (+160) is the physical block
    // of the container object map.
    var nxsb = this.LatestCheckpointSuperblock();
    var ctrOmapPhys = this.ResolveCtrOmapPhys();
    if (nxsb is { } latest) {
      var omapOid = BinaryPrimitives.ReadUInt64LittleEndian(latest.AsSpan(160));
      if (omapOid != 0 && this.IsObjectOfType((long)omapOid, OBJECT_TYPE_OMAP))
        ctrOmapPhys = omapOid;
    }
    if (ctrOmapPhys == 0) {
      // Nothing to parse — image is NXSB-only.
      return;
    }

    // Container OMAP points to its B-tree root. From that tree we find APSB
    // (volume superblock) via the nx_fs_oid[0] entry at NXSB +184.
    var apsbVirtOid = nxsb is { } current
      ? BinaryPrimitives.ReadUInt64LittleEndian(current.AsSpan(184))
      : this._data.ReadUInt64(184);
    if (apsbVirtOid == 0) return;

    var apsbPhys = this.ResolveOidViaOmap(ctrOmapPhys, apsbVirtOid);
    if (apsbPhys == 0) return;

    var apsbBlock = this.BlockSpan((long)apsbPhys);
    if (BinaryPrimitives.ReadUInt32LittleEndian(apsbBlock[32..]) != ApsbMagicLE) return;

    // APSB → the volume's object map, by block, and its filesystem tree, by identifier.
    var volOmapPhys = BinaryPrimitives.ReadUInt64LittleEndian(apsbBlock[APSB_OMAP_OID..]);
    var rootTreeVirtOid = BinaryPrimitives.ReadUInt64LittleEndian(apsbBlock[APSB_ROOT_TREE_OID..]);
    if (volOmapPhys == 0 || rootTreeVirtOid == 0) return;

    var rootTreePhys = this.ResolveOidViaOmap(volOmapPhys, rootTreeVirtOid);
    if (rootTreePhys == 0) return;

    // Walk FS tree leaf(s) and collect inodes / drec / file_extent.
    // The filesystem tree's nodes are virtual, so descending it needs the
    // volume's object map to turn each child's identifier into a block.
    // APFS_INCOMPAT_CASE_INSENSITIVE (0x1) or _NORMALIZATION_INSENSITIVE (0x8): directory
    // record keys carry a name hash beside the length.
    var incompat = BinaryPrimitives.ReadUInt64LittleEndian(apsbBlock[APSB_INCOMPAT_FEATURES..]);
    this._hashedNames = (incompat & 0x9UL) != 0;
    this.ParseFsTree((long)rootTreePhys, volOmapPhys);
  }

  // ── OMAP resolution ─────────────────────────────────────────────────────

  private bool IsObjectOfType(long block, uint type) {
    var off = block * this._blockSize;
    if (block <= 0 || off + this._blockSize > this._data.Length) return false;
    return (this._data.ReadUInt32(off + 24) & OBJECT_TYPE_MASK) == type;
  }

  /// <summary>
  /// The container superblock with the highest transaction id among those in the
  /// checkpoint descriptor area (nx_xp_desc_base @112, nx_xp_desc_blocks @104) whose
  /// magic and Fletcher-64 checksum are valid; null when the area holds none.
  /// </summary>
  private byte[]? LatestCheckpointSuperblock() {
    var descBlocks = this._data.ReadUInt32(104);
    var descBase = this._data.ReadUInt64(112);
    // The high bit marks a non-contiguous area addressed through a B-tree; not followed here.
    if ((descBlocks & 0x8000_0000u) != 0 || descBlocks == 0 || descBlocks > 1 << 16) return null;
    byte[]? best = null;
    ulong bestXid = 0;
    for (var i = 0L; i < descBlocks; i++) {
      var off = ((long)descBase + i) * this._blockSize;
      if (off < 0 || off + this._blockSize > this._data.Length) break;
      if ((this._data.ReadUInt32(off + 24) & OBJECT_TYPE_MASK) != OBJECT_TYPE_NX_SUPERBLOCK) continue;
      if (this._data.ReadUInt32(off + 32) != NxMagicLE) continue;
      var block = this._data.Read(off, (int)this._blockSize);
      if (!ApfsFletcher64.Verify(block)) continue;
      var xid = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(16));
      if (best is null || xid > bestXid) {
        best = block;
        bestXid = xid;
      }
    }
    return best;
  }

  private ulong ResolveCtrOmapPhys() {
    // Writer-stamped physical hint at offset 3072 of NXSB (unused spec area).
    var hint = this._data.ReadUInt64(3072);
    if (hint > 0 && (long)hint * this._blockSize + this._blockSize <= this._data.Length) {
      // Verify it's actually an OMAP object.
      var span = this.BlockSpan((long)hint);
      var type = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) & OBJECT_TYPE_MASK;
      if (type == OBJECT_TYPE_OMAP) return hint;
    }

    // Fallback: scan for any OMAP-typed block.
    var blockCount = this._data.Length / this._blockSize;
    for (long b = 0; b < blockCount; b++) {
      var span = this._data.Read(b * this._blockSize, (int)this._blockSize).AsSpan();
      var type = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) & OBJECT_TYPE_MASK;
      if (type == OBJECT_TYPE_OMAP)
        return (ulong)b;
    }
    return 0;
  }

  /// <summary>
  /// Given a physical block address of an OMAP phys object, follow its tree and
  /// resolve a virtual OID to its physical block number. Returns 0 if not found.
  /// </summary>
  private ulong ResolveOidViaOmap(ulong omapPhys, ulong virtOid) {
    var omap = this.BlockSpan((long)omapPhys);
    // om_tree_oid at offset 48 (u64) = physical block number of OMAP B-tree root.
    var treePhys = BinaryPrimitives.ReadUInt64LittleEndian(omap[48..]);
    if (treePhys == 0) return 0;

    // The map is a tree like any other: a volume with many nodes to name has a map
    // deeper than one node, and reading only its root finds nothing at all.
    // omap_key_t {ok_oid, ok_xid}: an object can have several versions; the one in
    // force is the newest (snapshots are not read here).
    ulong found = 0, foundXid = 0;
    var any = false;
    foreach (var (key, value) in this.CollectAllLeafRecords((long)treePhys)) {
      if (key.Length < 16) continue;
      var ok = BinaryPrimitives.ReadUInt64LittleEndian(key);
      if (ok != virtOid) continue;
      if (value.Length < 16) continue;
      var xid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8));
      if (any && xid <= foundXid) continue;
      found = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8));
      foundXid = xid;
      any = true;
    }
    return found;
  }

  // ── B-tree leaf enumeration ─────────────────────────────────────────────

  /// <summary>
  /// Enumerates (key, value) pairs from a B-tree leaf node. Supports single-level
  /// root-leaf trees (what our writer produces) and plain leaf nodes.
  /// </summary>
  private static IEnumerable<(byte[] Key, byte[] Value)> EnumerateBtreeLeafRecords(
      ReadOnlySpan<byte> node, bool isRoot, int fixedKeyLen = 0, int fixedValLen = 0) {
    var results = new List<(byte[], byte[])>();

    var type = BinaryPrimitives.ReadUInt32LittleEndian(node[24..]) & OBJECT_TYPE_MASK;
    if (type != OBJECT_TYPE_BTREE && type != OBJECT_TYPE_BTREE_NODE && type != OBJECT_TYPE_OMAP
        && type != OBJECT_TYPE_FSTREE && type != OBJECT_TYPE_BLOCKREFTREE
        && type != OBJECT_TYPE_SNAPMETATREE)
      return results;

    // btn_flags at offset 32.
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(node[32..]);
    var level = BinaryPrimitives.ReadUInt16LittleEndian(node[34..]);
    var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(node[36..]);
    // Both leaf (level 0) and internal (level > 0) nodes use the same TOC layout;
    // callers interpret the values differently (records vs. child addresses).
    _ = level;
    if (nkeys == 0) return results;

    // btn_table_space.off at +40; data[] starts after the btn header (at +56).
    var tableOff = BinaryPrimitives.ReadUInt16LittleEndian(node[40..]);
    var tableLen = BinaryPrimitives.ReadUInt16LittleEndian(node[42..]);
    const int btnHeaderEnd = 56;
    var tocAbs = btnHeaderEnd + tableOff;
    var keyAreaStart = tocAbs + tableLen;

    // Value area ends at node.Length (for non-root) or node.Length - 40 (for root, to skip btree_info).
    var valAreaEnd = isRoot || (flags & BTNODE_ROOT) != 0
      ? node.Length - 40
      : node.Length;

    var isFixed = (flags & BTNODE_FIXED_KV_SIZE) != 0;

    // A fixed-size tree carries only offsets in its slots and states the one key
    // size and one value size in the root's footer. This used to read those
    // offsets and then leave both lengths at zero, with a comment saying the
    // sizes were "not used by us" — and the guard below drops a slot of zero
    // length, so every record in such a node was skipped and the tree read as
    // empty. Every object map on a real APFS volume is laid out this way, so
    // what this could open was our own volumes and nothing else.
    // Only a root carries the footer; a node below one is told the sizes by the
    // walk that reached it.
    if (isFixed && (isRoot || (flags & BTNODE_ROOT) != 0)) {
      var info = node.Length - 40;
      fixedKeyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 8)..]);
      fixedValLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 12)..]);
    }
    // An internal node's value is the child's block number, whatever the leaves
    // hold.
    if (isFixed && (flags & BTNODE_LEAF) == 0) fixedValLen = 8;

    for (uint k = 0; k < nkeys; k++) {
      int keyOff, keyLen, valOff, valLen;
      if (isFixed) {
        var e = tocAbs + (int)k * 4;
        if (e + 4 > node.Length) break;
        keyOff = BinaryPrimitives.ReadUInt16LittleEndian(node[e..]);
        valOff = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 2)..]);
        keyLen = fixedKeyLen; valLen = fixedValLen;
      } else {
        var e = tocAbs + (int)k * 8;
        if (e + 8 > node.Length) break;
        keyOff = BinaryPrimitives.ReadUInt16LittleEndian(node[e..]);
        keyLen = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 2)..]);
        valOff = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 4)..]);
        valLen = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 6)..]);
      }
      if (keyLen <= 0 || valLen <= 0) continue;
      var keyAbs = keyAreaStart + keyOff;
      var valAbs = valAreaEnd - valOff;
      if (keyAbs < 0 || keyAbs + keyLen > node.Length) continue;
      if (valAbs < 0 || valAbs + valLen > node.Length) continue;

      var keyBuf = new byte[keyLen];
      node.Slice(keyAbs, keyLen).CopyTo(keyBuf);
      var valBuf = new byte[valLen];
      node.Slice(valAbs, valLen).CopyTo(valBuf);
      results.Add((keyBuf, valBuf));
    }
    return results;
  }

  // ── FS-tree parsing ─────────────────────────────────────────────────────

  /// <summary>
  /// Collects every leaf (key, value) record of a B-tree, descending internal
  /// (btn_level &gt; 0) nodes via their child block addresses. The writer stores
  /// internal-node values as 8-byte physical block numbers of the child nodes,
  /// so the walk follows those addresses directly. A single root-leaf node is the
  /// common case and is returned as-is. A visited set guards against malformed
  /// cyclic child references.
  /// </summary>
  /// <param name="omapPhys">
  /// The object map that turns a child's identifier into its block, for a tree
  /// whose nodes are virtual. Zero for a physical tree, whose children are named
  /// by their blocks outright.
  /// </param>
  private List<(byte[] Key, byte[] Value)> CollectAllLeafRecords(long rootPhys, ulong omapPhys = 0) {
    var results = new List<(byte[], byte[])>();
    var visited = new HashSet<long>();
    Descend(rootPhys, isRoot: true, 0, 0);
    return results;

    void Descend(long blockNum, bool isRoot, int fixedKeyLen, int fixedValLen) {
      if (!visited.Add(blockNum)) return;
      var node = this.BlockSpan(blockNum);
      var level = BinaryPrimitives.ReadUInt16LittleEndian(node[34..]);
      var nodeFlags = BinaryPrimitives.ReadUInt16LittleEndian(node[32..]);

      // The root states the record sizes for the whole tree; carry them down so
      // a node below it can be read at all.
      if ((nodeFlags & BTNODE_FIXED_KV_SIZE) != 0 && (isRoot || (nodeFlags & BTNODE_ROOT) != 0)) {
        var info = node.Length - 40;
        fixedKeyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 8)..]);
        fixedValLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 12)..]);
      }

      if (level == 0) {
        results.AddRange(EnumerateBtreeLeafRecords(node, isRoot, fixedKeyLen, fixedValLen));
        return;
      }
      // Internal node: each value is the physical block number of a child node.
      foreach (var (_, value) in EnumerateBtreeLeafRecords(node, isRoot, fixedKeyLen, fixedValLen)) {
        if (value.Length < 8) continue;
        var child = BinaryPrimitives.ReadUInt64LittleEndian(value);
        var childBlock = omapPhys == 0
          ? (long)child
          : (long)this.ResolveOidViaOmap(omapPhys, child);
        if (childBlock == 0) continue;
        Descend(childBlock, isRoot: false, fixedKeyLen, fixedValLen);
      }
    }
  }

  private void ParseFsTree(long treePhys, ulong volOmapPhys) {
    // Collect: inode name + size + isDir, drec: parent -> (name, child_ino), file_extent: ino -> (size, paddr).
    var inodeName = new Dictionary<ulong, string>();
    var inodeSize = new Dictionary<ulong, long>();
    var inodeIsDir = new Dictionary<ulong, bool>();
    var inodeIsLink = new Dictionary<ulong, bool>();
    var symlinkTarget = new Dictionary<ulong, string>();
    var drec = new List<(ulong Parent, string Name, ulong ChildIno, bool IsDir)>();
    // FILE_EXTENT records are keyed by the owning stream's id (an inode's private_id,
    // or an attribute stream's xattr_obj_id) and the logical offset; a file may have many.
    var fileExtents = new Dictionary<ulong, List<ApfsExtent>>();
    var inodePrivateId = new Dictionary<ulong, ulong>();
    var inodeCompressedFlag = new Dictionary<ulong, bool>();
    var decmpfs = new Dictionary<ulong, byte[]>();
    var resourceForkInline = new Dictionary<ulong, byte[]>();
    var resourceForkStream = new Dictionary<ulong, (ulong StreamId, long Size)>();
    var inodeTimestamps = new Dictionary<ulong, DateTime>();

    foreach (var (key, val) in this.CollectAllLeafRecords(treePhys, volOmapPhys)) {
      if (key.Length < 8) continue;
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(key);
      var keyType = (int)(oidAndType >> 60);
      var oid = oidAndType & 0x0FFFFFFFFFFFFFFFUL;

      switch (keyType) {
        case APFS_TYPE_INODE:
          if (val.Length < 88) break;
          var mode = BinaryPrimitives.ReadUInt16LittleEndian(val.AsSpan(80));
          // The length is in the data-stream extended field; the word at 84 is
          // the uncompressed size, which only a compressed file fills in.
          var size = ApfsInodeRecord.ReadDataStreamSize(val);
          inodeIsDir[oid] = (mode & 0xF000) == S_IFDIR;
          inodeIsLink[oid] = (mode & 0xF000) == S_IFLNK;
          inodeSize[oid] = size;
          inodePrivateId[oid] = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(8));
          // bsd_flags @68: UF_COMPRESSED (0x20) marks transparent (decmpfs) compression.
          inodeCompressedFlag[oid] = (BinaryPrimitives.ReadUInt32LittleEndian(val.AsSpan(68)) & UF_COMPRESSED) != 0;
          var mtimeNs = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(24));
          if (mtimeNs > 0) {
            var ms = (long)(mtimeNs / 1_000_000UL);
            inodeTimestamps[oid] = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
          }
          break;

        case APFS_TYPE_DIR_REC:
          if (val.Length < 18) break;
          if (!ApfsDrecKey.TryReadName(key, this._hashedNames, out var name)) break;
          var childIno = BinaryPrimitives.ReadUInt64LittleEndian(val);
          var flags = BinaryPrimitives.ReadUInt16LittleEndian(val.AsSpan(16));
          var dirType = flags & APFS_DIR_REC_FLAGS_MASK;
          drec.Add((oid, name, childIno, dirType == DT_DIR));
          break;

        case APFS_TYPE_FILE_EXTENT:
          // j_file_extent_key_t: logical_addr @8. j_file_extent_val_t: len_and_flags @0, phys_block_num @8.
          if (val.Length < 16 || key.Length < 16) break;
          var lenAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(val);
          var len = (long)(lenAndFlags & 0x00FFFFFFFFFFFFFFUL);
          var paddr = BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(8));
          var logical = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8));
          if (!fileExtents.TryGetValue(oid, out var extentList))
            fileExtents[oid] = extentList = [];
          extentList.Add(new ApfsExtent(logical, len, paddr));
          break;

        case APFS_TYPE_XATTR:
          // j_xattr_key_t: name_len (u16) @8, name[] @10 (NUL-terminated).
          // j_xattr_val_t: flags (u16) @0, xdata_len (u16) @2, xdata[] @4.
          // A symlink's target is the embedded xattr named "com.apple.fs.symlink".
          if (key.Length < 12 || val.Length < 4) break;
          var xattrNameLen = BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(8));
          if (xattrNameLen <= 0 || 10 + xattrNameLen > key.Length) break;
          var xattrName = Encoding.UTF8.GetString(key, 10, xattrNameLen).TrimEnd('\0');
          var xattrFlags = BinaryPrimitives.ReadUInt16LittleEndian(val);
          var xdataLen = BinaryPrimitives.ReadUInt16LittleEndian(val.AsSpan(2));
          var take = Math.Min(xdataLen, val.Length - 4);
          if (take <= 0) break;
          var embedded = (xattrFlags & XATTR_DATA_EMBEDDED) != 0;
          switch (xattrName) {
            case SYMLINK_XATTR_NAME when embedded:
              symlinkTarget[oid] = Encoding.UTF8.GetString(val, 4, take).TrimEnd('\0');
              break;
            case DECMPFS_XATTR_NAME when embedded:
              decmpfs[oid] = val.AsSpan(4, take).ToArray();
              break;
            case RESOURCE_FORK_XATTR_NAME when embedded:
              resourceForkInline[oid] = val.AsSpan(4, take).ToArray();
              break;
            case RESOURCE_FORK_XATTR_NAME when (xattrFlags & XATTR_DATA_STREAM) != 0 && take >= 16:
              // j_xattr_dstream_t: xattr_obj_id (u64), then j_dstream_t whose first field is size.
              resourceForkStream[oid] = (BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(4)),
                (long)BinaryPrimitives.ReadUInt64LittleEndian(val.AsSpan(12)));
              break;
          }
          break;
      }
    }

    // Index directory records by their parent directory inode so the tree can be
    // walked from the root. APFS stores a child's name only in the DIR_REC under
    // its parent, so full paths are reconstructed by recursive descent.
    var childrenByParent = new Dictionary<ulong, List<(string Name, ulong ChildIno, bool IsDir)>>();
    foreach (var (parent, name, childIno, isDir) in drec) {
      if (string.IsNullOrEmpty(name)) continue;
      if (!childrenByParent.TryGetValue(parent, out var bucket))
        childrenByParent[parent] = bucket = [];
      bucket.Add((name, childIno, isDir));
    }

    // Recurse from the root directory, building full nested paths. A visited set
    // guards against malformed cyclic DIR_REC chains.
    var visited = new HashSet<ulong>();
    EmitSubtree(APFS_ROOT_DIR_INO_NUM, parentPath: "");
    return;

    void EmitSubtree(ulong dirIno, string parentPath) {
      if (!visited.Add(dirIno)) return;
      if (!childrenByParent.TryGetValue(dirIno, out var children)) return;

      foreach (var (name, childIno, drecIsDir) in children) {
        var dir = drecIsDir || inodeIsDir.GetValueOrDefault(childIno, false);
        var isLink = inodeIsLink.GetValueOrDefault(childIno, false);
        var fullPath = parentPath.Length == 0 ? name : parentPath + "/" + name;
        DateTime? ts = inodeTimestamps.TryGetValue(childIno, out var t) ? t : null;

        ulong firstBlock = 0;
        long extentLen = 0;
        var sz = inodeSize.GetValueOrDefault(childIno, 0);
        IReadOnlyList<ApfsExtent> extents = [];
        byte[]? compression = null;
        IReadOnlyList<ApfsExtent>? forkExtents = null;
        long forkSize = 0;
        if (!dir && !isLink) {
          var streamId = inodePrivateId.GetValueOrDefault(childIno, childIno);
          if (fileExtents.TryGetValue(streamId, out var list) || fileExtents.TryGetValue(childIno, out list))
            extents = [.. list.OrderBy(e => e.Logical)];
          if (inodeCompressedFlag.GetValueOrDefault(childIno) && decmpfs.TryGetValue(childIno, out var cmp)) {
            compression = cmp;
            // A compressed file's data stream is empty; its length is the decmpfs header's.
            sz = HfsPlusDecmpfsSize(cmp);
            if (resourceForkStream.TryGetValue(childIno, out var fork)) {
              forkSize = fork.Size;
              forkExtents = fileExtents.TryGetValue(fork.StreamId, out var fl) ? [.. fl.OrderBy(e => e.Logical)] : [];
            }
          } else if (extents.Count == 1 && extents[0].Logical == 0 && extents[0].PhysBlock != 0 && extents[0].Length >= sz) {
            firstBlock = extents[0].PhysBlock;
            extentLen = extents[0].Length;
          }
        }

        string? linkTarget = null;
        if (isLink) {
          linkTarget = symlinkTarget.GetValueOrDefault(childIno);
          // A symlink's own size is the target-path byte length (on-disk truth).
          if (linkTarget != null) sz = Encoding.UTF8.GetByteCount(linkTarget);
        }

        this._entries.Add(new ApfsEntry {
          Name = fullPath,
          Size = sz,
          IsDirectory = dir,
          IsSymlink = isLink,
          LinkTarget = linkTarget,
          ObjectId = childIno,
          LastModified = ts,
          FirstBlock = firstBlock,
          ExtentLength = extentLen,
          Extents = extents,
          Decmpfs = compression,
          ResourceForkInline = compression is null ? null : resourceForkInline.GetValueOrDefault(childIno),
          ResourceForkExtents = forkExtents,
          ResourceForkSize = forkSize,
        });

        if (dir)
          EmitSubtree(childIno, fullPath);
      }
    }
  }


  /// <summary>The uncompressed length a decmpfs attribute declares (0 when malformed).</summary>
  private static long HfsPlusDecmpfsSize(byte[] attribute)
    => FileSystem.HfsPlus.HfsPlusDecmpfs.TryReadSize(attribute, out var size) ? size : 0;

  /// <summary>
  /// Copies an entry's bytes into <paramref name="destination" /> a block at a
  /// time. An APFS file may be far larger than the byte[] <see cref="Extract" />
  /// returns could hold.
  /// </summary>
  /// <exception cref="InvalidDataException">An extent lies outside the image or the compressed data is corrupt.</exception>
  /// <exception cref="NotSupportedException">The file uses a decmpfs method no decoder exists for.</exception>
  public void ExtractTo(ApfsEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsSymlink) {
      var target = Encoding.UTF8.GetBytes(entry.LinkTarget ?? "");
      destination.Write(target, 0, target.Length);
      return;
    }
    if (entry.IsDirectory) return;
    if (entry.Decmpfs is { } attribute) {
      var bytes = this.Decompress(entry, attribute);
      destination.Write(bytes, 0, bytes.Length);
      return;
    }
    this.CopyExtents(entry.Extents, entry.Size, destination);
  }

  /// <summary>
  /// Extracts a file's bytes: its data stream's extents in logical order (gaps and
  /// sparse extents read as zeros), or, for a transparently compressed file, the
  /// decoded decmpfs content.
  /// </summary>
  /// <exception cref="InvalidDataException">An extent lies outside the image or the compressed data is corrupt.</exception>
  /// <exception cref="NotSupportedException">The file uses a decmpfs method no decoder exists for.</exception>
  public byte[] Extract(ApfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    // A symlink's honest content is its target path text.
    if (entry.IsSymlink) return Encoding.UTF8.GetBytes(entry.LinkTarget ?? "");
    if (entry.IsDirectory) return [];
    if (entry.Decmpfs is { } attribute) return this.Decompress(entry, attribute);
    if (entry.Size > Array.MaxLength) throw new NotSupportedException("APFS: file is too large for a byte array; use ExtractTo.");
    using var ms = new MemoryStream();
    this.CopyExtents(entry.Extents, entry.Size, ms);
    return ms.ToArray();
  }

  private byte[] Decompress(ApfsEntry entry, byte[] attribute)
    => FileSystem.HfsPlus.HfsPlusDecmpfs.Decode(attribute, () => {
      if (entry.ResourceForkInline is { } inline) return inline;
      if (entry.ResourceForkExtents is { } extents) {
        using var ms = new MemoryStream();
        this.CopyExtents(extents, entry.ResourceForkSize, ms);
        return ms.ToArray();
      }
      throw new InvalidDataException($"APFS: '{entry.Name}' is compressed into a resource fork that is missing.");
    });

  /// <summary>Writes exactly <paramref name="size" /> bytes assembled from <paramref name="extents" />.</summary>
  private void CopyExtents(IReadOnlyList<ApfsExtent> extents, long size, Stream destination) {
    long written = 0;
    var zeros = new byte[(int)Math.Min(this._blockSize * 16L, 1 << 20)];
    void Zero(long upTo) {
      while (written < upTo) {
        var n = (int)Math.Min(zeros.Length, upTo - written);
        destination.Write(zeros, 0, n);
        written += n;
      }
    }

    foreach (var extent in extents) {
      if (written >= size) break;
      var logical = (long)extent.Logical;
      if (logical < written) continue; // overlapping record: the earlier one wins
      Zero(Math.Min(logical, size));
      var take = Math.Min(extent.Length, size - written);
      if (take <= 0) continue;
      if (extent.PhysBlock == 0) {
        Zero(written + take);
        continue;
      }
      var offset = checked((long)extent.PhysBlock * this._blockSize);
      if (offset < 0 || offset + take > this._data.Length)
        throw new InvalidDataException($"APFS: extent at block {extent.PhysBlock} lies outside the image.");
      this._data.CopyTo(offset, destination, take);
      written += take;
    }
    Zero(size);
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}

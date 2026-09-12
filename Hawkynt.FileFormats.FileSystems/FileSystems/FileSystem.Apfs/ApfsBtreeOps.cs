#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Generic APFS B-tree operations shared by the modifier and the validator.
/// <para>
/// Provides three primitives:
/// <list type="bullet">
///   <item><description><see cref="CollectAllLeafRecords"/> — walks a B-tree top-down and returns every
///     leaf record as a flat list, descending internal nodes via their 8-byte child
///     block addresses (the layout the writer emits).</description></item>
///   <item><description><see cref="ResolveOidViaOmapTree"/> — given an OMAP B-tree root and a virtual
///     OID, descends only the path needed to find the latest-xid record and returns
///     the physical block address it points at.</description></item>
///   <item><description><see cref="RebuildBtreeOnFixedRoot"/> — top-down rebuild that partitions the
///     supplied (sorted-once) records into one or more leaf nodes, builds whatever
///     internal levels are needed to index them, and writes the tree onto a fixed
///     root block (extra nodes are tail-allocated through <see cref="ApfsBlockAllocator"/>).
///     This is the engine behind both omap-split and FS-tree-split mutations.</description></item>
/// </list>
/// </para>
/// <para>
/// All nodes carry valid Fletcher-64 checksums after the rebuild and use the
/// strict APFS B-tree key ordering rule supplied via the <c>keyComparer</c>
/// delegate. Internal-node separator keys are <i>verbatim copies of the first
/// key of each child leaf</i> — this matches what the writer emits and what
/// libfsapfs accepts during structural reads.
/// </para>
/// </summary>
internal static class ApfsBtreeOps {
  private const uint BlockSize = DEFAULT_BLOCK_SIZE;
  private const int BtnHeaderEnd = 56;
  private const int BtreeInfoSize = 40;
  private const int TocEntrySize = 8;

  /// <summary>Table slots a node reserves however few it holds.</summary>
  private const int MinimumTableSlots = 16;

  /// <summary>A slot in a fixed-size tree: two offsets and no lengths.</summary>
  private const int FixedTocEntrySize = 4;

  /// <summary>A (key, value) pair stored in a B-tree leaf.</summary>
  internal readonly struct Record(byte[] key, byte[] value) {
    public byte[] Key { get; } = key;
    public byte[] Value { get; } = value;
  }

  /// <summary>
  /// Comparison used to sort records in APFS canonical order
  /// (oid asc, type asc, then raw key tail) for FS-tree records.
  /// </summary>
  public static int CompareFsKeys(byte[] a, byte[] b) {
    if (a.Length < 8 || b.Length < 8) return a.Length.CompareTo(b.Length);
    var oa = BinaryPrimitives.ReadUInt64LittleEndian(a);
    var ob = BinaryPrimitives.ReadUInt64LittleEndian(b);
    var oidA = oa & 0x0FFFFFFFFFFFFFFFUL;
    var oidB = ob & 0x0FFFFFFFFFFFFFFFUL;
    var cmp = oidA.CompareTo(oidB);
    if (cmp != 0) return cmp;
    var ta = (int)(oa >> 60);
    var tb = (int)(ob >> 60);
    cmp = ta.CompareTo(tb);
    if (cmp != 0) return cmp;

    // A directory entry's key is a two-byte name length and then the name, so
    // comparing the tail byte for byte compares the lengths first — which puts
    // "root" before "private-dir" and leaves the tree in an order nothing else
    // agrees with. Directory entries are ordered by their names.
    if (ta == APFS_TYPE_DIR_REC && a.Length >= 10 && b.Length >= 10)
      return a.AsSpan(10).SequenceCompareTo(b.AsSpan(10));

    return a.AsSpan(8).SequenceCompareTo(b.AsSpan(8));
  }

  /// <summary>
  /// Comparison used to sort OMAP records: (oid asc, xid asc). Per spec the
  /// latest-xid record wins for a given oid, so when adding new entries we
  /// strictly sort ascending and the reader picks the highest xid on lookup.
  /// </summary>
  public static int CompareOmapKeys(byte[] a, byte[] b) {
    if (a.Length < 16 || b.Length < 16) return a.Length.CompareTo(b.Length);
    var oidA = BinaryPrimitives.ReadUInt64LittleEndian(a);
    var oidB = BinaryPrimitives.ReadUInt64LittleEndian(b);
    var cmp = oidA.CompareTo(oidB);
    if (cmp != 0) return cmp;
    var xidA = BinaryPrimitives.ReadUInt64LittleEndian(a.AsSpan(8));
    var xidB = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8));
    return xidA.CompareTo(xidB);
  }

  /// <summary>
  /// Builds an OMAP (key, value) record: <c>omap_key_t {oid; xid}</c>,
  /// <c>omap_val_t {flags; size=4096; paddr}</c>.
  /// </summary>
  public static Record BuildOmapRecord(ulong oid, ulong xid, ulong physBlock) {
    var k = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(k, oid);
    BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8), xid);
    var v = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(v, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(4), BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), physBlock);
    return new Record(k, v);
  }

  // ── Read-side B-tree walk ───────────────────────────────────────────────

  /// <summary>
  /// Walks a B-tree from <paramref name="rootPhys"/> and returns every leaf
  /// record in document order. Internal-node values are 8-byte big-LE physical
  /// block addresses of the next-level node. A visited set guards against
  /// malformed cycles.
  /// </summary>
  /// <param name="omapTreePhys">
  /// The object map's B-tree root, for a tree whose nodes are virtual: its
  /// children are identifiers and this is what turns one into a block. Zero for
  /// a physical tree, whose children name their blocks outright.
  /// </param>
  public static List<Record> CollectAllLeafRecords(byte[] image, ulong rootPhys,
      ulong omapTreePhys = 0) {
    var results = new List<Record>();
    var visited = new HashSet<ulong>();
    Descend(rootPhys, isRoot: true);
    return results;

    void Descend(ulong blockNum, bool isRoot) {
      if (!visited.Add(blockNum)) return;
      if ((long)blockNum * BlockSize + BlockSize > image.Length) return;
      var node = image.AsSpan((int)(blockNum * BlockSize), (int)BlockSize);
      var level = BinaryPrimitives.ReadUInt16LittleEndian(node[34..]);
      var slots = ReadSlots(node, isRoot);
      if (level == 0) {
        foreach (var s in slots)
          results.Add(new Record(s.Key, s.Value));
        return;
      }
      foreach (var s in slots) {
        if (s.Value.Length < 8) continue;
        var child = BinaryPrimitives.ReadUInt64LittleEndian(s.Value);
        var childAddr = omapTreePhys == 0 ? child : ResolveOidViaOmapTree(image, omapTreePhys, child);
        if (childAddr == 0) continue;
        Descend(childAddr, isRoot: false);
      }
    }
  }

  /// <summary>
  /// Walks the OMAP B-tree rooted at <paramref name="treePhys"/> and returns the
  /// physical block (paddr) for <paramref name="virtOid"/>. When the OID has
  /// multiple records, the latest xid wins. Returns 0 when not found.
  /// </summary>
  public static ulong ResolveOidViaOmapTree(byte[] image, ulong treePhys, ulong virtOid) {
    ulong bestXid = 0;
    ulong bestPaddr = 0;
    foreach (var rec in CollectAllLeafRecords(image, treePhys)) {
      if (rec.Key.Length < 16 || rec.Value.Length < 16) continue;
      var oid = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key);
      if (oid != virtOid) continue;
      var xid = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key.AsSpan(8));
      if (xid < bestXid && bestPaddr != 0) continue;
      bestXid = xid;
      bestPaddr = BinaryPrimitives.ReadUInt64LittleEndian(rec.Value.AsSpan(8));
    }
    return bestPaddr;
  }

  /// <summary>Decodes every slot (key, value) from a B-tree node's TOC.</summary>
  private static List<(byte[] Key, byte[] Value)> ReadSlots(ReadOnlySpan<byte> node, bool isRoot) {
    var result = new List<(byte[], byte[])>();
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(node[32..]);
    var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(node[36..]);
    var tableOff = BinaryPrimitives.ReadUInt16LittleEndian(node[40..]);
    var tableLen = BinaryPrimitives.ReadUInt16LittleEndian(node[42..]);
    var tocAbs = BtnHeaderEnd + tableOff;
    var keyAreaStart = tocAbs + tableLen;
    var valAreaEnd = isRoot || (flags & BTNODE_ROOT) != 0
      ? node.Length - BtreeInfoSize
      : node.Length;
    var isFixed = (flags & BTNODE_FIXED_KV_SIZE) != 0;

    // A fixed-size tree states its one key size and one value size in the root's
    // footer, because the slots carry only offsets. This used to take the fixed
    // branch and then set both lengths to zero, which the guard below reads as an
    // empty slot — so every record in such a node was skipped and the tree looked
    // empty. An object map is precisely the tree laid out this way.
    var fixedKeyLen = 0;
    var fixedValLen = 0;
    if (isFixed) {
      var info = node.Length - BtreeInfoSize;
      fixedKeyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 8)..]);
      fixedValLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 12)..]);
    }

    for (uint i = 0; i < nkeys; i++) {
      int keyOff, keyLen, valOff, valLen;
      if (isFixed) {
        var e = tocAbs + (int)i * 4;
        if (e + 4 > node.Length) break;
        keyOff = BinaryPrimitives.ReadUInt16LittleEndian(node[e..]);
        valOff = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 2)..]);
        keyLen = fixedKeyLen; valLen = fixedValLen;
      } else {
        var e = tocAbs + (int)i * TocEntrySize;
        if (e + TocEntrySize > node.Length) break;
        keyOff = BinaryPrimitives.ReadUInt16LittleEndian(node[e..]);
        keyLen = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 2)..]);
        valOff = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 4)..]);
        valLen = BinaryPrimitives.ReadUInt16LittleEndian(node[(e + 6)..]);
      }
      if (keyLen <= 0 || valLen <= 0) continue;
      var keyAbs = keyAreaStart + keyOff;
      var valAbs = valAreaEnd - valOff;
      if (keyAbs + keyLen > node.Length || valAbs + valLen > node.Length) continue;
      result.Add((node.Slice(keyAbs, keyLen).ToArray(), node.Slice(valAbs, valLen).ToArray()));
    }
    return result;
  }

  // ── Write-side B-tree rebuild (with splits) ─────────────────────────────

  /// <summary>
  /// Rebuilds the entire B-tree top-down onto a fixed root block address.
  /// <para>
  /// Steps: (1) sort the supplied records once; (2) partition them into the
  /// minimum number of leaf nodes that each fit in one block; (3) if the
  /// partition produced more than one leaf, write the extra leaves onto fresh
  /// tail-allocated blocks and emit internal index nodes pointing at them,
  /// repeating bottom-up until exactly one index node remains; (4) write that
  /// final index node (or the single leaf) onto <paramref name="rootBlock"/>.
  /// </para>
  /// <para>
  /// This is the engine behind FS-tree split and OMAP split: when a leaf is
  /// full a new sibling leaf is allocated, the parent gains a new separator,
  /// and if the parent itself overflows the tree grows another level. The
  /// algorithm scales to arbitrary depth — the writer caps at level 2 but the
  /// modifier has no such cap.
  /// </para>
  /// </summary>
  /// <param name="virtualNodes">
  /// Filled with every non-root node's identifier and the block it sits on, when
  /// the tree's nodes are virtual. Those need an entry in the object map, because
  /// a virtual tree names its children by identifier and the map is what turns
  /// one back into a block.
  /// </param>
  public static void RebuildBtreeOnFixedRoot(ref byte[] image, ApfsBlockAllocator allocator,
      long rootBlock, ulong rootOid, uint type, ulong xid,
      List<Record> records, Comparison<byte[]> keyComparer,
      List<(ulong Oid, ulong Block)>? virtualNodes = null, uint subtype = 0,
      bool fixedKv = false, int leafKeySize = 0, int leafValueSize = 0) {
    // OBJ_VIRTUAL is zero, so a tree is virtual when it is neither of the others.
    var isVirtual = (type & (OBJ_PHYSICAL | OBJ_EPHEMERAL)) == 0;
    ulong NodeOid(ulong block) => isVirtual ? OID_RESERVED_COUNT + block : block;
    // Sort records once into canonical key order.
    records.Sort((a, b) => keyComparer(a.Key, b.Key));

    // Try the single root-leaf case first.
    if (FitsInRootLeaf(records)) {
      WriteLeafNode(image.AsSpan((int)(rootBlock * BlockSize), (int)BlockSize),
        records, rootOid, type, xid, isRoot: true, nodeCount: 1, totalKeys: (ulong)records.Count,
        subtype: subtype, fixedKv: fixedKv, leafKeySize: leafKeySize, leafValueSize: leafValueSize);
      return;
    }

    // Partition into non-root leaves.
    var leafPartitions = PartitionIntoLeaves(records, isRoot: false);
    var leafBlocks = new List<ulong>(leafPartitions.Count);
    var leafFirstKeys = new List<byte[]>(leafPartitions.Count);
    for (var i = 0; i < leafPartitions.Count; i++) {
      var leafBlock = allocator.AllocateNode(ref image);
      var leafOid = NodeOid(leafBlock);
      leafBlocks.Add(leafOid);                        // what the parent names it by
      leafFirstKeys.Add(leafPartitions[i][0].Key);
      // Reported whether or not the tree is virtual: the caller counts the nodes
      // a volume owns, and only names the virtual ones in its map.
      virtualNodes?.Add((leafOid, leafBlock));
      WriteLeafNode(image.AsSpan((int)(leafBlock * BlockSize), (int)BlockSize),
        leafPartitions[i], leafOid, type, xid, isRoot: false,
        nodeCount: 0, totalKeys: 0, subtype: subtype,
        fixedKv: fixedKv, leafKeySize: leafKeySize, leafValueSize: leafValueSize);
    }

    // Build internal levels bottom-up.
    var currentLevel = 1;
    var currentKeys = leafFirstKeys;
    var currentChildren = leafBlocks;
    var totalNodes = (ulong)leafBlocks.Count;

    while (true) {
      // Try to fit everything in the root internal node.
      if (FitsInRootInternal(currentKeys)) {
        WriteInternalNode(image.AsSpan((int)(rootBlock * BlockSize), (int)BlockSize),
          currentKeys, currentChildren, rootOid, type, xid,
          level: (ushort)currentLevel, isRoot: true,
          nodeCount: totalNodes + 1, totalKeys: (ulong)records.Count, subtype: subtype,
          fixedKv: fixedKv, leafKeySize: leafKeySize, leafValueSize: leafValueSize);
        return;
      }

      // Otherwise this level overflows too — partition it into multiple internal
      // nodes and build another level on top.
      var (parentKeys, parentBlocks, addedNodes) = PartitionAndWriteInternalLevel(
        ref image, allocator, currentKeys, currentChildren, type, xid, (ushort)currentLevel,
        NodeOid, virtualNodes, subtype, fixedKv, leafKeySize, leafValueSize);
      currentKeys = parentKeys;
      currentChildren = parentBlocks;
      currentLevel++;
      totalNodes += addedNodes;
    }
  }

  /// <summary>
  /// Partitions an over-large level of (key, child) pairs into a series of
  /// internal nodes, writes those nodes to fresh blocks, and returns the
  /// (firstKey, blockAddr) list ready to be indexed by the next level up.
  /// </summary>
  private static (List<byte[]> ParentKeys, List<ulong> ParentBlocks, ulong AddedNodes)
      PartitionAndWriteInternalLevel(ref byte[] image, ApfsBlockAllocator allocator,
        List<byte[]> keys, List<ulong> children, uint type, ulong xid, ushort level,
        Func<ulong, ulong> nodeOidOf, List<(ulong Oid, ulong Block)>? virtualNodes, uint subtype,
        bool fixedKv, int leafKeySize, int leafValueSize) {
    var parentKeys = new List<byte[]>();
    var parentBlocks = new List<ulong>();
    var added = 0UL;
    var nodeCap = NodePayloadCapacity(isRoot: false);
    var startIdx = 0;
    while (startIdx < keys.Count) {
      // Greedily pack as many entries as fit in one internal node.
      var used = 0;
      var endIdx = startIdx;
      while (endIdx < keys.Count) {
        var cost = TocEntrySize + keys[endIdx].Length + 8;
        if (used + cost > nodeCap && endIdx > startIdx) break;
        used += cost;
        endIdx++;
      }
      var sliceKeys = keys.GetRange(startIdx, endIdx - startIdx);
      var sliceChildren = children.GetRange(startIdx, endIdx - startIdx);
      var nodeBlock = allocator.AllocateNode(ref image);
      var nodeOid = nodeOidOf(nodeBlock);
      virtualNodes?.Add((nodeOid, nodeBlock));
      WriteInternalNode(image.AsSpan((int)(nodeBlock * BlockSize), (int)BlockSize),
        sliceKeys, sliceChildren, nodeOid, type, xid,
        level: level, isRoot: false, nodeCount: 0, totalKeys: 0, subtype: subtype,
        fixedKv: fixedKv, leafKeySize: leafKeySize, leafValueSize: leafValueSize);
      parentKeys.Add(sliceKeys[0]);
      parentBlocks.Add(nodeOid);
      added++;
      startIdx = endIdx;
    }
    return (parentKeys, parentBlocks, added);
  }

  /// <summary>
  /// Bytes a node can devote to TOC + keys + values after the obj_phys header,
  /// the btn_phys header and (for a root node) the trailing btree_info footer.
  /// </summary>
  private static int NodePayloadCapacity(bool isRoot) =>
    (int)BlockSize - BtnHeaderEnd - (isRoot ? BtreeInfoSize : 0);

  /// <summary>Returns true when the entire record set fits in a single root-leaf block.</summary>
  private static bool FitsInRootLeaf(List<Record> records) {
    var cap = NodePayloadCapacity(isRoot: true);
    var used = 0;
    foreach (var r in records) used += TocEntrySize + r.Key.Length + r.Value.Length;
    return used <= cap;
  }

  /// <summary>Returns true when the index entries fit in one root internal block.</summary>
  private static bool FitsInRootInternal(List<byte[]> keys) {
    var cap = NodePayloadCapacity(isRoot: true);
    var used = 0;
    foreach (var k in keys) used += TocEntrySize + k.Length + 8;
    return used <= cap;
  }

  /// <summary>
  /// Greedy left-to-right partition of <paramref name="records"/> into the
  /// minimum number of leaf nodes such that each leaf fits in one block. The
  /// records are not reordered, so each partition's first key is a valid
  /// separator for the parent index.
  /// </summary>
  private static List<List<Record>> PartitionIntoLeaves(List<Record> records, bool isRoot) {
    var partitions = new List<List<Record>>();
    if (records.Count == 0) {
      partitions.Add([]);
      return partitions;
    }
    var cap = NodePayloadCapacity(isRoot);
    var current = new List<Record>();
    var used = 0;
    foreach (var r in records) {
      var cost = TocEntrySize + r.Key.Length + r.Value.Length;
      if (current.Count > 0 && used + cost > cap) {
        partitions.Add(current);
        current = [];
        used = 0;
      }
      current.Add(r);
      used += cost;
    }
    if (current.Count > 0) partitions.Add(current);
    return partitions;
  }

  // ── Node writers ────────────────────────────────────────────────────────

  /// <summary>Writes a leaf node (level 0) containing <paramref name="records"/>.</summary>
  private static void WriteLeafNode(Span<byte> block, List<Record> records, ulong oid, uint type, ulong xid,
      bool isRoot, ulong nodeCount, ulong totalKeys, uint subtype,
      bool fixedKv, int leafKeySize, int leafValueSize) {
    var flags = isRoot ? (ushort)(BTNODE_ROOT | BTNODE_LEAF) : (ushort)BTNODE_LEAF;
    WriteBtreeNodeRaw(block, records.Select(r => (r.Key, r.Value)).ToList(),
      oid, type, xid, flags, level: 0, isRoot, nodeCount, totalKeys, subtype,
      fixedKv, leafKeySize, leafValueSize);
  }

  /// <summary>
  /// Writes an internal node (level > 0) whose values are 8-byte physical
  /// addresses of child blocks. Each <c>(key, child)</c> becomes one TOC slot
  /// with the key copied verbatim and the value containing the child block
  /// number in little-endian.
  /// </summary>
  private static void WriteInternalNode(Span<byte> block, List<byte[]> keys, List<ulong> children, ulong oid, uint type, ulong xid,
      ushort level, bool isRoot, ulong nodeCount, ulong totalKeys, uint subtype,
      bool fixedKv, int leafKeySize, int leafValueSize) {
    var slots = new List<(byte[], byte[])>(keys.Count);
    for (var i = 0; i < keys.Count; i++) {
      var val = new byte[8];
      BinaryPrimitives.WriteUInt64LittleEndian(val, children[i]);
      slots.Add((keys[i], val));
    }
    var flags = isRoot ? (ushort)BTNODE_ROOT : (ushort)0;
    // An index node's values are child identifiers, eight bytes each.
    WriteBtreeNodeRaw(block, slots, oid, type, xid, flags, level, isRoot, nodeCount, totalKeys,
      subtype, fixedKv, leafKeySize, 8);
  }

  /// <summary>
  /// Shared serializer for any B-tree node (root / leaf / internal). Writes
  /// the object header, btn_phys header, TOC, forward-growing key area,
  /// backward-growing value area, optional btree_info footer (root only), and
  /// stamps the Fletcher-64 checksum.
  /// </summary>
  private static void WriteBtreeNodeRaw(Span<byte> block, List<(byte[] Key, byte[] Value)> slots,
      ulong oid, uint type, ulong xid, ushort flags, ushort level, bool isRoot, ulong nodeCount,
      ulong totalKeys, uint subtype, bool fixedKv, int leafKeySize, int leafValueSize) {
    block.Clear();

    // obj_phys_t header (32 bytes).
    BinaryPrimitives.WriteUInt64LittleEndian(block[8..], oid);
    BinaryPrimitives.WriteUInt64LittleEndian(block[16..], xid);
    // A node below the root is a btree_node; which tree it belongs to is the
    // subtype. Both used to be lost here — every rebuilt node claimed to be a
    // root of no tree in particular.
    BinaryPrimitives.WriteUInt32LittleEndian(block[24..],
      isRoot ? type : (type & ~(uint)0xFFFF) | OBJECT_TYPE_BTREE_NODE);
    BinaryPrimitives.WriteUInt32LittleEndian(block[28..], subtype);

    // btn_phys header at offset 32.
    // The node has to say it is fixed before its header is written, not after.
    if (fixedKv) flags |= BTNODE_FIXED_KV_SIZE;
    BinaryPrimitives.WriteUInt16LittleEndian(block[32..], flags);
    BinaryPrimitives.WriteUInt16LittleEndian(block[34..], level);
    BinaryPrimitives.WriteUInt32LittleEndian(block[36..], (uint)slots.Count);

    // A tree whose records are all one size keeps two offsets per slot instead
    // of an offset and a length for each half, and reserves table space for as
    // many records as the node could hold. Every object map is laid out that way.
    var tocEntrySize = fixedKv ? FixedTocEntrySize : TocEntrySize;

    var tocOff = BtnHeaderEnd;
    var tocLen = slots.Count * tocEntrySize;
    if (fixedKv) {
      var keySize = 0;
      var valSize = 0;
      foreach (var (k, v) in slots) {
        if (k.Length > keySize) keySize = k.Length;
        if (v.Length > valSize) valSize = v.Length;
      }
      if (slots.Count == 0) { keySize = leafKeySize; valSize = leafValueSize; }
      var perRecord = tocEntrySize + keySize + valSize;
      if (perRecord > tocEntrySize)
        tocLen = Math.Max(tocLen, (block.Length - BtnHeaderEnd) / perRecord * tocEntrySize);
    } else {
      tocLen = Math.Max(tocLen, MinimumTableSlots * tocEntrySize);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(block[40..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(block[42..], (ushort)tocLen);

    var keyAreaStart = tocOff + tocLen;
    var valAreaEnd = isRoot ? block.Length - BtreeInfoSize : block.Length;
    var keyCursor = keyAreaStart;
    var valCursor = valAreaEnd;

    for (var i = 0; i < slots.Count; i++) {
      var (k, v) = slots[i];
      var keyRelOff = (ushort)(keyCursor - keyAreaStart);
      k.CopyTo(block[keyCursor..]);
      keyCursor += k.Length;

      valCursor -= v.Length;
      v.CopyTo(block[valCursor..]);
      var valRelOff = (ushort)(valAreaEnd - valCursor);

      var entryOff = tocOff + i * tocEntrySize;
      if (fixedKv) {
        BinaryPrimitives.WriteUInt16LittleEndian(block[entryOff..], keyRelOff);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 2)..], valRelOff);
      } else {
        BinaryPrimitives.WriteUInt16LittleEndian(block[entryOff..], keyRelOff);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 2)..], (ushort)k.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 4)..], valRelOff);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(entryOff + 6)..], (ushort)v.Length);
      }
    }

    if (keyCursor > valCursor)
      throw new InvalidOperationException(
        $"APFS B-tree node overflow during rebuild: {slots.Count} slots exceed one block.");

    BinaryPrimitives.WriteUInt16LittleEndian(block[44..], (ushort)(keyCursor - keyAreaStart));
    BinaryPrimitives.WriteUInt16LittleEndian(block[46..], (ushort)(valCursor - keyCursor));
    BinaryPrimitives.WriteUInt16LittleEndian(block[48..], BTOFF_INVALID);
    BinaryPrimitives.WriteUInt16LittleEndian(block[50..], 0);
    BinaryPrimitives.WriteUInt16LittleEndian(block[52..], BTOFF_INVALID);
    BinaryPrimitives.WriteUInt16LittleEndian(block[54..], 0);

    if (isRoot) {
      var infoOff = block.Length - BtreeInfoSize;
      var longestKey = leafKeySize;
      var longestVal = leafValueSize;
      foreach (var s in slots) {
        if (s.Key.Length > longestKey) longestKey = s.Key.Length;
        if (s.Value.Length > longestVal) longestVal = s.Value.Length;
      }

      // Where the nodes live, whether the records are padded, and — for a tree
      // whose records are all one size — what that size is. All four used to be
      // written as zero, so a fixed tree this rebuilt said nothing about how to
      // read it and came back empty: the modifier could read a tree the writer
      // made and not one it had made itself.
      var storage = (type & OBJ_EPHEMERAL) != 0 ? BTREE_EPHEMERAL
        : (type & OBJ_PHYSICAL) != 0 ? BTREE_PHYSICAL : 0u;
      BinaryPrimitives.WriteUInt32LittleEndian(block[infoOff..],
        storage | (fixedKv ? 0u : BTREE_KV_NONALIGNED));
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 4)..], (uint)block.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 8)..],
        fixedKv ? (uint)longestKey : 0u);
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 12)..],
        fixedKv ? (uint)longestVal : 0u);
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 16)..], (uint)longestKey);
      BinaryPrimitives.WriteUInt32LittleEndian(block[(infoOff + 20)..], (uint)longestVal);
      BinaryPrimitives.WriteUInt64LittleEndian(block[(infoOff + 24)..], totalKeys);
      BinaryPrimitives.WriteUInt64LittleEndian(block[(infoOff + 32)..], nodeCount);
    }

    ApfsFletcher64.Stamp(block);
  }
}

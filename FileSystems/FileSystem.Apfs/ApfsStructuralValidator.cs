#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// Paranoid internal structural validator for APFS images.
/// <para>
/// Because there is no <c>fsck_apfs</c> on Windows or Linux that we can rely on
/// (Apple's tool is macOS-only; <c>apfs-fuse</c> only ships read code; libfsapfs
/// is read-only), this validator is the real acceptance gate for in-place
/// mutations. It walks every B-tree (container OMAP, volume OMAP, FS-tree),
/// re-verifies every block's Fletcher-64, checks key ordering inside every
/// node, cross-references OMAP entries against actual on-disk blocks, and
/// checks the FS-tree integrity invariants:
/// <list type="bullet">
///   <item><description>every <c>DIR_REC</c>'s child object id resolves to an <c>INODE</c> record;</description></item>
///   <item><description>every non-root <c>INODE</c> has at least one <c>DIR_REC</c> naming it;</description></item>
///   <item><description>every <c>FILE_EXTENT</c> belongs to an existing inode and points at a real block.</description></item>
/// </list>
/// </para>
/// <para>
/// xid monotonicity: every block touched by the mutator must carry an
/// <c>o_xid</c> that is less than or equal to the container's
/// <c>nx_next_xid</c>, with the checkpoint header carrying the highest xid.
/// </para>
/// </summary>
public static class ApfsStructuralValidator {
  private const uint BlockSize = DEFAULT_BLOCK_SIZE;
  private const int ObjHeaderSize = 32;
  private const int BtnHeaderEnd = 56;
  private const int BtreeInfoSize = 40;
  private const int TocEntrySize = 8;

  /// <summary>The outcome of validating an APFS image — empty <c>Errors</c> means OK.</summary>
  public sealed class Report {
    /// <summary>
    /// Gets the errors.
    /// </summary>
    public List<string> Errors { get; } = [];
    /// <summary>
    /// Gets the warnings.
    /// </summary>
    public List<string> Warnings { get; } = [];
    /// <summary>
    /// Gets or sets the blocks checksum checked.
    /// </summary>
    public int BlocksChecksumChecked { get; set; }
    /// <summary>
    /// Gets or sets the btree nodes visited.
    /// </summary>
    public int BtreeNodesVisited { get; set; }
    /// <summary>
    /// Gets or sets the fs records scanned.
    /// </summary>
    public int FsRecordsScanned { get; set; }
    /// <summary>
    /// Gets or sets the max xid seen.
    /// </summary>
    public ulong MaxXidSeen { get; set; }
    /// <summary>
    /// Gets or sets the container next xid.
    /// </summary>
    public ulong ContainerNextXid { get; set; }
    /// <summary>
    /// Gets a value indicating whether is valid.
    /// </summary>
    public bool IsValid => this.Errors.Count == 0;
    /// <summary>
    /// Performs the to string operation.
    /// </summary>
    public override string ToString() {
      var sb = new StringBuilder();
      sb.Append($"APFS validator: blocks={this.BlocksChecksumChecked} nodes={this.BtreeNodesVisited} ");
      sb.Append($"records={this.FsRecordsScanned} maxXid={this.MaxXidSeen} nextXid={this.ContainerNextXid}");
      foreach (var w in this.Warnings) sb.AppendLine().Append("  WARN: ").Append(w);
      foreach (var e in this.Errors) sb.AppendLine().Append("  ERR: ").Append(e);
      return sb.ToString();
    }
  }

  /// <summary>Walks an APFS image and returns a structural validation report.</summary>
  public static Report Validate(byte[] image) {
    ArgumentNullException.ThrowIfNull(image);
    var r = new Report();
    if (image.Length < (long)12 * BlockSize) {
      r.Errors.Add($"image too small ({image.Length} bytes)");
      return r;
    }

    var nx = image.AsSpan(0, (int)BlockSize);
    var nxMagic = BinaryPrimitives.ReadUInt32LittleEndian(nx[32..]);
    if (nxMagic != 0x4253584EU) {
      r.Errors.Add($"NXSB magic invalid: 0x{nxMagic:X8}");
      return r;
    }

    if (!ApfsFletcher64.Verify(nx)) r.Errors.Add("NXSB Fletcher-64 invalid");
    r.BlocksChecksumChecked++;

    var nxOid = BinaryPrimitives.ReadUInt64LittleEndian(nx[8..]);
    var nxXid = BinaryPrimitives.ReadUInt64LittleEndian(nx[16..]);
    var nxNextXid = BinaryPrimitives.ReadUInt64LittleEndian(nx[96..]);
    r.ContainerNextXid = nxNextXid;
    r.MaxXidSeen = Math.Max(r.MaxXidSeen, nxXid);

    // The writer also writes a checkpoint NXSB copy at block 2.
    var nxCopy = image.AsSpan(2 * (int)BlockSize, (int)BlockSize);
    if (!ApfsFletcher64.Verify(nxCopy)) r.Errors.Add("NXSB copy Fletcher-64 invalid");
    r.BlocksChecksumChecked++;

    // Checkpoint map at block 1.
    var chk = image.AsSpan(1 * (int)BlockSize, (int)BlockSize);
    if (!ApfsFletcher64.Verify(chk)) r.Errors.Add("checkpoint map Fletcher-64 invalid");
    var chkXid = BinaryPrimitives.ReadUInt64LittleEndian(chk[16..]);
    if (chkXid >= nxNextXid)
      r.Errors.Add($"checkpoint xid {chkXid} must be < nx_next_xid {nxNextXid}");
    r.MaxXidSeen = Math.Max(r.MaxXidSeen, chkXid);
    r.BlocksChecksumChecked++;

    // Container OMAP physical address hint (writer-specific).
    var ctrOmapPhys = BinaryPrimitives.ReadUInt64LittleEndian(nx[3072..]);
    if (ctrOmapPhys == 0) {
      r.Warnings.Add("no container OMAP hint at NXSB+3072 — image is likely NXSB-only");
      return r;
    }

    // Walk container OMAP and resolve APSB.
    var ctrOmapEntries = WalkOmap(image, ctrOmapPhys, "container OMAP", r);
    var apsbVirtOid = BinaryPrimitives.ReadUInt64LittleEndian(nx[184..]);
    if (apsbVirtOid == 0) {
      r.Errors.Add("nx_fs_oid[0] (APSB OID) is zero");
      return r;
    }
    if (!ctrOmapEntries.TryGetValue(apsbVirtOid, out var apsbPhys)) {
      r.Errors.Add($"container OMAP missing APSB virtual OID 0x{apsbVirtOid:X}");
      return r;
    }

    var apsb = image.AsSpan((int)(apsbPhys * BlockSize), (int)BlockSize);
    if (!ApfsFletcher64.Verify(apsb)) r.Errors.Add("APSB Fletcher-64 invalid");
    var apsbMagic = BinaryPrimitives.ReadUInt32LittleEndian(apsb[32..]);
    if (apsbMagic != 0x42535041U) r.Errors.Add($"APSB magic invalid: 0x{apsbMagic:X8}");
    r.BlocksChecksumChecked++;
    var apsbXid = BinaryPrimitives.ReadUInt64LittleEndian(apsb[16..]);
    r.MaxXidSeen = Math.Max(r.MaxXidSeen, apsbXid);

    // Walk volume OMAP and resolve FS-tree root.
    var volOmapPhys = BinaryPrimitives.ReadUInt64LittleEndian(apsb[APSB_OMAP_OID..]);
    var fsTreeVirtOid = BinaryPrimitives.ReadUInt64LittleEndian(apsb[APSB_ROOT_TREE_OID..]);
    if (volOmapPhys == 0 || fsTreeVirtOid == 0) {
      r.Errors.Add("APSB omap_oid or root_tree_oid is zero");
      return r;
    }
    var volOmapEntries = WalkOmap(image, volOmapPhys, "volume OMAP", r);
    if (!volOmapEntries.TryGetValue(fsTreeVirtOid, out var fsTreePhys)) {
      r.Errors.Add($"volume OMAP missing FS-tree virtual OID 0x{fsTreeVirtOid:X}");
      return r;
    }

    // Walk FS-tree end to end.
    var fsLeafRecords = WalkBtree(image, fsTreePhys, "FS-tree", r, omapPhys: volOmapPhys);
    r.FsRecordsScanned = fsLeafRecords.Count;
    ValidateFsTreeSemantics(fsLeafRecords, image, r);

    if (r.MaxXidSeen >= nxNextXid)
      r.Errors.Add($"some object xid {r.MaxXidSeen} >= nx_next_xid {nxNextXid} (must be strictly less)");

    return r;
  }

  /// <summary>
  /// Walks every node of an OMAP B-tree (root-internal-leaf), validates checksums,
  /// key ordering inside each node, and returns the consolidated (virtOid → paddr)
  /// mapping. A duplicate OID with a higher xid wins (latest-xid rule).
  /// </summary>
  private static Dictionary<ulong, ulong> WalkOmap(byte[] image, ulong omapPhys, string label, Report r) {
    var entries = new Dictionary<ulong, ulong>();
    var omap = image.AsSpan((int)(omapPhys * BlockSize), (int)BlockSize);
    if (!ApfsFletcher64.Verify(omap))
      r.Errors.Add($"{label} OMAP phys Fletcher-64 invalid (block {omapPhys})");
    r.BlocksChecksumChecked++;

    var treePhys = BinaryPrimitives.ReadUInt64LittleEndian(omap[48..]);
    if (treePhys == 0) {
      r.Warnings.Add($"{label}: empty (om_tree_oid=0)");
      return entries;
    }

    var bestXid = new Dictionary<ulong, ulong>();
    foreach (var (key, value) in WalkBtree(image, treePhys, label + " B-tree", r)) {
      if (key.Length < 16 || value.Length < 16) continue;
      var oid = BinaryPrimitives.ReadUInt64LittleEndian(key);
      var xid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8));
      var paddr = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8));
      if (paddr == 0 || (long)paddr * BlockSize + BlockSize > image.Length) {
        r.Errors.Add($"{label}: OMAP entry oid=0x{oid:X} points at invalid block {paddr}");
        continue;
      }
      if (!bestXid.TryGetValue(oid, out var prev) || xid > prev) {
        bestXid[oid] = xid;
        entries[oid] = paddr;
      }
    }
    return entries;
  }

  /// <summary>
  /// Walks a B-tree starting from a physical root, collecting all leaf records.
  /// Validates checksum on every visited node, key ordering inside the node, and
  /// that the level field is consistent (root level = depth, leaves = 0). Uses
  /// a visited set to guard against malformed cyclic child pointers.
  /// </summary>
  /// <param name="omapPhys">
  /// The object map that turns a child's identifier into its block, for a tree
  /// whose nodes are virtual. Zero for a physical tree.
  /// </param>
  private static List<(byte[] Key, byte[] Value)> WalkBtree(byte[] image, ulong rootPhys, string label,
      Report r, ulong omapPhys = 0) {
    var records = new List<(byte[], byte[])>();
    var visited = new HashSet<ulong>();
    Walk(rootPhys, isRoot: true, expectedLevel: -1);
    return records;

    void Walk(ulong blockNum, bool isRoot, int expectedLevel) {
      if (!visited.Add(blockNum)) {
        r.Errors.Add($"{label}: cyclic child reference to block {blockNum}");
        return;
      }
      if ((long)blockNum * BlockSize + BlockSize > image.Length) {
        r.Errors.Add($"{label}: child block {blockNum} past image end");
        return;
      }
      var node = image.AsSpan((int)(blockNum * BlockSize), (int)BlockSize);
      if (!ApfsFletcher64.Verify(node))
        r.Errors.Add($"{label}: node {blockNum} Fletcher-64 invalid");
      r.BlocksChecksumChecked++;
      r.BtreeNodesVisited++;

      var flags = BinaryPrimitives.ReadUInt16LittleEndian(node[32..]);
      var level = BinaryPrimitives.ReadUInt16LittleEndian(node[34..]);
      var nkeys = BinaryPrimitives.ReadUInt32LittleEndian(node[36..]);
      var nodeXid = BinaryPrimitives.ReadUInt64LittleEndian(node[16..]);
      r.MaxXidSeen = Math.Max(r.MaxXidSeen, nodeXid);

      if (isRoot && (flags & BTNODE_ROOT) == 0)
        r.Errors.Add($"{label}: root node {blockNum} missing BTNODE_ROOT");

      if (expectedLevel >= 0 && level != expectedLevel)
        r.Errors.Add($"{label}: node {blockNum} level={level} expected={expectedLevel}");

      var slots = ReadAllSlots(node, isRoot);
      if (slots.Count != (int)nkeys)
        r.Warnings.Add($"{label}: node {blockNum} nkeys={nkeys} but decoded {slots.Count} slots");

      // Key ordering inside the node.
      for (var i = 1; i < slots.Count; i++) {
        if (CompareKeys(slots[i - 1].Key, slots[i].Key) > 0) {
          r.Errors.Add($"{label}: node {blockNum} keys out of order at slot {i}");
          break;
        }
      }

      if (level == 0) {
        // Leaf — collect.
        foreach (var s in slots) records.Add((s.Key, s.Value));
        return;
      }

      // Internal — descend.
      foreach (var s in slots) {
        if (s.Value.Length < 8) {
          r.Errors.Add($"{label}: internal node {blockNum} slot value too short");
          continue;
        }
        // A virtual tree names its children by identifier; the map turns one
        // into a block. Following the identifier as though it were a block found
        // nothing, so a split tree looked like a tree with one node in it.
        var child = BinaryPrimitives.ReadUInt64LittleEndian(s.Value);
        var childAddr = omapPhys == 0
          ? child
          : ApfsBtreeOps.ResolveOidViaOmapTree(image,
              BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)(omapPhys * BlockSize) + 48)),
              child);
        if (childAddr == 0) {
          r.Errors.Add($"{label}: child {child} is named by nothing in the object map");
          continue;
        }
        Walk(childAddr, isRoot: false, expectedLevel: level - 1);
      }
    }
  }

  /// <summary>
  /// Decodes every slot (key/value pair) from a B-tree node's TOC. Returns the raw
  /// byte arrays. Skips invalid offsets silently so the validator never crashes,
  /// but the caller can compare the decoded slot count against nkeys to detect
  /// malformed TOCs.
  /// </summary>
  private static List<(byte[] Key, byte[] Value)> ReadAllSlots(ReadOnlySpan<byte> node, bool isRoot) {
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

    // A tree whose records are all one size states that size in its root's
    // footer, because the slots carry only offsets. Leaving both lengths at zero
    // here made every such node decode to nothing — "nkeys=1 but decoded 0
    // slots" — which is the third place in this codebase that assumed the shape
    // our own writer used to produce.
    var fixedKeyLen = 0;
    var fixedValLen = 0;
    if (isFixed) {
      var info = node.Length - BtreeInfoSize;
      fixedKeyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 8)..]);
      fixedValLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(node[(info + 12)..]);
      if ((flags & BTNODE_LEAF) == 0) fixedValLen = 8;   // an index node names children
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

  /// <summary>
  /// Validates FS-tree semantic invariants once all leaf records are collected.
  /// <list type="bullet">
  ///   <item><description>every <c>DIR_REC</c>'s child OID must resolve to an <c>INODE</c>;</description></item>
  ///   <item><description>every non-root <c>INODE</c> must be named by at least one <c>DIR_REC</c>;</description></item>
  ///   <item><description>every <c>FILE_EXTENT</c> must belong to an existing inode and point at a real on-disk block.</description></item>
  /// </list>
  /// </summary>
  private static void ValidateFsTreeSemantics(List<(byte[] Key, byte[] Value)> records, byte[] image, Report r) {
    var inodes = new HashSet<ulong>();
    var dirRecsToChild = new Dictionary<ulong, string>();
    var inodesNamed = new HashSet<ulong>();
    var inodesWithExtent = new Dictionary<ulong, (long Length, ulong PhysBlock)>();

    foreach (var (key, value) in records) {
      if (key.Length < 8) continue;
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(key);
      var keyType = (int)(oidAndType >> 60);
      var oid = oidAndType & 0x0FFFFFFFFFFFFFFFUL;
      switch (keyType) {
        case APFS_TYPE_INODE:
          inodes.Add(oid);
          break;
        case APFS_TYPE_DIR_REC:
          if (key.Length >= 12 && value.Length >= 8) {
            var childIno = BinaryPrimitives.ReadUInt64LittleEndian(value);
            dirRecsToChild[childIno] = SafeReadName(key);
            inodesNamed.Add(childIno);
          }
          break;
        case APFS_TYPE_FILE_EXTENT:
          if (value.Length >= 16) {
            var lenAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(value);
            var len = (long)(lenAndFlags & 0x00FFFFFFFFFFFFFFUL);
            var paddr = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(8));
            inodesWithExtent[oid] = (len, paddr);
          }
          break;
      }
    }

    // Every DIR_REC's child must be a real inode.
    foreach (var (childIno, name) in dirRecsToChild)
      if (!inodes.Contains(childIno))
        r.Errors.Add($"FS-tree: DIR_REC name={name} → inode {childIno} not found");

    // Every non-root inode must be named by a DIR_REC. The reserved inodes below the
    // first user one are the exception: the root, and the private directory a mount
    // reads by number, which nothing in the volume links to.
    foreach (var ino in inodes) {
      if (ino < APFS_MIN_USER_INO_NUM) continue;
      if (!inodesNamed.Contains(ino))
        r.Errors.Add($"FS-tree: orphaned inode {ino} not named by any DIR_REC");
    }

    // Every FILE_EXTENT's inode must exist and the block must be on disk.
    foreach (var (ino, ext) in inodesWithExtent) {
      if (!inodes.Contains(ino))
        r.Errors.Add($"FS-tree: FILE_EXTENT references unknown inode {ino}");
      if (ext.PhysBlock > 0 && (long)ext.PhysBlock * BlockSize + ext.Length > image.Length)
        r.Errors.Add($"FS-tree: FILE_EXTENT inode={ino} block={ext.PhysBlock} past image end");
    }
  }

  private static string SafeReadName(byte[] key)
    => ApfsDrecKey.TryReadName(key, out var name) ? name : string.Empty;

  /// <summary>
  /// Compares two B-tree keys in APFS canonical order: (oid asc, type asc, then
  /// raw key tail for DIR_REC names or FILE_EXTENT logical offsets).
  /// </summary>
  private static int CompareKeys(byte[] a, byte[] b) {
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
    // comparing the tail byte for byte compares the lengths first. Entries are
    // ordered by name.
    if (ta == APFS_TYPE_DIR_REC && a.Length >= 10 && b.Length >= 10)
      return a.AsSpan(10).SequenceCompareTo(b.AsSpan(10));

    return a.AsSpan(8).SequenceCompareTo(b.AsSpan(8));
  }
}

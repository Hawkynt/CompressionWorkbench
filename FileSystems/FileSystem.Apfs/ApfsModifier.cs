#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// In-place mutation for APFS images produced by <see cref="ApfsWriter"/>.
/// <para>
/// Targets the writer's documented layout: NXSB at block 0, checkpoint
/// descriptor at block 1, NXSB copy at block 2, container OMAP phys at block 3,
/// its B-tree root at block 4, APSB at block 5, volume OMAP phys at block 6,
/// volume OMAP B-tree root at block 7, FS-tree root at block 8, extent-ref /
/// snap-meta at 9-10, and dynamic file data / extra B-tree leaf nodes from
/// block 11 onward.
/// </para>
/// <para>
/// <b>Full-scope mutation</b>: Add / Remove are no longer restricted to
/// one-level / one-leaf trees. The modifier:
/// <list type="number">
///   <item><description>walks the existing FS-tree end-to-end, collects all leaf records, applies the
///     mutation, then rebuilds the tree top-down via <see cref="ApfsBtreeOps"/>: it
///     partitions records into fresh leaves and, when more than one leaf is needed,
///     emits one or more levels of internal index nodes growing tree height as required;</description></item>
///   <item><description>does the same for the container OMAP and the volume OMAP — if either grows
///     past one node, the new tree height is honoured;</description></item>
///   <item><description>walks dirent (<c>j_drec_key_t</c>) records to find the target parent directory's
///     inode for multi-component paths (<c>"a/b/c.txt"</c>), inserting missing
///     intermediate directory inodes on the fly;</description></item>
///   <item><description>allocates new physical blocks for split leaves, internal nodes, and file data
///     contiguously from the image tail (the writer never emits a spaceman block, so the
///     modifier mirrors that by tail-allocating);</description></item>
///   <item><description>advances <c>nx_next_xid</c> / every touched node's <c>o_xid</c>, recomputes
///     Fletcher-64 on every block touched, and zeroes removed file data.</description></item>
/// </list>
/// </para>
/// <para>
/// <b>Genuinely-out-of-scope</b> features keep a specific
/// <see cref="NotSupportedException"/> message: snapshots, encryption / FileVault,
/// fusion / tiered storage, sparse clones, directory-tree removal.
/// </para>
/// </summary>
internal static class ApfsModifier {

  private const uint BlockSize = DEFAULT_BLOCK_SIZE;

  // ── Entry points ────────────────────────────────────────────────────────

  /// <summary>
  /// Adds a single file (possibly under a nested path) to an existing APFS
  /// image in place. Replaces any existing file with the same path.
  /// </summary>
  public static void Add(Stream archive, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new InvalidOperationException("APFS modifier requires a seekable read/write stream.");

    var ctx = ReadContext(archive);
    var path = NormalizePath(name);
    if (path.Length == 0) return;

    // Locate / synthesise parent dir inode chain (everything except the last component).
    var leafName = path[^1];
    var parentIno = ResolveOrCreateParentChain(ctx, path.AsSpan(0, path.Length - 1));

    // If a node with this name already exists under the parent, drop it first.
    if (TryFindChildOid(ctx, parentIno, leafName, out var existingIno, out var existingIsDir)) {
      if (existingIsDir)
        throw new NotSupportedException(
          "APFS: replacing a directory with a file is not supported by the in-place modifier.");
      DropFileRecordsAndZeroData(ctx, existingIno, leafName, parentIno);
      // Removing reduces the parent's child count; the Add below bumps it back.
      BumpInodeChildCount(ctx.FsRecords, parentIno, -1);
    }

    // Allocate physical blocks at image tail for the new file data.
    var dataBlockCount = data.Length == 0 ? 0 : (int)((data.Length + BlockSize - 1) / BlockSize);
    var firstBlock = dataBlockCount > 0
      ? ctx.Allocator.AllocateData(ref ctx.Image, dataBlockCount, data)
      : 0UL;

    var fileIno = ctx.AllocInode();
    var fileRecs = BuildFileRecords(parentIno, fileIno, leafName, data, firstBlock);
    foreach (var rec in fileRecs)
      InsertOrReplace(ctx.FsRecords, rec);

    BumpInodeChildCount(ctx.FsRecords, parentIno, +1);

    Persist(ctx, archive);
  }

  /// <summary>
  /// Removes a single file by name (full path, '/' or '\\' separators).
  /// Sibling entries are preserved. The removed file's data blocks are zeroed.
  /// </summary>
  public static void Remove(Stream archive, string name) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(name);
    if (!archive.CanSeek || !archive.CanRead || !archive.CanWrite)
      throw new InvalidOperationException("APFS modifier requires a seekable read/write stream.");

    var ctx = ReadContext(archive);
    var path = NormalizePath(name);
    if (path.Length == 0) return;

    var parentIno = ResolveParentChain(ctx, path.AsSpan(0, path.Length - 1));
    if (parentIno == 0) return; // unknown parent path — no-op (mirror other modifiers).

    if (!TryFindChildOid(ctx, parentIno, path[^1], out var fileIno, out var isDir))
      return;
    if (isDir)
      throw new NotSupportedException(
        "APFS: directory removal (recursive subtree drop) is not supported by the in-place modifier.");

    DropFileRecordsAndZeroData(ctx, fileIno, path[^1], parentIno);
    BumpInodeChildCount(ctx.FsRecords, parentIno, -1);

    Persist(ctx, archive);
  }

  // ── Persist: rebuild trees, stamp checkpoint ────────────────────────────

  /// <summary>
  /// Writes the mutated state back to the image: rebuilds the FS-tree (top-down,
  /// growing height when needed), rebuilds the volume + container OMAPs to point
  /// at the new FS-tree root, advances the transaction id, and recomputes
  /// Fletcher-64 on every touched block (NXSB, NXSB copy, APSB, checkpoint map,
  /// every B-tree node, every OMAP block).
  /// </summary>
  private static void Persist(Context ctx, Stream archive) {
    var newXid = ctx.CurrentXid + 1;

    // Rebuild FS-tree on the original block 8 (writer-fixed root location).
    var fsTreeRootBlock = (long)ctx.FsTreeRootBlock;
    ApfsBtreeOps.RebuildBtreeOnFixedRoot(
      ref ctx.Image, ctx.Allocator,
      rootBlock: fsTreeRootBlock,
      rootOid: ctx.FsTreeOid,
      type: ctx.FsTreeType,
      xid: newXid,
      records: ctx.FsRecords,
      keyComparer: ApfsBtreeOps.CompareFsKeys);

    // Volume OMAP: only one record (FS-tree virtual OID → fsTreeRootBlock).
    var volOmapTreeBlock = (long)ctx.VolOmapTreeBlock;
    var volOmapRecs = new List<ApfsBtreeOps.Record> {
      ApfsBtreeOps.BuildOmapRecord(ctx.FsTreeVirtOid, newXid, (ulong)fsTreeRootBlock),
    };
    ApfsBtreeOps.RebuildBtreeOnFixedRoot(
      ref ctx.Image, ctx.Allocator,
      rootBlock: volOmapTreeBlock,
      rootOid: (ulong)volOmapTreeBlock,
      type: OBJECT_TYPE_BTREE | OBJ_PHYSICAL,
      xid: newXid,
      records: volOmapRecs,
      keyComparer: ApfsBtreeOps.CompareOmapKeys);

    // Restamp volume OMAP phys.
    var volOmap = ctx.Image.AsSpan((int)(ctx.VolOmapBlock * BlockSize), (int)BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(volOmap[16..], newXid);
    BinaryPrimitives.WriteUInt64LittleEndian(volOmap[48..], (ulong)volOmapTreeBlock);
    ApfsFletcher64.Stamp(volOmap);

    // Container OMAP: one record (APSB virtual OID → apsbBlock).
    var ctrOmapTreeBlock = (long)ctx.CtrOmapTreeBlock;
    var ctrOmapRecs = new List<ApfsBtreeOps.Record> {
      ApfsBtreeOps.BuildOmapRecord(ctx.ApsbVirtOid, newXid, ctx.ApsbBlock),
    };
    ApfsBtreeOps.RebuildBtreeOnFixedRoot(
      ref ctx.Image, ctx.Allocator,
      rootBlock: ctrOmapTreeBlock,
      rootOid: (ulong)ctrOmapTreeBlock,
      type: OBJECT_TYPE_BTREE | OBJ_PHYSICAL,
      xid: newXid,
      records: ctrOmapRecs,
      keyComparer: ApfsBtreeOps.CompareOmapKeys);

    var ctrOmap = ctx.Image.AsSpan((int)(ctx.CtrOmapBlock * BlockSize), (int)BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(ctrOmap[16..], newXid);
    BinaryPrimitives.WriteUInt64LittleEndian(ctrOmap[48..], (ulong)ctrOmapTreeBlock);
    ApfsFletcher64.Stamp(ctrOmap);

    // APSB.
    var apsb = ctx.Image.AsSpan((int)(ctx.ApsbBlock * BlockSize), (int)BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(apsb[16..], newXid);
    BinaryPrimitives.WriteUInt64LittleEndian(apsb[APSB_LAST_MOD_TIME..],
      (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    BinaryPrimitives.WriteUInt64LittleEndian(apsb[APSB_NEXT_OBJ_ID..], ctx.NextOid);
    var fileCount = (ulong)CountInodes(ctx.FsRecords, isDir: false);
    var dirCount = (ulong)CountInodes(ctx.FsRecords, isDir: true);
    BinaryPrimitives.WriteUInt64LittleEndian(apsb[APSB_NUM_FILES..], fileCount);
    BinaryPrimitives.WriteUInt64LittleEndian(apsb[APSB_NUM_DIRECTORIES..], dirCount);
    ApfsFletcher64.Stamp(apsb);

    // Checkpoint map.
    var chk = ctx.Image.AsSpan((int)(ctx.ChkMapBlock * BlockSize), (int)BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(chk[8..], newXid);
    BinaryPrimitives.WriteUInt64LittleEndian(chk[16..], newXid);
    ApfsFletcher64.Stamp(chk);

    // NXSB primary.
    var nx = ctx.Image.AsSpan(0, (int)BlockSize);
    BinaryPrimitives.WriteUInt64LittleEndian(nx[16..], newXid);
    BinaryPrimitives.WriteUInt64LittleEndian(nx[40..], (ulong)(ctx.Image.Length / BlockSize));
    BinaryPrimitives.WriteUInt64LittleEndian(nx[96..], newXid + 1);
    ApfsFletcher64.Stamp(nx);

    // NXSB copy — mirror primary.
    ctx.Image.AsSpan(0, (int)BlockSize).CopyTo(ctx.Image.AsSpan((int)(ctx.NxCopyBlock * BlockSize), (int)BlockSize));
    var nxCopy = ctx.Image.AsSpan((int)(ctx.NxCopyBlock * BlockSize), (int)BlockSize);
    ApfsFletcher64.Stamp(nxCopy);

    ctx.CurrentXid = newXid;

    archive.Position = 0;
    archive.Write(ctx.Image);
    archive.SetLength(ctx.Image.Length);
  }

  // ── Path / parent resolution ────────────────────────────────────────────

  /// <summary>Splits a path into its components (forward slashes, no empty parts).</summary>
  private static string[] NormalizePath(string path) =>
    path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

  /// <summary>
  /// Walks <paramref name="dirComponents"/> from the root. Returns the parent
  /// inode for the final leaf name (or <c>APFS_ROOT_DIR_INO_NUM</c> when the
  /// array is empty). Returns 0 when an intermediate directory does not exist.
  /// </summary>
  private static ulong ResolveParentChain(Context ctx, ReadOnlySpan<string> dirComponents) {
    var parent = APFS_ROOT_DIR_INO_NUM;
    foreach (var comp in dirComponents) {
      if (!TryFindChildOid(ctx, parent, comp, out var ino, out var isDir) || !isDir)
        return 0;
      parent = ino;
    }
    return parent;
  }

  /// <summary>
  /// Same as <see cref="ResolveParentChain"/> but inserts missing intermediate
  /// directory inodes (with DIR_REC + INODE records) so the leaf file can be added.
  /// </summary>
  private static ulong ResolveOrCreateParentChain(Context ctx, ReadOnlySpan<string> dirComponents) {
    var parent = APFS_ROOT_DIR_INO_NUM;
    foreach (var comp in dirComponents) {
      if (TryFindChildOid(ctx, parent, comp, out var ino, out var isDir)) {
        if (!isDir)
          throw new NotSupportedException(
            $"APFS: path component '{comp}' resolves to a file, not a directory.");
        parent = ino;
        continue;
      }
      var newDirIno = ctx.AllocInode();
      InsertOrReplace(ctx.FsRecords, new ApfsBtreeOps.Record(
        BuildDrecKey(parent, comp),
        BuildDrecValue(newDirIno, isDir: true)));
      InsertOrReplace(ctx.FsRecords, new ApfsBtreeOps.Record(
        BuildInodeKey(newDirIno),
        BuildInodeValue(newDirIno, parentId: parent, size: 0, isDir: true, nchildren: 0)));
      BumpInodeChildCount(ctx.FsRecords, parent, +1);
      parent = newDirIno;
    }
    return parent;
  }

  /// <summary>
  /// Looks up <paramref name="name"/> under <paramref name="parentOid"/> by
  /// scanning the FS-tree records. Returns the child inode and its DT_DIR flag.
  /// </summary>
  private static bool TryFindChildOid(Context ctx, ulong parentOid, string name, out ulong childOid, out bool isDir) {
    foreach (var rec in ctx.FsRecords) {
      if (rec.Key.Length < 12) continue;
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key);
      var keyType = (int)(oidAndType >> 60);
      var oid = oidAndType & 0x0FFFFFFFFFFFFFFFUL;
      if (keyType != APFS_TYPE_DIR_REC || oid != parentOid) continue;
      if (!ApfsDrecKey.TryReadName(rec.Key, out var actual)) continue;
      if (!string.Equals(actual, name, StringComparison.Ordinal)) continue;
      if (rec.Value.Length < 18) continue;
      childOid = BinaryPrimitives.ReadUInt64LittleEndian(rec.Value);
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(rec.Value.AsSpan(16));
      isDir = (flags & APFS_DIR_REC_FLAGS_MASK) == DT_DIR;
      return true;
    }
    childOid = 0;
    isDir = false;
    return false;
  }

  // ── Record management ───────────────────────────────────────────────────

  /// <summary>
  /// Inserts a record into the FS-tree record list, replacing any existing entry
  /// with the same key.
  /// </summary>
  private static void InsertOrReplace(List<ApfsBtreeOps.Record> records, ApfsBtreeOps.Record rec) {
    for (var i = 0; i < records.Count; i++) {
      if (records[i].Key.AsSpan().SequenceEqual(rec.Key)) {
        records[i] = rec;
        return;
      }
    }
    records.Add(rec);
  }

  /// <summary>
  /// Drops every FS-tree record keyed on <paramref name="fileIno"/> (INODE, FILE_EXTENT)
  /// plus the parent's DIR_REC naming the file. Zeroes the file's data blocks
  /// before dropping the FILE_EXTENT pointer.
  /// </summary>
  private static void DropFileRecordsAndZeroData(Context ctx, ulong fileIno, string fileName, ulong parentIno) {
    long extentBytes = 0;
    ulong extentFirstBlock = 0;
    foreach (var rec in ctx.FsRecords) {
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key);
      var keyType = (int)(oidAndType >> 60);
      var oid = oidAndType & 0x0FFFFFFFFFFFFFFFUL;
      if (keyType != APFS_TYPE_FILE_EXTENT || oid != fileIno) continue;
      if (rec.Value.Length < 16) continue;
      var lenAndFlags = BinaryPrimitives.ReadUInt64LittleEndian(rec.Value);
      extentBytes = (long)(lenAndFlags & 0x00FFFFFFFFFFFFFFUL);
      extentFirstBlock = BinaryPrimitives.ReadUInt64LittleEndian(rec.Value.AsSpan(8));
      break;
    }
    if (extentFirstBlock > 0 && extentBytes > 0) {
      var dataOff = (long)extentFirstBlock * BlockSize;
      var dataBlocks = (extentBytes + BlockSize - 1) / BlockSize;
      var totalBytes = Math.Min(dataBlocks * BlockSize, ctx.Image.Length - dataOff);
      if (totalBytes > 0)
        Array.Clear(ctx.Image, (int)dataOff, (int)totalBytes);
    }

    var pruned = new List<ApfsBtreeOps.Record>(ctx.FsRecords.Count);
    foreach (var rec in ctx.FsRecords) {
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key);
      var keyType = (int)(oidAndType >> 60);
      var oid = oidAndType & 0x0FFFFFFFFFFFFFFFUL;

      if (keyType == APFS_TYPE_DIR_REC && oid == parentIno) {
        if (ApfsDrecKey.TryReadName(rec.Key, out var actualName)
            && string.Equals(actualName, fileName, StringComparison.Ordinal))
          continue;
      }
      if ((keyType == APFS_TYPE_INODE || keyType == APFS_TYPE_FILE_EXTENT) && oid == fileIno)
        continue;
      pruned.Add(rec);
    }
    ctx.FsRecords = pruned;
  }

  /// <summary>Adjusts <c>nchildren</c> on the directory inode <paramref name="oid"/> by <paramref name="delta"/>.</summary>
  private static void BumpInodeChildCount(List<ApfsBtreeOps.Record> records, ulong oid, int delta) {
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key);
      if ((int)(oidAndType >> 60) != APFS_TYPE_INODE) continue;
      var key = oidAndType & 0x0FFFFFFFFFFFFFFFUL;
      if (key != oid) continue;
      if (rec.Value.Length < 60) continue;
      var nch = BinaryPrimitives.ReadUInt32LittleEndian(rec.Value.AsSpan(56));
      var nv = (uint)Math.Max(0, (int)nch + delta);
      var newValue = (byte[])rec.Value.Clone();
      BinaryPrimitives.WriteUInt32LittleEndian(newValue.AsSpan(56), nv);
      records[i] = new ApfsBtreeOps.Record(rec.Key, newValue);
      return;
    }
  }

  /// <summary>Counts INODE records whose mode flags match <paramref name="isDir"/>.</summary>
  private static int CountInodes(List<ApfsBtreeOps.Record> records, bool isDir) {
    var n = 0;
    foreach (var rec in records) {
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key);
      if ((int)(oidAndType >> 60) != APFS_TYPE_INODE) continue;
      if (rec.Value.Length < 82) continue;
      var mode = BinaryPrimitives.ReadUInt16LittleEndian(rec.Value.AsSpan(80));
      if (((mode & 0xF000) == S_IFDIR) == isDir) n++;
    }
    return n;
  }

  // ── Image read / context ────────────────────────────────────────────────

  /// <summary>The mutator's working context — image bytes + parsed pointers + record list.</summary>
  internal sealed class Context {
    public byte[] Image = [];
    public List<ApfsBtreeOps.Record> FsRecords = [];
    public ulong CurrentXid;
    public ulong NextOid;
    public ulong FsTreeOid;
    public ulong FsTreeVirtOid;
    public uint FsTreeType;
    public ulong FsTreeRootBlock;
    public ulong ApsbBlock;
    public ulong ApsbVirtOid;
    public ulong CtrOmapBlock;
    public ulong CtrOmapTreeBlock;
    public ulong VolOmapBlock;
    public ulong VolOmapTreeBlock;
    public ulong NxCopyBlock = 2;
    public ulong ChkMapBlock = 1;
    public ApfsBlockAllocator Allocator = null!;

    public ulong AllocInode() {
      var ino = this.NextOid;
      this.NextOid++;
      return ino;
    }
  }

  /// <summary>
  /// Reads the image into memory and parses the writer's fixed-layout pointers.
  /// All B-tree records (FS-tree) are decoded once at this stage so the mutator
  /// can manipulate them as a flat list before rebuilding the tree.
  /// </summary>
  private static Context ReadContext(Stream archive) {
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    var image = ms.ToArray();
    if (image.Length < 12 * BlockSize)
      throw new InvalidDataException("APFS: image too small for in-place modification.");

    var nx = image.AsSpan(0, (int)BlockSize);
    var nxMagic = BinaryPrimitives.ReadUInt32LittleEndian(nx[32..]);
    if (nxMagic != 0x4253584EU)
      throw new InvalidDataException("APFS: invalid NXSB magic — cannot modify.");

    var ctrOmapBlock = BinaryPrimitives.ReadUInt64LittleEndian(nx[3072..]);
    if (ctrOmapBlock == 0)
      throw new InvalidDataException("APFS: container OMAP hint missing — image was not produced by ApfsWriter.");

    var apsbVirtOid = BinaryPrimitives.ReadUInt64LittleEndian(nx[184..]);
    var ctrOmap = image.AsSpan((int)(ctrOmapBlock * BlockSize), (int)BlockSize);
    var ctrOmapTreeBlock = BinaryPrimitives.ReadUInt64LittleEndian(ctrOmap[48..]);
    var apsbPhys = ApfsBtreeOps.ResolveOidViaOmapTree(image, ctrOmapTreeBlock, apsbVirtOid);
    if (apsbPhys == 0)
      throw new InvalidDataException("APFS: cannot resolve APSB via container OMAP.");

    var apsb = image.AsSpan((int)(apsbPhys * BlockSize), (int)BlockSize);
    var volOmapBlock = BinaryPrimitives.ReadUInt64LittleEndian(apsb[APSB_OMAP_OID..]);
    var fsTreeVirtOid = BinaryPrimitives.ReadUInt64LittleEndian(apsb[APSB_ROOT_TREE_OID..]);
    var volOmap = image.AsSpan((int)(volOmapBlock * BlockSize), (int)BlockSize);
    var volOmapTreeBlock = BinaryPrimitives.ReadUInt64LittleEndian(volOmap[48..]);
    var fsTreePhys = ApfsBtreeOps.ResolveOidViaOmapTree(image, volOmapTreeBlock, fsTreeVirtOid);
    if (fsTreePhys == 0)
      throw new InvalidDataException("APFS: cannot resolve FS-tree via volume OMAP.");

    var fsTreeRoot = image.AsSpan((int)(fsTreePhys * BlockSize), (int)BlockSize);
    var fsTreeType = BinaryPrimitives.ReadUInt32LittleEndian(fsTreeRoot[24..]);
    var fsTreeOid = BinaryPrimitives.ReadUInt64LittleEndian(fsTreeRoot[8..]);

    var fsRecords = ApfsBtreeOps.CollectAllLeafRecords(image, fsTreePhys);

    var ctx = new Context {
      Image = image,
      FsRecords = fsRecords,
      CurrentXid = BinaryPrimitives.ReadUInt64LittleEndian(nx[16..]),
      NextOid = Math.Max(BinaryPrimitives.ReadUInt64LittleEndian(apsb[APSB_NEXT_OBJ_ID..]), APFS_MIN_USER_INO_NUM),
      FsTreeRootBlock = fsTreePhys,
      FsTreeOid = fsTreeOid,
      FsTreeVirtOid = fsTreeVirtOid,
      FsTreeType = fsTreeType,
      ApsbBlock = apsbPhys,
      ApsbVirtOid = apsbVirtOid,
      CtrOmapBlock = ctrOmapBlock,
      CtrOmapTreeBlock = ctrOmapTreeBlock,
      VolOmapBlock = volOmapBlock,
      VolOmapTreeBlock = volOmapTreeBlock,
    };

    // Seed the next-allocated inode above any in-use inode.
    foreach (var rec in fsRecords) {
      var oidAndType = BinaryPrimitives.ReadUInt64LittleEndian(rec.Key);
      if ((int)(oidAndType >> 60) != APFS_TYPE_INODE) continue;
      var ino = oidAndType & 0x0FFFFFFFFFFFFFFFUL;
      if (ino >= ctx.NextOid) ctx.NextOid = ino + 1;
    }

    ctx.Allocator = new ApfsBlockAllocator(initialBlocks: (ulong)(image.Length / BlockSize));
    return ctx;
  }

  // ── Record builders (j_inode_val_t, j_drec_*, j_file_extent_*) ──────────

  /// <summary>
  /// Constructs the three records that materialise a regular file: a DIR_REC
  /// under its parent, an INODE record for the file itself, and (when data is
  /// non-empty) a single FILE_EXTENT pointing at the first allocated block.
  /// </summary>
  private static List<ApfsBtreeOps.Record> BuildFileRecords(ulong parentIno, ulong fileIno, string name,
      byte[] data, ulong physBlock) {
    var list = new List<ApfsBtreeOps.Record>(3) {
      new(BuildDrecKey(parentIno, name), BuildDrecValue(fileIno, isDir: false)),
      new(BuildInodeKey(fileIno), BuildInodeValue(fileIno, parentIno, data.LongLength, isDir: false, nchildren: 1)),
    };
    // The stream's share count, which a driver reads before it opens the file.
    list.Add(new ApfsBtreeOps.Record(
      ApfsInodeRecord.BuildDstreamIdKey(fileIno), ApfsInodeRecord.BuildDstreamIdValue(1)));
    if (data.Length > 0)
      list.Add(new ApfsBtreeOps.Record(BuildFileExtentKey(fileIno, 0),
        BuildFileExtentValue((ulong)data.LongLength, physBlock)));
    return list;
  }

  private static byte[] BuildInodeKey(ulong ino) {
    var k = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(k, ino | ((ulong)APFS_TYPE_INODE << 60));
    return k;
  }

  private static byte[] BuildDrecKey(ulong parentOid, string name)
    => ApfsDrecKey.Build(parentOid, name);

  private static byte[] BuildFileExtentKey(ulong ino, ulong logicalOffset) {
    var k = new byte[16];
    BinaryPrimitives.WriteUInt64LittleEndian(k, ino | ((ulong)APFS_TYPE_FILE_EXTENT << 60));
    BinaryPrimitives.WriteUInt64LittleEndian(k.AsSpan(8), logicalOffset);
    return k;
  }

  private static byte[] BuildInodeValue(ulong ino, ulong parentId, long size, bool isDir, uint nchildren)
    => ApfsInodeRecord.BuildValue(ino, parentId, size, isDir, nchildren);


  private static byte[] BuildDrecValue(ulong fileId, bool isDir) {
    var v = new byte[18];
    BinaryPrimitives.WriteUInt64LittleEndian(v, fileId);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8),
      (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL);
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(16), (ushort)(isDir ? DT_DIR : DT_REG));
    return v;
  }

  /// <summary>Writes one file extent, whose length names whole blocks.</summary>
  private static byte[] BuildFileExtentValue(ulong lengthBytes, ulong physBlockNum) {
    var v = new byte[24];
    var covered = (lengthBytes + DEFAULT_BLOCK_SIZE - 1) / DEFAULT_BLOCK_SIZE * DEFAULT_BLOCK_SIZE;
    BinaryPrimitives.WriteUInt64LittleEndian(v, covered & 0x00FFFFFFFFFFFFFFUL);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), physBlockNum);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), 0);
    return v;
  }
}

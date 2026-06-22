#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// Builds a complete, fsck-clean OCFS2 (Oracle Cluster Filesystem 2) image from
/// scratch in the single-node "local" (non-clustered) variant — the layout the
/// reference <c>mkfs.ocfs2 -M local -N 1</c> produces with feature set
/// <c>local | extended-slotmap | inline-data | append-dio</c> (incompat 0x8148)
/// and <c>strict-journal-super</c> (compat 0x2). No metaecc, so the per-block
/// <c>ocfs2_block_check</c> (CRC32C + ECC) stays zero.
///
/// Fixed block layout (4 KB block == 4 KB cluster):
/// <code>
///   0,1   reserved
///   2     superblock dinode             ("OCFSV2")
///   3     global_bitmap   group descriptor (GROUP01, chain 0)
///   4     global_inode_alloc group descriptor (GROUP01, chain 0)
///   5     root directory dinode         (inline dir)
///   6     system directory dinode       (inline dir)
///   7     bad_blocks dinode
///   8     global_inode_alloc dinode     (chain allocator)
///   9     slot_map dinode
///   10    heartbeat dinode
///   11    global_bitmap dinode          (chain allocator over all clusters)
///   12    orphan_dir:0000 dinode        (inline dir)
///   13    extent_alloc:0000 dinode      (chain allocator, empty)
///   14    inode_alloc:0000 dinode       (chain allocator, per-slot inodes)
///   15    journal:0000 dinode           (JBD2)
///   16    local_alloc:0000 dinode
///   17    truncate_log:0000 dinode
/// </code>
/// The global_inode_alloc group at block 4 owns a contiguous run of blocks
/// (blocks 4..4+groupBits-1); every system dinode is a bit within it. Heartbeat,
/// journal and slot_map data follow; then the per-slot inode_alloc group, which
/// owns lost+found and all user-file dinodes; finally user file data clusters.
/// The global_bitmap marks every block up to the end of the inode_alloc group as
/// used, mirroring mkfs.
/// </summary>
internal sealed class Ocfs2Writer {

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Volume label written into <c>s_label</c> (64-byte field, NUL-padded). Capped at 63 ASCII bytes.</summary>
  private string _label = "OCFS2VOL";

  internal const int BlockSize = 4096;
  internal const int ClusterSize = 4096;
  internal const int BlockSizeBits = 12;
  internal const int ClusterSizeBits = 12;

  internal const int SuperBlockBlkno = 2;
  internal const int GlobalBitmapGroupBlkno = 3;
  internal const int InodeAllocGroupBlkno = 4;
  internal const int RootDirBlkno = 5;
  internal const int SystemDirBlkno = 6;
  internal const int BadBlocksBlkno = 7;
  internal const int GlobalInodeAllocBlkno = 8;
  internal const int SlotMapBlkno = 9;
  internal const int HeartbeatBlkno = 10;
  internal const int GlobalBitmapBlkno = 11;
  internal const int OrphanDirBlkno = 12;
  internal const int ExtentAllocBlkno = 13;
  internal const int InodeAllocBlkno = 14;
  internal const int JournalBlkno = 15;
  internal const int LocalAllocBlkno = 16;
  internal const int TruncateLogBlkno = 17;
  internal const int SystemDinodeCount = TruncateLogBlkno - InodeAllocGroupBlkno + 1; // blocks 4..17 = 14

  // ── Layout accessors used by the in-place modifier / descriptor ──
  // The cluster allocation bitmap lives in the global_bitmap group descriptor
  // (block 3) at byte offset BitmapInGroupOffset within that block.
  internal const int BitmapDataBlkno = GlobalBitmapGroupBlkno; // block 3 (group desc holds bg_bitmap)
  internal const int BitmapInGroupOffset = 0x40;               // ocfs2_group_desc.bg_bitmap
  internal const int FirstFileBlkno = TruncateLogBlkno + 1;    // first non-system block

  // Tunables (kept small but spec-valid; fsck does not enforce mkfs minimums).
  private const int HeartbeatClusters = 1;   // mkfs uses 256; fsck only needs i_size to match the extent
  private const int JournalClusters = 16;    // valid JBD2 journal; mkfs uses 1024

  private static readonly byte[] SuperSignature = "OCFSV2"u8.ToArray();
  private static readonly byte[] InodeSignature = "INODE01"u8.ToArray();
  private static readonly byte[] GroupSignature = "GROUP01"u8.ToArray();

  // Deterministic generation + uuid (mkfs randomises these).
  private const uint FsGeneration = 0xD1300777u;
  private static readonly byte[] Uuid = [
    0xF2, 0xB9, 0xAD, 0xB5, 0x11, 0xD9, 0x47, 0xC7,
    0x95, 0x4A, 0xD0, 0xCB, 0x42, 0xD7, 0xA0, 0x01,
  ];
  private const uint MkTime = 0x6A324A82u;

  private const int Id2Offset = 0xC0;
  private const int InlineHeaderLen = 8;     // ocfs2_inline_data header
  private const int ListHeaderLen = 0x10;    // ocfs2_extent_list / ocfs2_chain_list header
  internal const int MaxInline = BlockSize - Id2Offset - InlineHeaderLen; // 3896
  private const int DynFeaturesOffset = 0x76;

  // i_flags
  private const uint FlValid = 0x00000001;
  private const uint FlSystem = 0x00000010;
  private const uint FlSuperBlock = 0x00000020;
  private const uint FlLocalAlloc = 0x00000040;
  private const uint FlBitmap = 0x00000080;
  private const uint FlJournal = 0x00000100;
  private const uint FlHeartbeat = 0x00000200;
  private const uint FlChain = 0x00000400;
  private const uint FlDealloc = 0x00000800;

  // i_dyn_features
  private const ushort DynInlineData = 0x0001;

  // POSIX mode bits
  private const uint S_IFDIR = 0x4000;
  private const uint S_IFREG = 0x8000;
  private const uint ModeDir = S_IFDIR | 0x1ED;   // 0755
  private const uint ModeFile = S_IFREG | 0x1A4;  // 0644

  // Directory-entry file types.
  private const byte FtRegFile = 1;
  private const byte FtDir = 2;

  // Chain-allocator geometry. cl_count is the max chain records that fit in id2
  // for a 4 KB block: (4096 - 0xC0 - 0x10) / sizeof(chain_rec=16) = 243.
  private const int ChainListCount = 243;

  // ocfs2_dinode.id1.bitmap1 lives at byte 0xB8 (i_used) / 0xBC (i_total).
  private const int Id1UsedOffset = 0xB8;
  private const int Id1TotalOffset = 0xBC;

  // Standard clusters-per-group for the global cluster bitmap at 4 KB blocks:
  // a single group descriptor's bg_bitmap holds (blocksize - 0x40) * 8 bits.
  private const int ClustersPerGroup = (BlockSize - 0x40) * 8; // 32256

  /// <summary>Sets the volume label written into <c>s_label</c> (capped at 63 ASCII bytes).</summary>
  public void SetLabel(string label) {
    ArgumentNullException.ThrowIfNull(label);
    this._label = label.Length > 63 ? label[..63] : label;
  }

  /// <summary>Adds a file to the image. '/' separators create directories.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var normalized = name.Replace('\\', '/').Trim('/');
    if (string.IsNullOrEmpty(Path.GetFileName(normalized)))
      throw new ArgumentException("File name must not be empty.", nameof(name));
    _files.Add((normalized, data));
  }

  /// <summary>A node in the directory tree assembled from the added file paths.</summary>
  private sealed class TreeNode {
    public required string Name;
    public bool IsDir;
    public byte[] Data = [];
    public readonly Dictionary<string, TreeNode> Children = new(StringComparer.Ordinal);
    public readonly List<TreeNode> Order = [];

    // Layout assignment.
    public long DinodeBlkno;
    public long ParentBlkno;
    public long DataBlkno;
    public int DataClusters;
    public int InodeAllocBit; // bit index within the inode_alloc group
  }

  private TreeNode BuildTree() {
    var root = new TreeNode { Name = "", IsDir = true };
    foreach (var (path, data) in _files) {
      var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      var cur = root;
      for (var i = 0; i < parts.Length; i++) {
        var part = parts[i];
        var isLeaf = i == parts.Length - 1;
        if (cur.Children.TryGetValue(part, out var child)) {
          if (!isLeaf) child.IsDir = true;
        } else {
          child = new TreeNode { Name = part, IsDir = !isLeaf };
          cur.Children[part] = child;
          cur.Order.Add(child);
        }
        if (isLeaf && !child.IsDir) child.Data = data;
        cur = child;
      }
    }
    return root;
  }

  // ───────────────────────── layout plan ─────────────────────────

  private sealed class Plan {
    public long TotalBlocks;
    public int InodeAllocGroupBits;   // bg_bits of the global_inode_alloc group (owns blocks 4..)
    public int InodeAllocGroupBlock = InodeAllocGroupBlkno;
    public long HeartbeatData;        // first block of heartbeat data
    public long JournalData;          // first block of journal data
    public long SlotMapData;          // slot_map data block
    public long PerSlotGroupBlock;    // inode_alloc:0000 group descriptor block
    public int PerSlotGroupBits;      // bg_bits of the per-slot inode group
    public long LostFoundBlkno;
    public long FirstUserDataBlock;
    public int UsedClusters;          // clusters marked used in global_bitmap
    public TreeNode Root = null!;
    public List<TreeNode> Dirs = null!;
    public List<TreeNode> Files = null!;
    public int PerSlotUsedBits;       // bits set in the per-slot inode group bitmap
  }

  private Plan BuildPlan() {
    var root = BuildTree();
    root.DinodeBlkno = RootDirBlkno;

    var dirs = new List<TreeNode>();
    var files = new List<TreeNode>();

    // Per-slot inode group owns: bit 0 = the group descriptor itself, bit 1 =
    // lost+found, then one bit per user dir/file dinode.
    var perSlotGroupBlock = (long)0; // filled after data regions are sized
    var userInodes = new List<TreeNode>();
    void CollectInodes(TreeNode node) {
      foreach (var c in node.Order) {
        userInodes.Add(c);
        if (c.IsDir) dirs.Add(c); else files.Add(c);
      }
      foreach (var c in node.Order)
        if (c.IsDir)
          CollectInodes(c);
    }
    CollectInodes(root);

    // global_inode_alloc group: owns blocks 4 .. (4+bits-1). Must cover every
    // system dinode (blocks 4..17 == 14 bits). Round the group up so the heartbeat
    // region begins right after it, mirroring mkfs (contiguous group ownership).
    var inodeGroupBits = SystemDinodeCount; // 14 — exactly the system dinodes
    var afterInodeGroup = InodeAllocGroupBlkno + inodeGroupBits; // first free block

    var heartbeatData = (long)afterInodeGroup;
    var journalData = heartbeatData + HeartbeatClusters;
    var slotMapData = journalData + JournalClusters;
    perSlotGroupBlock = slotMapData + 1;

    // Per-slot inode group: bit0 = group block, bit1 = lost+found, then user inodes.
    var lostFoundBlkno = perSlotGroupBlock + 1;
    var perSlotUsedBits = 2 + userInodes.Count;

    // Assign user dinode blocks contiguously after lost+found.
    var nextInode = lostFoundBlkno + 1;
    for (var i = 0; i < userInodes.Count; i++) {
      userInodes[i].DinodeBlkno = nextInode++;
      userInodes[i].InodeAllocBit = 2 + i;
    }

    // The per-slot inode group must be large enough to hold all its bits.
    var perSlotGroupBits = Math.Max(perSlotUsedBits, 1);

    // Parent back-references.
    void SetParents(TreeNode node) {
      foreach (var c in node.Order) {
        c.ParentBlkno = node.DinodeBlkno;
        if (c.IsDir) SetParents(c);
      }
    }
    SetParents(root);

    // User data clusters begin after the per-slot inode group's owned range.
    var firstUserData = perSlotGroupBlock + perSlotGroupBits;
    var nextData = firstUserData;

    // Files: allocate data clusters (extent-backed). Inline-small files keep data
    // in the dinode; only files larger than the inline area need clusters.
    foreach (var f in files) {
      if (f.Data.Length > MaxInline) {
        var clusters = (f.Data.Length + ClusterSize - 1) / ClusterSize;
        f.DataBlkno = nextData;
        f.DataClusters = clusters;
        nextData += clusters;
      }
    }

    // Directories that overflow the inline area become extent-backed. The root
    // also carries a lost+found entry, so size it with that included.
    foreach (var dir in dirs.Prepend(root)) {
      var isRoot = dir.DinodeBlkno == RootDirBlkno;
      var inlineLen = InlineDirLength(dir, extraLostFound: isRoot, lostFoundBlkno: lostFoundBlkno);
      if (inlineLen <= MaxInline) continue;
      var blocks = BuildExtentDirBlocks(dir, isRoot ? lostFoundBlkno : 0);
      dir.Data = blocks;
      dir.DataBlkno = nextData;
      dir.DataClusters = blocks.Length / ClusterSize;
      nextData += dir.DataClusters;
    }

    var usedClusters = (int)nextData; // every cluster up to here is allocated
    var totalBlocks = Math.Max(nextData, usedClusters);
    // Keep a little tail of free space so the bitmap has free bits (fsck is happy
    // either way, but mkfs always leaves slack).
    totalBlocks = Math.Max(totalBlocks + 8, 64);

    return new Plan {
      TotalBlocks = totalBlocks,
      InodeAllocGroupBits = inodeGroupBits,
      HeartbeatData = heartbeatData,
      JournalData = journalData,
      SlotMapData = slotMapData,
      PerSlotGroupBlock = perSlotGroupBlock,
      PerSlotGroupBits = perSlotGroupBits,
      LostFoundBlkno = lostFoundBlkno,
      FirstUserDataBlock = firstUserData,
      UsedClusters = usedClusters,
      Root = root,
      Dirs = dirs,
      Files = files,
      PerSlotUsedBits = perSlotUsedBits,
    };
  }

  /// <summary>Builds the OCFS2 image and returns the raw bytes.</summary>
  public byte[] Build() {
    var plan = BuildPlan();
    var image = new byte[plan.TotalBlocks * BlockSize];

    WriteSuperblock(image, plan);
    WriteGlobalBitmap(image, plan);
    WriteGlobalInodeAlloc(image, plan);
    WriteRootDir(image, plan);
    WriteSystemDir(image, plan);
    WriteBadBlocks(image);
    WriteSlotMap(image, plan);
    WriteHeartbeat(image, plan);
    WriteOrphanDir(image);
    WriteExtentAlloc(image);
    WritePerSlotInodeAlloc(image, plan);
    WriteJournal(image, plan);
    WriteLocalAlloc(image);
    WriteTruncateLog(image);
    WriteLostFound(image, plan);

    // User directories and files.
    foreach (var dir in plan.Dirs)
      WriteDirDinode(image, dir);
    foreach (var dir in plan.Dirs.Prepend(plan.Root))
      if (dir.DataClusters > 0 && dir.Data.Length > 0)
        Buffer.BlockCopy(dir.Data, 0, image, (int)(dir.DataBlkno * BlockSize), dir.Data.Length);

    foreach (var f in plan.Files) {
      WriteFileDinode(image, f);
      if (f.DataClusters > 0 && f.Data.Length > 0)
        Buffer.BlockCopy(f.Data, 0, image, (int)(f.DataBlkno * BlockSize), f.Data.Length);
    }

    return image;
  }

  public void WriteTo(Stream output) {
    var data = Build();
    output.Write(data, 0, data.Length);
  }

  // ───────────────────────── dinode header ─────────────────────────

  private static void WriteDinodeHeader(
      byte[] image, long blkno, uint mode, uint flags, long size, ushort links,
      int suballocSlot, int suballocBit) {
    var off = (int)(blkno * BlockSize);
    InodeSignature.CopyTo(image.AsSpan(off, InodeSignature.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x08, 4), FsGeneration);
    BinaryPrimitives.WriteInt16LittleEndian(image.AsSpan(off + 0x0C, 2), (short)suballocSlot);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x0E, 2), (ushort)suballocBit);

    var clusters = size > 0 ? (uint)((size + ClusterSize - 1) / ClusterSize) : 0;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x14, 4), clusters);

    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x20, 8), (ulong)size);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x28, 2), (ushort)mode);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x2A, 2), links);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x2C, 4), flags);

    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x30, 8), MkTime); // atime
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x38, 8), MkTime); // ctime
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x40, 8), MkTime); // mtime

    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x50, 8), (ulong)blkno);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x60, 4), FsGeneration);
  }

  private static void SetExtentRecord(byte[] image, long blkno, int recIdx, uint cpos, ushort clusters, long dataBlkno) {
    var rec = (int)(blkno * BlockSize) + Id2Offset + ListHeaderLen + recIdx * 16;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(rec + 0, 4), cpos);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(rec + 4, 2), clusters);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(rec + 8, 8), (ulong)dataBlkno);
  }

  private static void SetExtentListHeader(byte[] image, long blkno, ushort count, ushort nextFree) {
    var off = (int)(blkno * BlockSize) + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0, 2), 0);        // l_tree_depth
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 2, 2), count);    // l_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 4, 2), nextFree); // l_next_free_rec
  }

  // l_count for a leaf extent list inline in a dinode: (4096-0xC0-0x10)/16 = 243.
  private const int ExtentListCount = 243;

  // ───────────────────────── superblock ─────────────────────────

  private void WriteSuperblock(byte[] image, Plan plan) {
    WriteDinodeHeader(image, SuperBlockBlkno, 0, FlValid | FlSystem | FlSuperBlock, 0, 0, -1, 0xFFFF);
    var dinodeOff = (int)(SuperBlockBlkno * BlockSize);
    image.AsSpan(dinodeOff, 8).Clear();
    SuperSignature.CopyTo(image.AsSpan(dinodeOff, SuperSignature.Length));

    // i_clusters = total clusters; i_blkno stays 2; i_size 0.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + 0x14, 4), (uint)plan.TotalBlocks);

    var off = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x00, 2), 0);    // s_major_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x02, 2), 90);   // s_minor_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x06, 2), 20);   // s_max_mnt_count
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x10, 8), MkTime); // s_lastcheck
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x1C, 4), 0x0002);   // s_feature_compat: JBD2_SB
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x20, 4), 0x8148);   // s_feature_incompat: append-dio|extended-slotmap|inline-data|local
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x24, 4), 0);        // s_feature_ro_compat
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x28, 8), RootDirBlkno);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x30, 8), SystemDirBlkno);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x38, 4), BlockSizeBits);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x3C, 4), ClusterSizeBits);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x40, 2), 1);        // s_max_slots
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x48, 8), GlobalBitmapGroupBlkno); // s_first_cluster_group
    // s_label[64] @ +0x50 — NUL-padded ASCII. The field is zeroed by the
    // image allocation, so a short label leaves the tail clean for the reader.
    var labelBytes = Encoding.ASCII.GetBytes(this._label);
    labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 64)).CopyTo(image.AsSpan(off + 0x50, 64));
    Uuid.CopyTo(image.AsSpan(off + 0x90, 16)); // s_uuid
  }

  // ───────────────────────── group descriptors ─────────────────────────

  /// <summary>
  /// Writes an <c>ocfs2_group_desc</c> (GROUP01) at <paramref name="blkno"/>.
  /// <paramref name="bits"/> total bits in the group, <paramref name="usedBits"/>
  /// the count marked used (bits 0..usedBits-1 set in the bitmap).
  /// </summary>
  private static void WriteGroupDesc(byte[] image, long blkno, int bits, int usedBits, long parentInode) {
    var off = (int)(blkno * BlockSize);
    GroupSignature.CopyTo(image.AsSpan(off, GroupSignature.Length));
    // bg_size: bytes available for the bitmap = blocksize - header(0x40), capped.
    var bgSize = BlockSize - 0x40; // 4032
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x08, 2), (ushort)bgSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x0A, 2), (ushort)bits);          // bg_bits
    var freeBits = bits - usedBits;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x0C, 2), (ushort)freeBits);      // bg_free_bits_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x0E, 2), 0);                     // bg_chain
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x10, 4), FsGeneration);          // bg_generation
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x14, 2), (ushort)freeBits);      // bg_contig_free_bits
    // bg_next_group @0x18 = 0
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x20, 8), (ulong)parentInode);    // bg_parent_dinode
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x28, 8), (ulong)blkno);          // bg_blkno

    // bg_bitmap @0x40: set bits [0, usedBits).
    var bmp = off + 0x40;
    for (var i = 0; i < usedBits; i++)
      image[bmp + (i >> 3)] |= (byte)(1 << (i & 7));
  }

  private static void WriteGlobalBitmap(byte[] image, Plan plan) {
    // Dinode (chain allocator over all clusters).
    WriteDinodeHeader(image, GlobalBitmapBlkno, ModeFile, FlValid | FlSystem | FlBitmap | FlChain,
      plan.TotalBlocks * ClusterSize, 1, -1, GlobalBitmapBlkno);

    var dinodeOff = (int)(GlobalBitmapBlkno * BlockSize);
    // id1.bitmap1 { i_used @0xB8, i_total @0xBC }
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + Id1UsedOffset, 4), (uint)plan.UsedClusters);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + Id1TotalOffset, 4), (uint)plan.TotalBlocks);

    // id2.i_chain
    var ch = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x00, 2), (ushort)ClustersPerGroup); // cl_cpg (fixed 32256)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x02, 2), 1);                        // cl_bpc
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x04, 2), ChainListCount);           // cl_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x06, 2), 1);                        // cl_next_free_rec
    // chain rec 0 @0x10: c_free, c_total, c_blkno
    var free = (int)plan.TotalBlocks - plan.UsedClusters;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ch + 0x10, 4), (uint)free);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ch + 0x14, 4), (uint)plan.TotalBlocks);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ch + 0x18, 8), GlobalBitmapGroupBlkno);

    // Group descriptor at block 3: covers all clusters, used = plan.UsedClusters.
    WriteGroupDesc(image, GlobalBitmapGroupBlkno, (int)plan.TotalBlocks, plan.UsedClusters, GlobalBitmapBlkno);
  }

  private static void WriteGlobalInodeAlloc(byte[] image, Plan plan) {
    var bits = plan.InodeAllocGroupBits;
    var used = SystemDinodeCount; // every system dinode block 4..17
    var size = (long)bits * BlockSize;
    WriteDinodeHeader(image, GlobalInodeAllocBlkno, ModeFile, FlValid | FlSystem | FlBitmap | FlChain,
      size, 1, -1, GlobalInodeAllocBlkno - InodeAllocGroupBlkno);

    var dinodeOff = (int)(GlobalInodeAllocBlkno * BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + Id1UsedOffset, 4), (uint)used);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + Id1TotalOffset, 4), (uint)bits);

    var ch = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x00, 2), (ushort)bits); // cl_cpg
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x02, 2), 1);            // cl_bpc
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x04, 2), ChainListCount);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x06, 2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ch + 0x10, 4), (uint)(bits - used)); // c_free
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ch + 0x14, 4), (uint)bits);          // c_total
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ch + 0x18, 8), InodeAllocGroupBlkno);

    WriteGroupDesc(image, InodeAllocGroupBlkno, bits, used, GlobalInodeAllocBlkno);
    // The system dinodes are blocks 4..17. The group descriptor IS block 4, and it
    // counts as bit 0. Bits 1..13 = blocks 5..17. usedBits=14 covers exactly 4..17.
  }

  // ───────────────────────── system directories & files ─────────────────────────

  private void WriteRootDir(byte[] image, Plan plan) {
    var node = plan.Root;
    var links = (ushort)(2 + node.Order.Count(c => c.IsDir) + 1); // +1 for lost+found

    if (node.DataClusters > 0) {
      WriteExtentDir(image, node, RootDirBlkno, (long)node.DataClusters * ClusterSize, links,
        FlValid | FlSystem, RootDirBlkno - InodeAllocGroupBlkno);
      return;
    }
    var inline = BuildInlineDir(node, extraLostFound: true, lostFoundBlkno: plan.LostFoundBlkno);
    WriteInlineDir(image, RootDirBlkno, inline, links, FlValid | FlSystem, RootDirBlkno - InodeAllocGroupBlkno);
  }

  private void WriteSystemDir(byte[] image, Plan plan) {
    var entries = new List<(long Inode, string Name, byte Type)> {
      (SystemDirBlkno, ".", FtDir),
      (SystemDirBlkno, "..", FtDir), // system dir is its own parent
      (BadBlocksBlkno, "bad_blocks", FtRegFile),
      (GlobalInodeAllocBlkno, "global_inode_alloc", FtRegFile),
      (SlotMapBlkno, "slot_map", FtRegFile),
      (HeartbeatBlkno, "heartbeat", FtRegFile),
      (GlobalBitmapBlkno, "global_bitmap", FtRegFile),
      (OrphanDirBlkno, "orphan_dir:0000", FtDir),
      (ExtentAllocBlkno, "extent_alloc:0000", FtRegFile),
      (InodeAllocBlkno, "inode_alloc:0000", FtRegFile),
      (JournalBlkno, "journal:0000", FtRegFile),
      (LocalAllocBlkno, "local_alloc:0000", FtRegFile),
      (TruncateLogBlkno, "truncate_log:0000", FtRegFile),
    };
    var inline = BuildDirEntriesFillingInline(entries);
    // sysdir links: 2 (./..) + 1 for orphan_dir's ".." back-ref = 3.
    WriteInlineDir(image, SystemDirBlkno, inline, 3, FlValid | FlSystem, SystemDirBlkno - InodeAllocGroupBlkno);
  }

  private static void WriteBadBlocks(byte[] image) {
    WriteDinodeHeader(image, BadBlocksBlkno, ModeFile, FlValid | FlSystem, 0, 1, -1, BadBlocksBlkno - InodeAllocGroupBlkno);
    SetExtentListHeader(image, BadBlocksBlkno, ExtentListCount, 0);
  }

  private static void WriteSlotMap(byte[] image, Plan plan) {
    // slot_map is a regular file with one cluster of data; extended-slotmap means
    // the data is ocfs2_extended_slot[] — all zero (no node mounted) is valid.
    WriteDinodeHeader(image, SlotMapBlkno, ModeFile, FlValid | FlSystem, ClusterSize, 1, -1, SlotMapBlkno - InodeAllocGroupBlkno);
    SetExtentListHeader(image, SlotMapBlkno, ExtentListCount, 1);
    SetExtentRecord(image, SlotMapBlkno, 0, 0, 1, plan.SlotMapData);
    // data left zeroed: es_valid=0 for the single slot.
  }

  private static void WriteHeartbeat(byte[] image, Plan plan) {
    var size = (long)HeartbeatClusters * ClusterSize;
    WriteDinodeHeader(image, HeartbeatBlkno, ModeFile, FlValid | FlSystem | FlHeartbeat, size, 1, -1, HeartbeatBlkno - InodeAllocGroupBlkno);
    SetExtentListHeader(image, HeartbeatBlkno, ExtentListCount, 1);
    SetExtentRecord(image, HeartbeatBlkno, 0, 0, (ushort)HeartbeatClusters, plan.HeartbeatData);
  }

  private static void WriteOrphanDir(byte[] image) {
    var entries = new List<(long Inode, string Name, byte Type)> {
      (OrphanDirBlkno, ".", FtDir),
      (SystemDirBlkno, "..", FtDir),
    };
    var inline = BuildDirEntriesFillingInline(entries);
    WriteInlineDir(image, OrphanDirBlkno, inline, 2, FlValid | FlSystem, OrphanDirBlkno - InodeAllocGroupBlkno);
  }

  private static void WriteExtentAlloc(byte[] image) {
    // Empty chain allocator for extent blocks.
    WriteDinodeHeader(image, ExtentAllocBlkno, ModeFile, FlValid | FlSystem | FlBitmap | FlChain, 0, 1, -1, ExtentAllocBlkno - InodeAllocGroupBlkno);
    var dinodeOff = (int)(ExtentAllocBlkno * BlockSize);
    var ch = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x00, 2), 1024); // cl_cpg
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x02, 2), 1);    // cl_bpc
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x04, 2), ChainListCount);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x06, 2), 0);    // cl_next_free_rec = empty
  }

  private static void WritePerSlotInodeAlloc(byte[] image, Plan plan) {
    var bits = plan.PerSlotGroupBits;
    var used = plan.PerSlotUsedBits;
    var size = (long)bits * BlockSize;
    WriteDinodeHeader(image, InodeAllocBlkno, ModeFile, FlValid | FlSystem | FlBitmap | FlChain,
      size, 1, -1, InodeAllocBlkno - InodeAllocGroupBlkno);

    var dinodeOff = (int)(InodeAllocBlkno * BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + Id1UsedOffset, 4), (uint)used);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + Id1TotalOffset, 4), (uint)bits);

    var ch = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x00, 2), (ushort)Math.Min(bits, 1024)); // cl_cpg
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x02, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x04, 2), ChainListCount);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(ch + 0x06, 2), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ch + 0x10, 4), (uint)(bits - used));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(ch + 0x14, 4), (uint)bits);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(ch + 0x18, 8), (ulong)plan.PerSlotGroupBlock);

    WriteGroupDesc(image, plan.PerSlotGroupBlock, bits, used, InodeAllocBlkno);
  }

  private static void WriteJournal(byte[] image, Plan plan) {
    var size = (long)JournalClusters * ClusterSize;
    WriteDinodeHeader(image, JournalBlkno, ModeFile, FlValid | FlSystem | FlJournal, size, 1, -1, JournalBlkno - InodeAllocGroupBlkno);
    SetExtentListHeader(image, JournalBlkno, ExtentListCount, 1);
    SetExtentRecord(image, JournalBlkno, 0, 0, (ushort)JournalClusters, plan.JournalData);

    // JBD2 journal superblock (big-endian) in the first journal data block.
    var jb = (int)(plan.JournalData * BlockSize);
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x00, 4), 0xC03B3998u); // h_magic
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x04, 4), 4);            // h_blocktype = JBD2_SUPERBLOCK_V2
    // h_sequence @0x08 = 0
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x0C, 4), BlockSize);    // s_blocksize
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x10, 4), (uint)JournalClusters); // s_maxlen
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x14, 4), 1);            // s_first
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x18, 4), 1);            // s_sequence
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x1C, 4), 1);            // s_start
    Uuid.CopyTo(image.AsSpan(jb + 0x30, 16));                                        // s_uuid
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(jb + 0x40, 4), 1);            // s_nr_users
  }

  private static void WriteLocalAlloc(byte[] image) {
    WriteDinodeHeader(image, LocalAllocBlkno, ModeFile, FlValid | FlSystem | FlLocalAlloc | FlBitmap, 0, 1, -1, LocalAllocBlkno - InodeAllocGroupBlkno);
    var off = (int)(LocalAllocBlkno * BlockSize) + Id2Offset;
    // ocfs2_local_alloc header is 16 bytes (la_bm_off u32 + la_size u16 +
    // la_reserved1 u16 + la_reserved2 u64); la_bitmap follows.
    var laSize = BlockSize - Id2Offset - 16; // 3888
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x04, 2), (ushort)laSize);
  }

  private static void WriteTruncateLog(byte[] image) {
    WriteDinodeHeader(image, TruncateLogBlkno, ModeFile, FlValid | FlSystem | FlDealloc, 0, 1, -1, TruncateLogBlkno - InodeAllocGroupBlkno);
    var off = (int)(TruncateLogBlkno * BlockSize) + Id2Offset;
    // ocfs2_truncate_log: tl_count @0x00 = max records that fit, tl_used @0x02 = 0.
    var tlCount = (BlockSize - Id2Offset - 8) / 8; // (4096-0xC0-8)/sizeof(rec=8)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x00, 2), (ushort)tlCount);
  }

  private static void WriteLostFound(byte[] image, Plan plan) {
    var entries = new List<(long Inode, string Name, byte Type)> {
      (plan.LostFoundBlkno, ".", FtDir),
      (RootDirBlkno, "..", FtDir),
    };
    var inline = BuildDirEntriesFillingInline(entries);
    // lost+found is allocated from the per-slot inode group, so suballoc_slot=0,
    // suballoc_bit=1 (bit 1 in that group).
    WriteInlineDirWithSlot(image, plan.LostFoundBlkno, inline, 2, FlValid, 0, 1);
  }

  // ───────────────────────── user dir/file dinodes ─────────────────────────

  private void WriteDirDinode(byte[] image, TreeNode node) {
    var links = (ushort)(2 + node.Order.Count(c => c.IsDir));
    if (node.DataClusters > 0) {
      WriteExtentDir(image, node, node.DinodeBlkno, (long)node.DataClusters * ClusterSize, links, FlValid, node.InodeAllocBit, slot: 0);
      return;
    }
    var inline = BuildInlineDir(node);
    WriteInlineDirWithSlot(image, node.DinodeBlkno, inline, links, FlValid, 0, node.InodeAllocBit);
  }

  private static void WriteFileDinode(byte[] image, TreeNode f) {
    var size = f.Data.Length;
    if (size <= MaxInline) {
      WriteDinodeHeaderWithSlot(image, f.DinodeBlkno, ModeFile, FlValid, size, 1, 0, f.InodeAllocBit);
      var dinodeOff = (int)(f.DinodeBlkno * BlockSize);
      // Inline files keep their bytes in the dinode → i_clusters must be 0.
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + 0x14, 4), 0);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dinodeOff + DynFeaturesOffset, 2), DynInlineData);
      var off = dinodeOff + Id2Offset;
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off, 2), MaxInline); // id_count
      if (size > 0) Buffer.BlockCopy(f.Data, 0, image, off + InlineHeaderLen, size);
      return;
    }
    WriteDinodeHeaderWithSlot(image, f.DinodeBlkno, ModeFile, FlValid, size, 1, 0, f.InodeAllocBit);
    SetExtentListHeader(image, f.DinodeBlkno, ExtentListCount, 1);
    SetExtentRecord(image, f.DinodeBlkno, 0, 0, (ushort)f.DataClusters, f.DataBlkno);
  }

  // Header variant that takes an explicit suballoc slot (for inodes allocated
  // from the per-slot inode_alloc, slot 0).
  private static void WriteDinodeHeaderWithSlot(byte[] image, long blkno, uint mode, uint flags, long size, ushort links, int slot, int bit) {
    WriteDinodeHeader(image, blkno, mode, flags, size, links, slot, bit);
  }

  private static void WriteExtentDir(byte[] image, TreeNode node, long blkno, long size, ushort links, uint flags, int bit, int slot = -1) {
    WriteDinodeHeader(image, blkno, ModeDir, flags, size, links, slot, bit);
    SetExtentListHeader(image, blkno, ExtentListCount, 1);
    SetExtentRecord(image, blkno, 0, 0, (ushort)node.DataClusters, node.DataBlkno);
  }

  private static void WriteInlineDir(byte[] image, long blkno, byte[] inline, ushort links, uint flags, int bit) =>
    WriteInlineDirWithSlot(image, blkno, inline, links, flags, -1, bit);

  private static void WriteInlineDirWithSlot(byte[] image, long blkno, byte[] inline, ushort links, uint flags, int slot, int bit) {
    WriteDinodeHeader(image, blkno, ModeDir, flags, MaxInline, links, slot, bit);
    var dinodeOff = (int)(blkno * BlockSize);
    // i_size for inline dirs == id_count (full inline area), matching mkfs.
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dinodeOff + 0x20, 8), MaxInline);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(dinodeOff + 0x14, 4), 0); // i_clusters = 0
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dinodeOff + DynFeaturesOffset, 2), DynInlineData);
    var off = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off, 2), MaxInline); // id_count
    if (inline.Length > MaxInline)
      throw new InvalidOperationException("Inline dir overflow not planned as extent-backed.");
    if (inline.Length > 0) Buffer.BlockCopy(inline, 0, image, off + InlineHeaderLen, inline.Length);
  }

  // ───────────────────────── directory entry builders ─────────────────────────

  /// <summary>
  /// Builds inline dir-entry bytes that EXACTLY fill the inline area (MaxInline):
  /// the final entry's rec_len is stretched to consume the remaining space, which
  /// is what mkfs and the kernel do for inline directories.
  /// </summary>
  /// <summary>Packed byte length of a set of dir entries (no inline stretch).</summary>
  private static int InlineDirByteLength(List<(long Inode, string Name, byte Type)> entries) {
    var total = 0;
    foreach (var (_, name, _) in entries)
      total += (12 + Encoding.UTF8.GetByteCount(name) + 3) & ~3;
    return total;
  }

  private static byte[] BuildDirEntriesFillingInline(List<(long Inode, string Name, byte Type)> entries) {
    var buf = new byte[MaxInline];
    var pos = 0;
    var lastOff = 0;
    for (var i = 0; i < entries.Count; i++) {
      var (inode, name, type) = entries[i];
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var recLen = (12 + nameBytes.Length + 3) & ~3;
      BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(pos, 8), (ulong)inode);
      BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(pos + 8, 2), (ushort)recLen);
      buf[pos + 10] = (byte)nameBytes.Length;
      buf[pos + 11] = type;
      nameBytes.CopyTo(buf.AsSpan(pos + 12, nameBytes.Length));
      lastOff = pos;
      pos += recLen;
    }
    // Stretch the last entry to fill the inline area.
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(lastOff + 8, 2), (ushort)(MaxInline - lastOff));
    return buf;
  }

  /// <summary>Assembles the dir-entry list for a user directory tree node.</summary>
  private static List<(long Inode, string Name, byte Type)> DirEntryList(TreeNode node, bool extraLostFound, long lostFoundBlkno) {
    var parent = node.DinodeBlkno == RootDirBlkno ? RootDirBlkno
               : node.ParentBlkno == 0 ? RootDirBlkno : node.ParentBlkno;
    var entries = new List<(long Inode, string Name, byte Type)> {
      (node.DinodeBlkno, ".", FtDir),
      (parent, "..", FtDir),
    };
    if (extraLostFound)
      entries.Add((lostFoundBlkno, "lost+found", FtDir));
    foreach (var child in node.Order)
      entries.Add((child.DinodeBlkno, child.Name, child.IsDir ? FtDir : FtRegFile));
    return entries;
  }

  /// <summary>Builds inline dir entries for a user directory tree node.</summary>
  private static byte[] BuildInlineDir(TreeNode node, bool extraLostFound = false, long lostFoundBlkno = 0) =>
    BuildDirEntriesFillingInline(DirEntryList(node, extraLostFound, lostFoundBlkno));

  /// <summary>Packed inline byte length for a user directory tree node.</summary>
  private static int InlineDirLength(TreeNode node, bool extraLostFound = false, long lostFoundBlkno = 0) =>
    InlineDirByteLength(DirEntryList(node, extraLostFound, lostFoundBlkno));

  /// <summary>
  /// Lays out a directory's entries across whole 4 KB directory blocks for an
  /// extent-backed directory. No entry crosses a block boundary; each block's last
  /// entry is stretched to the block end.
  /// </summary>
  private static byte[] BuildExtentDirBlocks(TreeNode node, long lostFoundBlkno) {
    var entries = new List<(long Inode, string Name, byte Type)> {
      (node.DinodeBlkno, ".", FtDir),
      (node.DinodeBlkno == RootDirBlkno ? RootDirBlkno : node.ParentBlkno, "..", FtDir),
    };
    if (node.DinodeBlkno == RootDirBlkno && lostFoundBlkno != 0)
      entries.Add((lostFoundBlkno, "lost+found", FtDir));
    foreach (var child in node.Order)
      entries.Add((child.DinodeBlkno, child.Name, child.IsDir ? FtDir : FtRegFile));

    var blocks = new List<byte[]>();
    var block = new byte[BlockSize];
    var pos = 0;
    var lastOff = -1;

    void Flush() {
      if (lastOff >= 0)
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(lastOff + 8, 2), (ushort)(BlockSize - lastOff));
      blocks.Add(block);
      block = new byte[BlockSize];
      pos = 0;
      lastOff = -1;
    }

    foreach (var (inode, name, type) in entries) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      var recLen = (12 + nameBytes.Length + 3) & ~3;
      if (pos + recLen > BlockSize) Flush();
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(pos, 8), (ulong)inode);
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(pos + 8, 2), (ushort)recLen);
      block[pos + 10] = (byte)nameBytes.Length;
      block[pos + 11] = type;
      nameBytes.CopyTo(block.AsSpan(pos + 12, nameBytes.Length));
      lastOff = pos;
      pos += recLen;
    }
    Flush();

    var result = new byte[blocks.Count * BlockSize];
    for (var i = 0; i < blocks.Count; i++)
      Buffer.BlockCopy(blocks[i], 0, result, i * BlockSize, BlockSize);
    return result;
  }
}

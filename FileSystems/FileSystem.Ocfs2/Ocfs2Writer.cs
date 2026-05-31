#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// Builds a minimal OCFS2 (Oracle Cluster Filesystem 2) image from scratch.
/// Single-node mode (no DLM). Layout (4 KB block size, 4 KB cluster size):
///
///   Block 0:    Empty / reserved
///   Block 1:    Empty / reserved
///   Block 2:    Superblock dinode ("OCFSV2" magic at offset 0, ocfs2_super_block at +0xC0)
///   Block 3:    Global bitmap dinode (system file, tracks cluster allocation)
///   Block 4:    Global bitmap data (1 bit per cluster)
///   Block 5:    Root directory dinode (inline dir entries for user files)
///   Block 6:    System directory dinode (inline entries for global_bitmap, etc.)
///   Block 7:    Inode alloc dinode (system file, tracks inode allocation)
///   Block 8+:   File dinodes (1 block each), then file data (cluster-aligned)
///
/// Each dinode carries i_signature "OCFSV2", generation, mode, size, and either
/// inline data (for small dirs/files) or extent records pointing at data blocks.
/// </summary>
internal sealed class Ocfs2Writer {

  private readonly List<(string Name, byte[] Data)> _files = [];

  internal const int BlockSize = 4096;
  internal const int ClusterSize = 4096;
  internal const int BlockSizeBits = 12;
  internal const int ClusterSizeBits = 12;
  internal const int SuperBlockBlkno = 2;
  internal const int GlobalBitmapBlkno = 3;
  internal const int BitmapDataBlkno = 4;
  internal const int RootDirBlkno = 5;
  internal const int SystemDirBlkno = 6;
  internal const int InodeAllocBlkno = 7;
  internal const int FirstFileBlkno = 8;

  /// <summary>OCFS2 dinode signature.</summary>
  private static readonly byte[] Signature = "OCFSV2"u8.ToArray();

  /// <summary>Offset of id2 union within the dinode.</summary>
  private const int Id2Offset = 0xC0;

  // Inode flags
  private const uint InodeValid = 0x00000001;
  private const uint InodeSystemFile = 0x00000002;
  private const uint InodeIsDir = 0x00000004;
  private const uint InodeInlineData = 0x00000040;

  // Dinode i_dyn_features
  private const ushort DynInlineData = 0x0001;

  // POSIX mode bits
  private const uint S_IFDIR = 0x4000;
  private const uint S_IFREG = 0x8000;
  private const uint ModeDir = S_IFDIR | 0x1FF;  // drwxrwxrwx
  private const uint ModeFile = S_IFREG | 0x1B4;  // -rw-r--r--

  /// <summary>Adds a file to the image. The name may contain '/' separators,
  /// which place the file inside the corresponding directory tree.</summary>
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
    // Children keyed by name, in insertion order, for directories.
    public readonly Dictionary<string, TreeNode> Children = new(StringComparer.Ordinal);
    public readonly List<TreeNode> Order = [];

    // Layout assignment (filled in during planning).
    public long DinodeBlkno;
    public long ParentBlkno;
    public long DataBlkno;
    public int DataClusters;
  }

  /// <summary>Builds the directory tree from the flat list of path-named files.</summary>
  private TreeNode BuildTree() {
    var root = new TreeNode { Name = "", IsDir = true };
    foreach (var (path, data) in _files) {
      var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      var cur = root;
      for (var i = 0; i < parts.Length; i++) {
        var part = parts[i];
        var isLeaf = i == parts.Length - 1;
        if (cur.Children.TryGetValue(part, out var child)) {
          // Existing node — must remain a directory if more path follows.
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

  /// <summary>Builds the OCFS2 image and returns the raw bytes.</summary>
  public byte[] Build() {
    var root = BuildTree();

    // Plan block layout. Every directory and file gets one dinode block; file
    // data follows, cluster-aligned. The root directory reuses the fixed
    // RootDirBlkno so the superblock's s_root_blkno stays valid.
    var dirs = new List<TreeNode>();
    var files = new List<TreeNode>();
    var nextBlk = (long)FirstFileBlkno;

    void Plan(TreeNode node) {
      // node is a directory; assign dinode blocks to its children first so a
      // parent can reference them, then recurse.
      foreach (var child in node.Order) {
        child.DinodeBlkno = nextBlk++;
        child.ParentBlkno = node.DinodeBlkno;
        if (child.IsDir)
          dirs.Add(child);
        else
          files.Add(child);
      }
      foreach (var child in node.Order)
        if (child.IsDir)
          Plan(child);
    }

    root.DinodeBlkno = RootDirBlkno;
    Plan(root);

    // Assign data clusters to files.
    foreach (var f in files) {
      f.DataBlkno = nextBlk;
      var clusters = (f.Data.Length + ClusterSize - 1) / ClusterSize;
      if (clusters == 0 && f.Data.Length > 0) clusters = 1;
      f.DataClusters = clusters;
      nextBlk += clusters;
    }

    var totalBlocks = Math.Max(nextBlk, 64); // minimum image size
    var image = new byte[totalBlocks * BlockSize];

    // 1. Superblock dinode at block 2
    WriteSuperblock(image, totalBlocks);

    // 2. Global bitmap dinode at block 3
    WriteGlobalBitmapDinode(image);

    // 3. Bitmap data at block 4
    WriteBitmapData(image, (int)nextBlk);

    // 4. Root directory dinode at block 5 (inline dir referencing children)
    WriteDirDinode(image, root, RootDirBlkno, RootDirBlkno, 3);

    // 5. System directory dinode at block 6
    WriteSystemDirDinode(image);

    // 6. Inode alloc dinode at block 7
    WriteInodeAllocDinode(image);

    // 7. Subdirectory dinodes (inline dirs referencing their own children)
    foreach (var dir in dirs)
      WriteDirDinode(image, dir, dir.DinodeBlkno, dir.ParentBlkno, (uint)(dir.DinodeBlkno + 200));

    // 8. File dinodes and data
    foreach (var f in files) {
      WriteFileDinode(image, f.DinodeBlkno, f.DataBlkno, f.DataClusters, f.Data.Length);
      if (f.Data.Length > 0)
        Buffer.BlockCopy(f.Data, 0, image, (int)(f.DataBlkno * BlockSize), f.Data.Length);
    }

    return image;
  }

  /// <summary>Writes the image to a stream.</summary>
  public void WriteTo(Stream output) {
    var data = Build();
    output.Write(data, 0, data.Length);
  }

  /// <summary>Writes the common dinode header (signature + basic fields).</summary>
  private static void WriteDinodeHeader(byte[] image, long blkno, uint mode, uint flags, long size, ushort links, uint generation) {
    var off = (int)(blkno * BlockSize);

    // i_signature[8] at offset 0
    Signature.CopyTo(image.AsSpan(off, 6));

    // i_generation at offset 8
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x08, 4), generation);

    // i_suballoc_slot at offset 12 (i16): -1 for global
    BinaryPrimitives.WriteInt16LittleEndian(image.AsSpan(off + 0x0C, 2), -1);

    // i_suballoc_bit at offset 14 (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x0E, 2), (ushort)blkno);

    // i_links_count at offset 0x10 (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x10, 2), links);

    // i_uid at offset 0x14 (u32): 0
    // i_gid at offset 0x18 (u32): 0
    // i_size at offset 0x1C (u64)
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x1C, 8), (ulong)size);

    // i_mode at offset 0x24 (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x24, 2), (ushort)mode);

    // i_flags at offset 0x28 (u32)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x28, 4), flags);

    // i_blkno at offset 0x30 (u64) — this dinode's own block number
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x30, 8), (ulong)blkno);

    // i_clusters at offset 0x38 (u32)
    var clusters = size > 0 ? (uint)((size + ClusterSize - 1) / ClusterSize) : 0;
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x38, 4), clusters);

    // i_fs_generation at offset 0x40 (u32)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x40, 4), 1);
  }

  private void WriteSuperblock(byte[] image, long totalBlocks) {
    // Write dinode header for superblock
    WriteDinodeHeader(image, SuperBlockBlkno, 0, InodeValid | InodeSystemFile, 0, 0, 1);

    var off = (int)(SuperBlockBlkno * BlockSize) + Id2Offset;

    // ocfs2_super_block at id2 (+0xC0):
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x00, 2), 1);  // s_major_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x02, 2), 0);  // s_minor_rev_level
    // s_mnt_count at +0x04: 0
    // s_max_mnt_count at +0x06: 0
    // s_state at +0x08: 0 (clean)
    // s_errors at +0x0A: 0
    // s_checkinterval at +0x0C: 0
    // s_lastcheck at +0x10: 0
    // s_creator_os at +0x18: 0 (Linux)
    // s_feature_compat at +0x1C: 0

    // s_feature_incompat at +0x20: OCFS2_FEATURE_INCOMPAT_INLINE_DATA (0x0040)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x20, 4), 0x0040);

    // s_feature_ro_compat at +0x24: 0

    // s_root_blkno at +0x28 (u64)
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x28, 8), RootDirBlkno);

    // s_system_dir_blkno at +0x30 (u64)
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x30, 8), SystemDirBlkno);

    // s_blocksize_bits at +0x38 (u32)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x38, 4), BlockSizeBits);

    // s_clustersize_bits at +0x3C (u32)
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x3C, 4), ClusterSizeBits);

    // s_max_slots at +0x40 (u16)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0x40, 2), 1); // single-node

    // s_tunefs_flag at +0x42: 0
    // s_uuid_hash at +0x44: 0

    // s_first_cluster_group at +0x48 (u64): block of first cluster group descriptor
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 0x48, 8), (ulong)GlobalBitmapBlkno);

    // s_label[64] at +0x50
    Encoding.ASCII.GetBytes("OCFS2VOL").CopyTo(image.AsSpan(off + 0x50, 8));

    // s_uuid[16] at +0x90 — deterministic UUID
    for (var i = 0; i < 16; i++)
      image[off + 0x90 + i] = (byte)(0xA0 + i);

    // s_num_clusters at +0xA0 (u32) — total clusters in the filesystem
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xA0, 4), (uint)totalBlocks);
  }

  private static void WriteGlobalBitmapDinode(byte[] image) {
    WriteDinodeHeader(image, GlobalBitmapBlkno, 0, InodeValid | InodeSystemFile, BlockSize, 1, 2);

    // Extent record pointing at bitmap data block
    var off = (int)(GlobalBitmapBlkno * BlockSize) + Id2Offset;
    // ocfs2_extent_list header:
    //   l_tree_depth at +0: 0 (leaf)
    //   l_count at +2: 1
    //   l_next_free_rec at +4: 1
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0, 2), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 2, 2), 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 4, 2), 1);
    // extent record at +8:
    //   e_cpos (u32) at +8: 0
    //   e_int_clusters (u16) at +12: 1
    //   e_blkno (u64) at +16
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 8, 4), 0);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 12, 2), 1);
    // padding u16 at +14
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 16, 8), BitmapDataBlkno);
  }

  private static void WriteBitmapData(byte[] image, int usedBlocks) {
    var off = (int)(BitmapDataBlkno * BlockSize);
    // 1 bit per cluster. bit=1 means used. LSB-first bit ordering.
    for (var i = 0; i < usedBlocks && i < BlockSize * 8; i++) {
      var byteIdx = i / 8;
      var bitIdx = i % 8;
      image[off + byteIdx] |= (byte)(1 << bitIdx);
    }
  }

  // OCFS2 directory-entry file types.
  private const byte FtRegFile = 1;
  private const byte FtDir = 2;

  /// <summary>
  /// Writes an inline directory dinode for <paramref name="node"/> at
  /// <paramref name="blkno"/>. Emits "." / ".." plus a directory entry for each
  /// child (file_type 1 for regular files, 2 for subdirectories). The link count
  /// is 2 (for "." and the parent's reference) plus one per child directory,
  /// matching how each child's ".." back-reference inflates this directory's
  /// links.
  /// </summary>
  private void WriteDirDinode(byte[] image, TreeNode node, long blkno, long parentBlkno, uint generation) {
    var inlineData = BuildInlineDirEntries(node, blkno, parentBlkno);
    var childDirs = node.Order.Count(c => c.IsDir);
    var links = (ushort)(2 + childDirs);

    WriteDinodeHeader(image, blkno, ModeDir,
      InodeValid | InodeIsDir | InodeInlineData,
      inlineData.Length, links, generation);

    var dinodeOff = (int)(blkno * BlockSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dinodeOff + 0x4C, 2), DynInlineData);

    var off = dinodeOff + Id2Offset;

    // For inline directories, the id2 area starts with:
    //   id_count (u16) at +0: max inline data size
    //   id_data[0] at +2: the actual inline data
    var maxInline = BlockSize - Id2Offset - 2;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off, 2), (ushort)maxInline);

    if (inlineData.Length > maxInline)
      throw new InvalidOperationException(
        $"OCFS2 writer only supports inline directories; '{node.Name}' has too many entries to fit one block.");

    if (inlineData.Length > 0)
      Buffer.BlockCopy(inlineData, 0, image, off + 2, inlineData.Length);
  }

  /// <summary>
  /// Builds OCFS2 directory entries in the inline-data format for one directory.
  /// Each entry: u64 inode, u16 rec_len, u8 name_len, u8 file_type, name[].
  /// Starts with "." (self) and ".." (parent), then one entry per child.
  /// </summary>
  private static byte[] BuildInlineDirEntries(TreeNode node, long selfBlkno, long parentBlkno) {
    var entries = new List<byte[]> {
      BuildDirEntry((ulong)selfBlkno, ".", FtDir),
      BuildDirEntry((ulong)parentBlkno, "..", FtDir),
    };

    foreach (var child in node.Order)
      entries.Add(BuildDirEntry((ulong)child.DinodeBlkno, child.Name, child.IsDir ? FtDir : FtRegFile));

    var totalLen = entries.Sum(e => e.Length);
    var result = new byte[totalLen];
    var pos = 0;
    foreach (var e in entries) {
      Buffer.BlockCopy(e, 0, result, pos, e.Length);
      pos += e.Length;
    }
    return result;
  }

  private static byte[] BuildDirEntry(ulong inode, string name, byte fileType) {
    var nameBytes = Encoding.UTF8.GetBytes(name);
    var recLen = 8 + 2 + 1 + 1 + nameBytes.Length; // inode(8) + rec_len(2) + name_len(1) + file_type(1) + name
    // Align to 4 bytes
    if ((recLen & 3) != 0) recLen = (recLen + 3) & ~3;

    var entry = new byte[recLen];
    BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(0, 8), inode);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8, 2), (ushort)recLen);
    entry[10] = (byte)nameBytes.Length;
    entry[11] = fileType;
    nameBytes.CopyTo(entry.AsSpan(12, nameBytes.Length));
    return entry;
  }

  private static void WriteSystemDirDinode(byte[] image) {
    // System dir: inline directory with entries for global_bitmap and inode_alloc
    WriteDinodeHeader(image, SystemDirBlkno, ModeDir,
      InodeValid | InodeIsDir | InodeSystemFile | InodeInlineData,
      0, 2, 4);

    var dinodeOff = (int)(SystemDirBlkno * BlockSize);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(dinodeOff + 0x4C, 2), DynInlineData);

    var off = dinodeOff + Id2Offset;
    var maxInline = BlockSize - Id2Offset - 2;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off, 2), (ushort)maxInline);

    // Build system dir entries
    var entries = new List<byte[]> {
      BuildDirEntry(SystemDirBlkno, ".", 2),
      BuildDirEntry(RootDirBlkno, "..", 2),
      BuildDirEntry(GlobalBitmapBlkno, "global_bitmap", 1),
      BuildDirEntry(InodeAllocBlkno, "global_inode_alloc", 1),
    };

    var totalLen = entries.Sum(e => e.Length);
    var data = new byte[totalLen];
    var pos = 0;
    foreach (var e in entries) {
      Buffer.BlockCopy(e, 0, data, pos, e.Length);
      pos += e.Length;
    }

    if (data.Length <= maxInline)
      Buffer.BlockCopy(data, 0, image, off + 2, data.Length);

    // Update size in dinode header
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(dinodeOff + 0x1C, 8), (ulong)data.Length);
  }

  private static void WriteInodeAllocDinode(byte[] image) {
    WriteDinodeHeader(image, InodeAllocBlkno, 0, InodeValid | InodeSystemFile, 0, 1, 5);
  }

  private static void WriteFileDinode(byte[] image, long blkno, long dataBlkno, int dataClusters, int fileSize) {
    var flags = InodeValid;
    WriteDinodeHeader(image, blkno, ModeFile, flags, fileSize, 1, (uint)(blkno + 100));

    if (fileSize == 0) return;

    // Write extent list at id2
    var off = (int)(blkno * BlockSize) + Id2Offset;
    // ocfs2_extent_list:
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 0, 2), 0);            // l_tree_depth
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 2, 2), 1);            // l_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 4, 2), 1);            // l_next_free_rec
    // extent record at +8:
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 8, 4), 0);            // e_cpos
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(off + 12, 2), (ushort)dataClusters); // e_int_clusters (low 16)
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(off + 16, 8), (ulong)dataBlkno);   // e_blkno
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// Writes a minimal UFS1 (FreeBSD/BSD FFS) filesystem image with a single cylinder
/// group, a flat root directory, and direct-block-only file extents.
/// <para>
/// All on-disk structures (superblock <c>fs</c>, cylinder-group header <c>cg</c>,
/// and <c>ufs1_dinode</c>) use the exact field offsets defined in FreeBSD's
/// <c>sys/ufs/ffs/fs.h</c> and <c>sys/ufs/ufs/dinode.h</c>. The superblock is 1376
/// bytes; <c>fs_magic = 0x00011954</c> sits at its last 4 bytes (offset 1372 within
/// the superblock, i.e. image offset 9564). Cylinder-group <c>cg_magic = 0x00090255</c>
/// sits at offset 4 of the cg structure (image-relative offset <c>cgstart + 4</c>).
/// Free-block / free-inode bitmaps, fragment-summary array, and the summary-info
/// block (<c>fs_csp</c>) are populated so that <c>fsck_ffs -n</c>-grade structural
/// invariants hold.
/// </para>
/// </summary>
public sealed class UfsWriter {
  // ── on-disk geometry ──────────────────────────────────────────────────────
  internal const int SuperblockOffset = 8192;     // SBLOCK_UFS1
  internal const int SuperblockSize = 1376;       // sizeof(struct fs)
  internal const int BlockSize = 8192;            // fs_bsize
  internal const int FragSize = 1024;             // fs_fsize
  internal const int Frag = BlockSize / FragSize; // fs_frag = 8
  internal const int Ufs1Magic = 0x00011954;      // FS_UFS1_MAGIC
  internal const int CgMagic = 0x00090255;        // CG_MAGIC
  internal const int InodeSize = 128;             // sizeof(ufs1_dinode)
  internal const int InodesPerGroup = 2048;       // fs_ipg
  internal const int RootIno = 2;
  internal const int MaxDirectBlocks = 12;        // UFS_NDADDR

  // Layout inside CG 0:
  //   sb at frag 8 (byte 8192), cg header at frag 16 (byte 16384),
  //   inode table at frag 24; data starts after inode table.
  internal const int SblkNo = 8;
  internal const int CblkNo = 16;
  internal const int IblkNo = 24;
  internal const int InodeTableFrags = (InodesPerGroup * InodeSize + FragSize - 1) / FragSize;
  internal const int DblkNo = IblkNo + InodeTableFrags;    // = 280 frags
  internal const int FragsPerGroup = 16384;                // fs_fpg (16 MB / 1 KB)
  internal const int MinImageBytes = 16 * 1024 * 1024;     // 16 MB floor

  internal static readonly int FsMagicOffset = SuperblockSize - 4; // 1372
  internal static readonly int FsCsAddrBlock = DblkNo;             // first data block holds fs_cs summary

  private readonly List<(string Name, byte[] Data)> _files = [];
  private readonly byte[] _volumeUuid = Guid.NewGuid().ToByteArray();

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data));
  }

  // ── in-memory directory tree ───────────────────────────────────────────
  // A node is either the root, an intermediate directory, or a regular file.
  // Directories are laid out as a single 8-KB block of UFS dir entries; files
  // occupy direct-block extents.
  private sealed class TreeNode {
    public string Name = "";              // leaf name (no path separators)
    public bool IsDirectory;
    public byte[] Data = [];              // file payload (empty for directories)
    public int Inode;                     // assigned inode number
    public TreeNode? Parent;
    // Children keyed by leaf name; insertion order preserved for stable layout.
    public readonly Dictionary<string, TreeNode> Children = new(StringComparer.Ordinal);
    public readonly List<TreeNode> Order = [];
  }

  // Builds the directory tree from the flat list of '/'-separated paths,
  // creating intermediate directory nodes on demand and sharing them between
  // siblings.
  private TreeNode BuildTree() {
    var root = new TreeNode { IsDirectory = true, Name = "" };
    foreach (var (rawName, data) in _files) {
      var parts = rawName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var cursor = root;
      for (var i = 0; i < parts.Length; i++) {
        var part = parts[i];
        var isLeaf = i == parts.Length - 1;
        if (cursor.Children.TryGetValue(part, out var existing)) {
          if (isLeaf && !existing.IsDirectory)
            existing.Data = data; // duplicate file path → last write wins
          cursor = existing;
          continue;
        }

        var node = new TreeNode {
          Name = part,
          IsDirectory = !isLeaf,
          Data = isLeaf ? data : [],
          Parent = cursor,
        };
        cursor.Children[part] = node;
        cursor.Order.Add(node);
        cursor = node;
      }
    }
    return root;
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);

    var root = BuildTree();
    root.Inode = RootIno;

    // ── inode assignment (root = 2, then every directory and file) ───────
    var directories = new List<TreeNode> { root };
    var regularFiles = new List<TreeNode>();
    var nextIno = RootIno + 1;
    var queue = new Queue<TreeNode>();
    queue.Enqueue(root);
    while (queue.Count > 0) {
      var dir = queue.Dequeue();
      foreach (var child in dir.Order) {
        child.Inode = nextIno++;
        if (child.IsDirectory) {
          directories.Add(child);
          queue.Enqueue(child);
        } else {
          regularFiles.Add(child);
        }
      }
    }

    // Each directory must fit in a single 8-KB block (this minimal writer does
    // not allocate additional directory blocks or indirect blocks).
    foreach (var dir in directories)
      EnsureDirectoryFitsOneBlock(dir);

    // Files are limited to direct blocks (no indirect-block support).
    foreach (var file in regularFiles) {
      var blocks = (file.Data.Length + BlockSize - 1) / BlockSize;
      if (blocks > MaxDirectBlocks)
        throw new InvalidOperationException(
          $"UFS writer only supports direct blocks (max {MaxDirectBlocks * BlockSize} bytes per file); " +
          $"'{file.Name}' needs {file.Data.Length} bytes.");
    }

    // ── layout: one block per directory, then file data ──────────────────
    var currentFrag = DblkNo + Frag;               // skip 1 block for fs_cs summary
    var dirFrag = new Dictionary<TreeNode, int>();
    foreach (var dir in directories) {
      dirFrag[dir] = currentFrag;
      currentFrag += Frag;                         // one 8-KB block per directory
    }

    var fileFirstFrag = new Dictionary<TreeNode, int>();
    var fileFrags = new Dictionary<TreeNode, int>();
    foreach (var file in regularFiles) {
      fileFirstFrag[file] = currentFrag;
      var frags = Math.Max(1, (file.Data.Length + FragSize - 1) / FragSize);
      fileFrags[file] = frags;
      currentFrag += frags;
    }

    // Round used-frag count up to a whole block to keep the bitmap simple.
    var usedFrags = ((currentFrag + Frag - 1) / Frag) * Frag;
    var totalFrags = Math.Max(FragsPerGroup, usedFrags + Frag);
    var imageBytes = Math.Max(MinImageBytes, totalFrags * FragSize);
    totalFrags = imageBytes / FragSize;
    var disk = new byte[imageBytes];

    var inodeTableOffset = IblkNo * FragSize;
    // Accounting matches the historical flat-layout convention: the two reserved
    // inodes (0,1) plus every allocated inode (root + subdirectories + files).
    // nextIno is the first unused inode number, so nextIno-1 counts inodes
    // 1..nextIno-1, i.e. one reserved slot + the live tree.
    var usedInodes = nextIno - 1;

    // ── directory blocks + directory inodes ──────────────────────────────
    foreach (var dir in directories) {
      var parentIno = dir.Parent?.Inode ?? RootIno;     // root's ".." points at itself
      var block = BuildDirectoryBlock(dir, parentIno);
      block.CopyTo(disk, (long)dirFrag[dir] * FragSize);

      // di_nlink for a directory = 2 ("." + entry in parent) + one extra link
      // per child subdirectory (each child's ".." points back here).
      var childDirs = dir.Order.Count(c => c.IsDirectory);
      WriteUfs1Inode(disk, inodeTableOffset + dir.Inode * InodeSize,
        mode: 0x41ED, nlink: (ushort)(2 + childDirs), size: BlockSize,
        blocksUsed512: (uint)(Frag * FragSize / 512),
        directBlocks: [dirFrag[dir]]);
    }

    // ── file blocks + file inodes ─────────────────────────────────────────
    foreach (var file in regularFiles) {
      var data = file.Data;
      var first = fileFirstFrag[file];
      var dblks = new int[MaxDirectBlocks];
      // One direct-block pointer per 8-KB block; each block is Frag frags.
      var blocks = Math.Max(1, (data.Length + BlockSize - 1) / BlockSize);
      for (var b = 0; b < blocks && b < MaxDirectBlocks; b++) dblks[b] = first + b * Frag;
      WriteUfs1Inode(disk, inodeTableOffset + file.Inode * InodeSize,
        mode: 0x81A4, nlink: 1, size: (ulong)data.Length,
        blocksUsed512: (uint)(fileFrags[file] * FragSize / 512),
        directBlocks: dblks);
      if (data.Length > 0) data.CopyTo(disk, (long)first * FragSize);
    }

    // ── cylinder group header + bitmaps ─────────────────────────────────
    var usedFragsTotal = currentFrag;
    var dirCount = directories.Count;
    WriteCylinderGroup(disk, usedInodes, usedFragsTotal, totalFrags, dirCount);

    // ── fs_cs summary block (first data block, referenced by fs_csaddr) ──
    var csOffset = (long)FsCsAddrBlock * FragSize;
    BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan((int)csOffset + 0), dirCount);                   // cs_ndir
    BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan((int)csOffset + 4), (totalFrags - usedFragsTotal) / Frag); // cs_nbfree
    BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan((int)csOffset + 8), InodesPerGroup - usedInodes); // cs_nifree
    BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan((int)csOffset + 12), 0);                         // cs_nffree

    // ── superblock ──────────────────────────────────────────────────────
    WriteSuperblock(disk, totalFrags, usedInodes, usedFragsTotal, dirCount);

    output.Write(disk);
  }

  // Lays out a directory's data block: ".", "..", then one entry per child.
  private static byte[] BuildDirectoryBlock(TreeNode dir, int parentIno) {
    var block = new byte[BlockSize];
    var dirPos = 0;
    WriteDirEntry(block, ref dirPos, dir.Inode, ".", 4);     // DT_DIR = 4
    WriteDirEntry(block, ref dirPos, parentIno, "..", 4);
    foreach (var child in dir.Order)
      WriteDirEntry(block, ref dirPos, child.Inode, child.Name, child.IsDirectory ? (byte)4 : (byte)8); // DT_DIR / DT_REG
    if (dirPos < BlockSize) {
      // Extend the last entry's reclen to cover the rest of the block (UFS convention).
      var lastEntryStart = FindLastEntryStart(block, dirPos);
      var newLastReclen = BlockSize - lastEntryStart;
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(lastEntryStart + 4), (ushort)newLastReclen);
    }
    return block;
  }

  // Verifies the directory's entries (".", "..", children) fit one 8-KB block.
  private static void EnsureDirectoryFitsOneBlock(TreeNode dir) {
    var used = DirEntryReclen(".") + DirEntryReclen("..");
    foreach (var child in dir.Order) used += DirEntryReclen(child.Name);
    if (used > BlockSize)
      throw new InvalidOperationException(
        $"UFS writer keeps each directory in a single {BlockSize}-byte block; " +
        $"directory '{(dir.Name.Length == 0 ? "/" : dir.Name)}' with {dir.Order.Count} entries does not fit.");
  }

  private static int DirEntryReclen(string name) {
    var nameLen = Encoding.ASCII.GetByteCount(name);
    return (8 + nameLen + 3) & ~3;
  }

  // ── struct fs (superblock) ────────────────────────────────────────────
  private void WriteSuperblock(byte[] disk, int totalFrags, int usedInodes, int usedFragsTotal, int dirCount) {
    var sb = disk.AsSpan(SuperblockOffset, SuperblockSize);
    sb.Clear();

    BinaryPrimitives.WriteInt32LittleEndian(sb[8..], SblkNo);
    BinaryPrimitives.WriteInt32LittleEndian(sb[12..], CblkNo);
    BinaryPrimitives.WriteInt32LittleEndian(sb[16..], IblkNo);
    BinaryPrimitives.WriteInt32LittleEndian(sb[20..], DblkNo);
    BinaryPrimitives.WriteInt32LittleEndian(sb[24..], 0);                 // fs_old_cgoffset
    BinaryPrimitives.WriteInt32LittleEndian(sb[28..], -1);                // fs_old_cgmask
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // fs_old_time
    BinaryPrimitives.WriteInt32LittleEndian(sb[36..], totalFrags / Frag); // fs_old_size
    BinaryPrimitives.WriteInt32LittleEndian(sb[40..], (totalFrags - DblkNo) / Frag); // fs_old_dsize
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], 1);                // fs_ncg
    BinaryPrimitives.WriteInt32LittleEndian(sb[48..], BlockSize);         // fs_bsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[52..], FragSize);          // fs_fsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[56..], Frag);              // fs_frag
    BinaryPrimitives.WriteInt32LittleEndian(sb[60..], 8);                 // fs_minfree
    BinaryPrimitives.WriteInt32LittleEndian(sb[72..], ~(BlockSize - 1));  // fs_bmask
    BinaryPrimitives.WriteInt32LittleEndian(sb[76..], ~(FragSize - 1));   // fs_fmask
    BinaryPrimitives.WriteInt32LittleEndian(sb[80..], 13);                // fs_bshift
    BinaryPrimitives.WriteInt32LittleEndian(sb[84..], 10);                // fs_fshift
    BinaryPrimitives.WriteInt32LittleEndian(sb[88..], 16);                // fs_maxcontig
    BinaryPrimitives.WriteInt32LittleEndian(sb[92..], 2048);              // fs_maxbpg
    BinaryPrimitives.WriteInt32LittleEndian(sb[96..], 3);                 // fs_fragshift
    BinaryPrimitives.WriteInt32LittleEndian(sb[100..], 1);                // fs_fsbtodb
    BinaryPrimitives.WriteInt32LittleEndian(sb[104..], SuperblockSize);   // fs_sbsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[116..], BlockSize / 4);    // fs_nindir
    BinaryPrimitives.WriteUInt32LittleEndian(sb[120..], (uint)(BlockSize / InodeSize)); // fs_inopb
    BinaryPrimitives.WriteInt32LittleEndian(sb[124..], 2);                // fs_old_nspf
    BinaryPrimitives.WriteInt32LittleEndian(sb[128..], 0);                // fs_optim
    BinaryPrimitives.WriteInt32LittleEndian(sb[152..], FsCsAddrBlock);    // fs_old_csaddr
    BinaryPrimitives.WriteInt32LittleEndian(sb[156..], FragSize);         // fs_cssize
    BinaryPrimitives.WriteInt32LittleEndian(sb[160..], FragSize);         // fs_cgsize
    BinaryPrimitives.WriteUInt32LittleEndian(sb[184..], InodesPerGroup);  // fs_ipg
    BinaryPrimitives.WriteInt32LittleEndian(sb[188..], FragsPerGroup);    // fs_fpg
    // fs_old_cstotal at 192..207
    BinaryPrimitives.WriteInt32LittleEndian(sb[192..], dirCount);
    BinaryPrimitives.WriteInt32LittleEndian(sb[196..], (totalFrags - usedFragsTotal) / Frag);
    BinaryPrimitives.WriteInt32LittleEndian(sb[200..], InodesPerGroup - usedInodes);
    BinaryPrimitives.WriteInt32LittleEndian(sb[204..], 0);
    sb[209] = 1;                                                           // fs_clean
    // fs_fsmnt at 212
    var mntName = Encoding.ASCII.GetBytes("/ufs-workbench");
    mntName.AsSpan(0, Math.Min(mntName.Length, 468)).CopyTo(sb[212..]);
    var vol = Encoding.ASCII.GetBytes("ufs-workbench");
    vol.AsSpan(0, Math.Min(vol.Length, 32)).CopyTo(sb[680..]);             // fs_volname
    BinaryPrimitives.WriteInt32LittleEndian(sb[860..], BlockSize);         // fs_maxbsize
    BinaryPrimitives.WriteInt64LittleEndian(sb[872..], (long)totalFrags * FragSize / 512); // fs_providersize
    _volumeUuid.CopyTo(sb[896..]);                                         // (fs_sparecon64[0..1] → UUID 16B)
    BinaryPrimitives.WriteInt64LittleEndian(sb[992..], SuperblockOffset);  // fs_sblockactualloc
    BinaryPrimitives.WriteInt64LittleEndian(sb[1000..], SuperblockOffset); // fs_sblockloc
    // fs_cstotal (csum_total, 8 int64s) at 1008
    BinaryPrimitives.WriteInt64LittleEndian(sb[1008..], dirCount);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1016..], (totalFrags - usedFragsTotal) / Frag);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1024..], InodesPerGroup - usedInodes);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1032..], 0);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1072..], DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // fs_time
    BinaryPrimitives.WriteInt64LittleEndian(sb[1080..], totalFrags / Frag);                         // fs_size
    BinaryPrimitives.WriteInt64LittleEndian(sb[1088..], (totalFrags - DblkNo) / Frag);              // fs_dsize
    BinaryPrimitives.WriteInt64LittleEndian(sb[1096..], FsCsAddrBlock);                             // fs_csaddr
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1196..], 16384);                                    // fs_avgfilesize
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1200..], 64);                                       // fs_avgfpdir
    BinaryPrimitives.WriteInt64LittleEndian(sb[1208..], DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // fs_mtime
    BinaryPrimitives.WriteInt32LittleEndian(sb[1320..], 60);                                        // fs_maxsymlinklen
    BinaryPrimitives.WriteInt32LittleEndian(sb[1324..], 2);                                         // fs_old_inodefmt
    BinaryPrimitives.WriteUInt64LittleEndian(sb[1328..], 1UL << 42);                                // fs_maxfilesize (cap)
    BinaryPrimitives.WriteInt64LittleEndian(sb[1336..], ~(long)(BlockSize - 1));                    // fs_qbmask
    BinaryPrimitives.WriteInt64LittleEndian(sb[1344..], ~(long)(FragSize - 1));                     // fs_qfmask
    // fs_magic — canary at sb[1372]
    BinaryPrimitives.WriteInt32LittleEndian(sb[FsMagicOffset..], Ufs1Magic);
  }

  // ── struct cg (cylinder-group header) ─────────────────────────────────
  private static void WriteCylinderGroup(byte[] disk, int usedInodes, int usedFragsTotal, int totalFrags, int dirCount) {
    var cgOffset = CblkNo * FragSize;
    var cg = disk.AsSpan(cgOffset, BlockSize);
    cg.Clear();

    BinaryPrimitives.WriteInt32LittleEndian(cg[4..], CgMagic);             // cg_magic
    BinaryPrimitives.WriteUInt32LittleEndian(cg[8..], (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // cg_old_time
    BinaryPrimitives.WriteUInt32LittleEndian(cg[12..], 0);                 // cg_cgx
    BinaryPrimitives.WriteInt16LittleEndian(cg[16..], 0);                  // cg_old_ncyl
    BinaryPrimitives.WriteInt16LittleEndian(cg[18..], 0);                  // cg_old_niblk
    BinaryPrimitives.WriteUInt32LittleEndian(cg[20..], (uint)Math.Min(FragsPerGroup, totalFrags)); // cg_ndblk
    // cg_cs (csum) @ 24
    var freeBlocks = (Math.Min(FragsPerGroup, totalFrags) - usedFragsTotal) / Frag;
    BinaryPrimitives.WriteInt32LittleEndian(cg[24..], dirCount);
    BinaryPrimitives.WriteInt32LittleEndian(cg[28..], freeBlocks);
    BinaryPrimitives.WriteInt32LittleEndian(cg[32..], InodesPerGroup - usedInodes);
    BinaryPrimitives.WriteInt32LittleEndian(cg[36..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(cg[40..], 0);                 // cg_rotor
    BinaryPrimitives.WriteUInt32LittleEndian(cg[44..], 0);                 // cg_frotor
    BinaryPrimitives.WriteUInt32LittleEndian(cg[48..], (uint)usedInodes);  // cg_irotor
    BinaryPrimitives.WriteInt32LittleEndian(cg[84..], 0);                  // cg_old_btotoff
    BinaryPrimitives.WriteInt32LittleEndian(cg[88..], 0);                  // cg_old_boff

    const int CgHeaderSize = 184;
    var iusedOff = CgHeaderSize;
    var iusedSize = (InodesPerGroup + 7) / 8;
    var freeOff = iusedOff + iusedSize;
    var freeSize = (FragsPerGroup + 7) / 8;
    var clusterSumOff = freeOff + freeSize;
    var clusterSumSize = 32;
    var clusterOff = clusterSumOff + clusterSumSize;
    var clusterSize = (FragsPerGroup / Frag + 7) / 8;

    BinaryPrimitives.WriteUInt32LittleEndian(cg[92..], (uint)iusedOff);    // cg_iusedoff
    BinaryPrimitives.WriteUInt32LittleEndian(cg[96..], (uint)freeOff);     // cg_freeoff
    BinaryPrimitives.WriteUInt32LittleEndian(cg[100..], (uint)(clusterOff + clusterSize)); // cg_nextfreeoff
    BinaryPrimitives.WriteUInt32LittleEndian(cg[104..], (uint)clusterSumOff); // cg_clustersumoff
    BinaryPrimitives.WriteUInt32LittleEndian(cg[108..], (uint)clusterOff); // cg_clusteroff
    BinaryPrimitives.WriteUInt32LittleEndian(cg[112..], (uint)(FragsPerGroup / Frag)); // cg_nclusterblks
    BinaryPrimitives.WriteUInt32LittleEndian(cg[116..], InodesPerGroup);   // cg_niblk
    BinaryPrimitives.WriteUInt32LittleEndian(cg[120..], InodesPerGroup);   // cg_initediblk
    BinaryPrimitives.WriteUInt32LittleEndian(cg[124..], 0);                // cg_unrefs
    BinaryPrimitives.WriteInt64LittleEndian(cg[136..], DateTimeOffset.UtcNow.ToUnixTimeSeconds()); // cg_time

    // inode-used bitmap
    for (var ino = 0; ino < usedInodes; ino++)
      cg[iusedOff + ino / 8] |= (byte)(1 << (ino % 8));

    // free-frag bitmap (1 = free)
    for (var i = 0; i < freeSize; i++) cg[freeOff + i] = 0xFF;
    for (var f = 0; f < usedFragsTotal && f < FragsPerGroup; f++)
      cg[freeOff + f / 8] &= (byte)~(1 << (f % 8));
    var cgFrags = Math.Min(FragsPerGroup, totalFrags);
    for (var f = cgFrags; f < FragsPerGroup; f++)
      cg[freeOff + f / 8] &= (byte)~(1 << (f % 8));

    // free-cluster bitmap
    var usedBlocks = (usedFragsTotal + Frag - 1) / Frag;
    var cgBlocks = cgFrags / Frag;
    for (var b = usedBlocks; b < cgBlocks; b++)
      cg[clusterOff + b / 8] |= (byte)(1 << (b % 8));
  }

  // ── ufs1_dinode (128 bytes) ───────────────────────────────────────────
  private static void WriteUfs1Inode(
    byte[] disk, long inodeByteOffset,
    uint mode, ushort nlink, ulong size, uint blocksUsed512,
    ReadOnlySpan<int> directBlocks
  ) {
    var di = disk.AsSpan((int)inodeByteOffset, InodeSize);
    di.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(di[0..], (ushort)mode);         // di_mode
    BinaryPrimitives.WriteUInt16LittleEndian(di[2..], nlink);                // di_nlink
    BinaryPrimitives.WriteUInt32LittleEndian(di[4..], 0);                    // di_freelink/dirdepth
    BinaryPrimitives.WriteUInt64LittleEndian(di[8..], size);                 // di_size
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt32LittleEndian(di[16..], now);                 // di_atime
    BinaryPrimitives.WriteUInt32LittleEndian(di[24..], now);                 // di_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(di[32..], now);                 // di_ctime
    for (var i = 0; i < MaxDirectBlocks && i < directBlocks.Length; i++)
      BinaryPrimitives.WriteInt32LittleEndian(di[(40 + i * 4)..], directBlocks[i]);
    BinaryPrimitives.WriteUInt32LittleEndian(di[104..], blocksUsed512);      // di_blocks
    BinaryPrimitives.WriteUInt32LittleEndian(di[108..], 1);                  // di_gen
    BinaryPrimitives.WriteUInt32LittleEndian(di[112..], 0);                  // di_uid
    BinaryPrimitives.WriteUInt32LittleEndian(di[116..], 0);                  // di_gid
    BinaryPrimitives.WriteUInt64LittleEndian(di[120..], 0);                  // di_modrev
  }

  // ── directory entry ───────────────────────────────────────────────────
  private static void WriteDirEntry(byte[] block, ref int pos, int ino, string name, byte dtype) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var reclen = (8 + nameBytes.Length + 3) & ~3;
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(pos), (uint)ino);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(pos + 4), (ushort)reclen);
    block[pos + 6] = dtype;
    block[pos + 7] = (byte)nameBytes.Length;
    nameBytes.CopyTo(block, pos + 8);
    pos += reclen;
  }

  private static int FindLastEntryStart(byte[] block, int consumedLen) {
    var p = 0;
    var last = 0;
    while (p < consumedLen) {
      var rl = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(p + 4));
      if (rl < 8 || rl > consumedLen - p) break;
      last = p;
      p += rl;
    }
    return last;
  }
}

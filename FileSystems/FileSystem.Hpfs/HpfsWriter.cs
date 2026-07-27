#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hpfs;

/// <summary>
/// Builds a minimal HPFS (OS/2 High Performance File System) image from scratch.
/// Layout:
///   LBA  0:       Boot sector (BPB + OEM ID)
///   LBA 16:       Superblock (8-byte magic + root fnode LBA + total sectors + bitmap start)
///   LBA 17:       Spare block (8-byte magic, minimal)
///   LBA 18:       Root fnode (magic + direct alloc pointing to root dir block)
///   LBA 20..23:   Root directory block (2048 bytes = 4 LBAs, with dir entries)
///   LBA 24:       Bitmap band 0 (allocation bitmap for the whole volume)
///   LBA 32+:      Per-directory fnodes + dir blocks, then file fnodes and data.
///
/// Directories are honoured: a name passed to <see cref="AddFile"/> may contain
/// '/' (or '\') separators; each path segment becomes a real HPFS directory
/// (an fnode with the directory flag, referenced by a directory-flagged dirent
/// in the parent's dirent block, with its own dirent block).
///
/// A directory whose children overflow one 2 KiB dirent block spills into
/// additional leaf dirent blocks organised as a 2-level dirent B-tree: the
/// directory's root block holds separator dirents whose down-pointers reference
/// the leaf blocks. With short names this scales to well over a thousand entries
/// per directory (the 2-level root block holds roughly 40 separators, i.e. ~40
/// leaves of ~45 entries each). Other limitations remain: direct file allocation
/// only (no AllocSec B-tree), and a single bitmap band.
/// </summary>
internal sealed class HpfsWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  internal const int LbaSize = 512;
  internal const int DirBlockLbas = 4; // 2048 bytes per dir block
  internal const int DirBlockSize = LbaSize * DirBlockLbas;

  // Dirent flag bits.
  private const ushort DirentFlagSpecial = 0x0001; // end-of-block sentinel / ".."
  private const ushort DirentFlagBtreeDown = 0x0004; // record carries a 4-byte B-tree down-pointer at its tail
  private const ushort DirentFlagDirectory = 0x0008;

  // Dirents begin at this offset into a 2 KiB dirent block.
  private const int DirentAreaOffset = 0x14;
  // Minimum dirent record length (the fixed header before the name).
  private const int DirentHeaderLen = 32;

  // Fixed layout LBAs
  private const uint BootLba = 0;
  private const uint SuperblockLba = 16;
  private const uint SpareBlockLba = 17;
  private const uint RootFnodeLba = 18;
  private const uint RootDirLba = 20; // 4 LBAs = 2048 bytes
  private const uint BitmapLba = 24;  // 1 LBA for allocation bitmap
  private const uint DirBandBitmapLba = 25; // 1 LBA for the directory-band bitmap
  private const uint FirstAllocLba = 32;

  // The root dirent block doubles as the whole directory band. HPFS measures the
  // band in sectors and requires a 4-sector (one dnode) granularity, which
  // RootDirLba/DirBlockLbas already satisfy.
  private const uint DirBandStartLba = RootDirLba;
  private const uint DirBandSectors = DirBlockLbas;

  // Magics
  // HPFS stores each magic as a little-endian uint32, so the on-disk byte order is
  // the reverse of the constant as written in the OS/2 / Linux headers:
  //   superblock 0xF995E849, 0xFA53E9C5   spareblock 0xF9911849, 0xFA5229C5
  //   fnode      0xF7E40AAE               dirblock   0x77E40AAE
  private static readonly byte[] SuperblockMagic = [0x49, 0xE8, 0x95, 0xF9, 0xC5, 0xE9, 0x53, 0xFA];
  private static readonly byte[] SpareBlockMagic = [0x49, 0x18, 0x91, 0xF9, 0xC5, 0x29, 0x52, 0xFA];
  private static readonly byte[] FnodeMagic = [0xAE, 0x0A, 0xE4, 0xF7];
  private static readonly byte[] DirBlockMagic = [0xAE, 0x0A, 0xE4, 0x77];

  /// <summary>A node in the directory tree assembled before layout.</summary>
  private sealed class TreeNode {
    public required string Name;
    public bool IsDirectory;
    public byte[] Data = [];

    // Children of a directory, keyed by lower-cased segment name (HPFS is
    // case-insensitive but case-preserving; dirents are sorted by name).
    public readonly SortedDictionary<string, TreeNode> Children =
      new(StringComparer.OrdinalIgnoreCase);

    // Filled in during the layout pass.
    public uint FnodeLba;       // fnode for this entry (file or directory)
    public uint DirBlockLba;    // directory's own (root) dirent block (directories only)
    public uint DataLba;        // first data LBA (files only)
    public uint DataLenLbas;    // data length in LBAs (files only)

    // Extra leaf dirent blocks when the children overflow the root dirent block.
    // The root block then holds B-tree separator dirents whose down-pointers
    // reference these leaves (directories only).
    public readonly List<uint> LeafBlockLbas = [];
  }

  /// <summary>
  /// Adds a file to the image. The name may contain '/' or '\' separators; each
  /// segment becomes a real HPFS directory and the file lands at the nested path.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (string.IsNullOrEmpty(Path.GetFileName(name.Replace('\\', '/'))))
      throw new ArgumentException("File name must not be empty.", nameof(name));
    _files.Add((name, data));
  }

  /// <summary>Builds the HPFS image and returns the raw bytes.</summary>
  public byte[] Build() {
    var root = BuildTree();

    // Layout pass: assign LBAs depth-first. For each directory we reserve an
    // fnode (1 LBA) and a dirent block (DirBlockLbas). For each file we reserve
    // an fnode (1 LBA) and its data (rounded up to whole LBAs). The root's fnode
    // and dirent block sit at their fixed LBAs; everything else flows from
    // FirstAllocLba.
    var nextLba = FirstAllocLba;
    root.FnodeLba = RootFnodeLba;
    root.DirBlockLba = RootDirLba;
    // Root's first dirent block is the fixed RootDirLba; overflow leaves come
    // from the free pool (the reader chains them via B-tree down-pointers).
    var rootExtraLeaves = CountOverflowLeaves(root);
    for (var i = 0; i < rootExtraLeaves; i++) {
      root.LeafBlockLbas.Add(nextLba);
      nextLba += DirBlockLbas;
    }
    AssignLbas(root, ref nextLba, isRoot: true);

    var totalLbas = Math.Max(nextLba, 128u); // minimum 64 KB image
    var image = new byte[(long)totalLbas * LbaSize];

    WriteBootSector(image);
    WriteSuperblock(image, totalLbas);
    WriteSpareBlock(image);
    WriteBitmap(image, nextLba);
    WriteDirBandBitmap(image);

    // Emit the whole tree (fnodes, dir blocks, file data).
    WriteNode(image, root, parentFnodeLba: RootFnodeLba);

    return image;
  }

  /// <summary>Writes the image to a stream.</summary>
  public void WriteTo(Stream output) {
    var data = Build();
    output.Write(data, 0, data.Length);
  }

  /// <summary>Assembles the flat file list into a directory tree.</summary>
  private TreeNode BuildTree() {
    var root = new TreeNode { Name = "", IsDirectory = true };

    foreach (var (rawName, data) in _files) {
      var normalized = rawName.Replace('\\', '/').Trim('/');
      var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (segments.Length == 0) continue;

      var cursor = root;
      for (var i = 0; i < segments.Length - 1; i++) {
        var seg = segments[i];
        if (!cursor.Children.TryGetValue(seg, out var child)) {
          child = new TreeNode { Name = seg, IsDirectory = true };
          cursor.Children[seg] = child;
        }
        child.IsDirectory = true;
        cursor = child;
      }

      var leaf = segments[^1];
      // Last writer wins on a name clash; ignore a file colliding with a dir.
      cursor.Children[leaf] = new TreeNode { Name = leaf, IsDirectory = false, Data = data };
    }

    return root;
  }

  /// <summary>Depth-first LBA assignment for the whole tree.</summary>
  private void AssignLbas(TreeNode node, ref uint nextLba, bool isRoot) {
    foreach (var child in node.Children.Values) {
      child.FnodeLba = nextLba++;
      if (child.IsDirectory) {
        child.DirBlockLba = nextLba;
        nextLba += DirBlockLbas;
        // If the child's own children overflow one dirent block, reserve extra
        // leaf dirent blocks so the directory becomes a 2-level B-tree.
        var extraLeaves = CountOverflowLeaves(child);
        for (var i = 0; i < extraLeaves; i++) {
          child.LeafBlockLbas.Add(nextLba);
          nextLba += DirBlockLbas;
        }
        AssignLbas(child, ref nextLba, isRoot: false);
      } else {
        var dataLbas = (uint)((child.Data.Length + LbaSize - 1) / LbaSize);
        child.DataLenLbas = dataLbas;
        child.DataLba = nextLba;
        nextLba += dataLbas;
      }
    }
  }

  /// <summary>Emits the fnode, dirent block (for directories) and data (for files)
  /// of <paramref name="node"/> and recurses into its children.</summary>
  private void WriteNode(byte[] image, TreeNode node, uint parentFnodeLba) {
    if (node.IsDirectory) {
      WriteDirFnode(image, node.FnodeLba, node.DirBlockLba, parentFnodeLba);
      WriteDirBlock(image, node);
      foreach (var child in node.Children.Values)
        WriteNode(image, child, parentFnodeLba: node.FnodeLba);
    } else {
      WriteFileFnode(image, node.FnodeLba, node.DataLba, node.DataLenLbas, parentFnodeLba);
      if (node.Data.Length > 0)
        Buffer.BlockCopy(node.Data, 0, image, (int)(node.DataLba * LbaSize), node.Data.Length);
    }
  }

  private static void WriteBootSector(byte[] image) {
    // OEM ID at offset 3: "IBM 20.0" is a classic HPFS identifier
    Encoding.ASCII.GetBytes("IBM 20.0").CopyTo(image.AsSpan(3, 8));
    // Bytes per sector at offset 11 (u16 LE)
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(11, 2), LbaSize);
    // Boot signature at offset 510
    image[510] = 0x55;
    image[511] = 0xAA;
  }

  private void WriteSuperblock(byte[] image, uint totalSectors) {
    var off = (int)(SuperblockLba * LbaSize);

    // 8-byte magic
    SuperblockMagic.CopyTo(image.AsSpan(off, 8));

    // Version at offset 8 (u8): 2 = HPFS
    image[off + 8] = 2;

    // Functional version at offset 9: 2
    image[off + 9] = 2;

    // Root fnode LBA at offset 12
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 12, 4), RootFnodeLba);

    // Total sectors at offset 16
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 16, 4), totalSectors);

    // Number of bad sectors at offset 20: 0
    // Bitmap start LBA at offset 24
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 24, 4), BitmapLba);

    // Spare block LBA at offset 28
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 28, 4), SpareBlockLba);

    // Directory band (offsets 48/52/56/60). The OS/2 and Linux drivers both reject
    // the volume unless dir_band_end - dir_band_start + 1 == n_dir_band, so these
    // four fields have to agree even on a volume whose band holds a single dnode.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 48, 4), DirBandSectors);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 52, 4), DirBandStartLba);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 56, 4), DirBandStartLba + DirBandSectors - 1);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 60, 4), DirBandBitmapLba);
  }

  /// <summary>
  /// Writes the directory-band bitmap: one bit per dnode, 1 = free, matching the
  /// sector bitmap's polarity. The band holds exactly one dnode (the root dirent
  /// block), which is in use.
  /// </summary>
  private static void WriteDirBandBitmap(byte[] image) {
    var off = (int)(DirBandBitmapLba * LbaSize);
    for (var i = off; i < off + LbaSize; i++)
      image[i] = 0xFF;

    var dnodes = DirBandSectors / DirBlockLbas;
    for (var i = 0u; i < dnodes; i++)
      image[off + (int)(i / 8)] &= (byte)~(1 << (int)(i % 8));
  }

  private static void WriteSpareBlock(byte[] image) {
    var off = (int)(SpareBlockLba * LbaSize);
    // 8-byte spare block magic
    SpareBlockMagic.CopyTo(image.AsSpan(off, 8));
    // Rest is zeroed (no hot-fix entries, no dirty flags)
  }

  /// <summary>Writes a directory fnode whose first direct-allocation entry points
  /// at the directory's dirent block.</summary>
  private static void WriteDirFnode(byte[] image, uint fnodeLba, uint dirBlockLba, uint parentFnodeLba) {
    var off = (int)(fnodeLba * LbaSize);

    FnodeMagic.CopyTo(image.AsSpan(off, 4));

    // Parent-directory fnode LBA at offset 0x0C (used for ".." resolution).
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x0C, 4), parentFnodeLba);

    // Flag this fnode as a directory at offset 0x20 (bit 0). Real HPFS keeps a
    // directory flag in the fnode; we mirror it so readers can corroborate the
    // dirent's directory bit.
    image[off + 0x20] = 0x01;

    // AllocSec header at 0xC0: height=0 (direct list, already zeroed).
    // First direct-allocation entry at 0xC4: points at the dirent block.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 0, 4), 0);            // logical offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 4, 4), DirBlockLbas); // length in sectors
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 8, 4), dirBlockLba);  // physical LBA
  }

  /// <summary>The record length a child's dirent occupies (header + 4-aligned name),
  /// optionally with a trailing 4-byte B-tree down-pointer.</summary>
  private static int DirentRecordLen(string name, bool withDownPointer) {
    var nameLen = Math.Min(Encoding.Latin1.GetByteCount(name), 254);
    var recLen = DirentHeaderLen + nameLen + (withDownPointer ? 4 : 0);
    if ((recLen & 3) != 0) recLen = (recLen + 3) & ~3;
    return recLen;
  }

  /// <summary>Usable dirent bytes in a 2 KiB block (after the block header).</summary>
  private static int DirentAreaBytes => DirBlockSize - DirentAreaOffset;

  /// <summary>
  /// Number of extra leaf dirent blocks a directory needs beyond its root block.
  /// Zero when all children plus the end sentinel fit in one block; otherwise the
  /// directory becomes a 2-level B-tree whose leaves hold the children and whose
  /// root block holds separator dirents with down-pointers.
  /// </summary>
  private static int CountOverflowLeaves(TreeNode dir) {
    var children = dir.Children.Values.ToList();
    if (children.Count == 0) return 0;

    // Does everything fit directly in the root block (entries + end sentinel)?
    var direct = DirentHeaderLen; // reserve the end sentinel
    foreach (var c in children) direct += DirentRecordLen(c.Name, withDownPointer: false);
    if (direct <= DirentAreaBytes) return 0;

    var (leaves, _) = PlanBtree(children);
    return leaves.Count;
  }

  /// <summary>
  /// Plans a 2-level B-tree for the given (sorted) children: greedily fills leaf
  /// blocks, and after each full leaf promotes the next child as a separator in
  /// the root block. Returns the per-leaf child slices and the separator children
  /// (one between consecutive leaves; the rightmost leaf has no separator after it).
  /// </summary>
  private static (List<List<TreeNode>> Leaves, List<TreeNode> Separators) PlanBtree(List<TreeNode> children) {
    var leaves = new List<List<TreeNode>>();
    var separators = new List<TreeNode>();

    var current = new List<TreeNode>();
    var leafUsed = DirentHeaderLen; // reserve end sentinel in each leaf
    var rootUsed = DirentHeaderLen; // reserve end sentinel (carries rightmost down-pointer)

    for (var i = 0; i < children.Count; i++) {
      var child = children[i];
      var leafCost = DirentRecordLen(child.Name, withDownPointer: false);

      if (leafUsed + leafCost <= DirentAreaBytes) {
        current.Add(child);
        leafUsed += leafCost;
        continue;
      }

      // Leaf is full: close it and promote this child as a separator in the root.
      leaves.Add(current);
      separators.Add(child);
      rootUsed += DirentRecordLen(child.Name, withDownPointer: true);
      if (rootUsed > DirentAreaBytes)
        throw new InvalidOperationException(
          "HPFS: directory too large for a 2-level dirent B-tree (the root dirent block is out of separator space). " +
          "With short names this supports roughly 1500 entries per directory; deeper (3-level) B-trees are not yet implemented.");

      current = [];
      leafUsed = DirentHeaderLen;
    }

    leaves.Add(current); // final (rightmost) leaf
    return (leaves, separators);
  }

  /// <summary>Writes a directory's dirent structure. When the children fit in one
  /// 2 KiB block they are written directly; otherwise the root block becomes a
  /// 2-level B-tree of separator dirents whose down-pointers reference the
  /// directory's leaf dirent blocks.</summary>
  private void WriteDirBlock(byte[] image, TreeNode dir) {
    var children = dir.Children.Values.ToList();

    if (dir.LeafBlockLbas.Count == 0) {
      // Fits in one block: plain dirent list + end sentinel.
      WriteLeafDirBlock(image, dir.DirBlockLba, children);
      return;
    }

    var (leaves, separators) = PlanBtree(children);

    // Write each leaf block.
    for (var i = 0; i < leaves.Count; i++)
      WriteLeafDirBlock(image, dir.LeafBlockLbas[i], leaves[i]);

    // Write the root block: separator[i] sits between leaf[i] and leaf[i+1] and
    // carries a down-pointer to leaf[i]; the end sentinel carries the down-pointer
    // to the rightmost leaf.
    var off = (int)(dir.DirBlockLba * LbaSize);
    DirBlockMagic.CopyTo(image.AsSpan(off, 4));

    var cursor = off + DirentAreaOffset;
    for (var i = 0; i < separators.Count; i++) {
      cursor = WriteDirent(image, cursor, separators[i], downPointerLba: dir.LeafBlockLbas[i]);
    }

    // End sentinel with the down-pointer to the last leaf.
    WriteEndSentinel(image, cursor, downPointerLba: dir.LeafBlockLbas[^1]);
  }

  /// <summary>Writes a plain leaf dirent block: sorted child dirents + end sentinel.</summary>
  private static void WriteLeafDirBlock(byte[] image, uint blockLba, List<TreeNode> children) {
    var off = (int)(blockLba * LbaSize);
    DirBlockMagic.CopyTo(image.AsSpan(off, 4));

    var cursor = off + DirentAreaOffset;
    foreach (var child in children)
      cursor = WriteDirent(image, cursor, child, downPointerLba: 0);

    WriteEndSentinel(image, cursor, downPointerLba: 0);
  }

  /// <summary>Writes one child dirent at <paramref name="cursor"/> and returns the next
  /// cursor. When <paramref name="downPointerLba"/> is non-zero the record carries a
  /// trailing 4-byte B-tree down-pointer and the down-pointer flag.</summary>
  private static int WriteDirent(byte[] image, int cursor, TreeNode child, uint downPointerLba) {
    var nameBytes = Encoding.Latin1.GetBytes(child.Name);
    if (nameBytes.Length > 254) nameBytes = nameBytes[..254];

    var hasDown = downPointerLba != 0;
    var recLen = DirentHeaderLen + nameBytes.Length + (hasDown ? 4 : 0);
    if ((recLen & 3) != 0) recLen = (recLen + 3) & ~3;

    // Record layout:
    //   0: u16 recLen
    //   2: u16 flags (bit 2 = down-pointer present, bit 3 = directory)
    //   4: u32 fnodeLba
    //  12: u32 fileSize (0 for directories)
    //  30: u8 nameLen
    //  31: name bytes
    //  recLen-4: u32 down-pointer LBA (when bit 2 set)
    var flags = (ushort)((child.IsDirectory ? DirentFlagDirectory : 0)
                         | (hasDown ? DirentFlagBtreeDown : 0));

    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor, 2), (ushort)recLen);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor + 2, 2), flags);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + 4, 4), child.FnodeLba);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + 12, 4),
      child.IsDirectory ? 0u : (uint)child.Data.Length);
    image[cursor + 30] = (byte)nameBytes.Length;
    nameBytes.CopyTo(image.AsSpan(cursor + 31, nameBytes.Length));

    if (hasDown)
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + recLen - 4, 4), downPointerLba);

    return cursor + recLen;
  }

  /// <summary>Writes the end-of-block sentinel dirent, optionally carrying a
  /// B-tree down-pointer to the rightmost child block.</summary>
  private static void WriteEndSentinel(byte[] image, int cursor, uint downPointerLba) {
    var hasDown = downPointerLba != 0;
    var recLen = DirentHeaderLen + (hasDown ? 4 : 0);
    var flags = (ushort)(DirentFlagSpecial | (hasDown ? DirentFlagBtreeDown : 0));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor, 2), (ushort)recLen);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(cursor + 2, 2), flags);
    if (hasDown)
      BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(cursor + recLen - 4, 4), downPointerLba);
  }

  private static void WriteFileFnode(byte[] image, uint fnodeLba, uint dataLba, uint dataLenLbas, uint parentFnodeLba) {
    var off = (int)(fnodeLba * LbaSize);

    FnodeMagic.CopyTo(image.AsSpan(off, 4));

    // Parent-directory fnode LBA at offset 0x0C.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0x0C, 4), parentFnodeLba);

    // AllocSec header at 0xC0: height=0 (direct list, already zeroed).
    // First direct-allocation entry at 0xC4.
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 0, 4), 0);           // logical offset
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 4, 4), dataLenLbas); // length
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(off + 0xC4 + 8, 4), dataLba);     // physical LBA
  }

  private static void WriteBitmap(byte[] image, uint usedLbas) {
    var off = (int)(BitmapLba * LbaSize);
    // HPFS bitmap: 1 bit per sector, bit=1 means FREE, bit=0 means USED.
    // Fill the entire LBA with 0xFF (all free) then clear bits for used sectors.
    for (var i = off; i < off + LbaSize; i++)
      image[i] = 0xFF;

    // Mark used sectors (bits 0..usedLbas-1) as allocated (bit=0)
    for (var i = 0u; i < usedLbas && i < LbaSize * 8; i++) {
      var byteIdx = (int)(i / 8);
      var bitIdx = (int)(i % 8);
      image[off + byteIdx] &= (byte)~(1 << bitIdx); // Clear bit = used
    }
  }
}

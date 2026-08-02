#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Jfs;

/// <summary>
/// Writes a minimal IBM Journaled File System (JFS1) aggregate image with a single
/// allocation group, one fileset, and an inline dtree root directory.
/// <para>
/// Byte layout matches the on-disk structures in <c>linux/fs/jfs</c> and the
/// <c>jfsutils</c> reference (mkfs.jfs / fsck.jfs); validated by exit-zero from
/// <c>fsck.jfs -n -f -v</c>. All integer fields are little-endian. <c>pxd_t</c>
/// is packed as <c>len_addr = (len &amp; 0xFFFFFF) | ((addr &gt;&gt; 32) &lt;&lt; 24)</c>,
/// <c>addr2 = addr &amp; 0xFFFFFFFF</c>. Dtree slot names are UCS-2 (UTF-16 LE).
/// Round-trips through <see cref="JfsReader"/>.
/// </para>
/// <para>
/// Aggregate inode table (block 11..14, IXSIZE=16 KB) holds the AGGR_RESERVED_I (0),
/// AGGREGATE_I (1, → AIM), BMAP_I (2, → block-allocation map), LOG_I (3), BADBLOCK_I (4)
/// and FILESYSTEM_I (16, → fileset AIM) metadata inodes. The fileset inode table at
/// blocks 29..32 holds FILESET_RSVD_I (0), FILESET_EXT_I (1), ROOT_I (2, dtroot inline),
/// ACL_I (3) and user file inodes (4+).
/// </para>
/// </summary>
public sealed class JfsWriter {
  // ── spec constants ────────────────────────────────────────────────────────
  internal const int SuperblockOffset = 0x8000;   // 64 × 512 = 32768
  public const int BlockSize = 4096;
  internal const int SectorSize = 512;
  internal const int L2BSize = 12;                // log2(4096)
  internal const int L2PBSize = 9;                // log2(512)
  internal const int L2BFactor = 3;               // 4096 / 512 = 8 = 2^3
  internal const uint JfsMagic = 0x3153464A;      // "JFS1" little-endian
  internal const uint JfsVersion = 2;
  internal const int InodeSize = 512;             // DISIZE = 512
  internal const int InodesPerExtent = 32;        // INOSPEREXT = 32 → IXSIZE = 16384
  internal const int InodeExtentSize = 16384;     // 4 blocks @ 4 KB
  internal const int InodeExtentBlocks = 4;       // IXSIZE / BlockSize
  internal const int FilesetIno = 16;             // FILESYSTEM_I in aggregate inode table
  internal const int RootIno = 2;                 // ROOT_I in fileset inode table
  internal const int AclIno = 3;                  // ACL_I in fileset inode table
  internal const int FirstFileIno = 4;            // user files start here (after ACL_I)
  internal const int XtreeDataOffset = 224;       // di_data/dtroot offset inside 512-byte dinode
  internal const int DiDataSize = 288;            // size of _dtroot / _xtroot union (512 - 224)
  internal const int InostampFixed = unchecked((int)0x87878787);
  internal const int InlineDirEntries = 8;        // inline dtroot has 9 slots (1 header + 8 entries)
  internal const int MaxNodesPerIag = Iag_Extsperiag * InodesPerExtent; // 128 extents × 32 = 4096 inodes per IAG

  // ── inode numbers (jfs_filsys.h) ─────────────────────────────────────────
  private const int AggrReservedI = 0;
  private const int AggregateI = 1;               // aggregate inode map inode
  private const int BmapI = 2;                    // aggregate block allocation map inode
  private const int LogI = 3;                     // aggregate inline log inode
  private const int BadblockI = 4;                // aggregate bad block inode
  private const int FilesetExtI = 1;              // fileset inode extension (in fileset inode table)

  // ── di_mode bits (jfs_dinode.h) ──────────────────────────────────────────
  private const uint IfReg = 0x8000;
  private const uint IfDir = 0x4000;
  private const uint IfJournal = 0x00010000;
  private const uint ISparse = 0x00020000;

  // ── btree flags (jfs_btree.h, jfs_types.h) ──────────────────────────────
  private const byte BtRoot = 0x01;
  private const byte BtLeaf = 0x02;
  private const byte BtInternal = 0x04;
  private const byte DxdIndex = 0x80;
  // For B+-tree root pages on-disk: flag = DXD_INDEX | BT_ROOT | BT_LEAF = 0x83
  private const byte BtRootLeafFlag = DxdIndex | BtRoot | BtLeaf;
  // Inline dtroot promoted to a router: DXD_INDEX | BT_ROOT | BT_INTERNAL = 0x85.
  private const byte BtRootInternalFlag = DxdIndex | BtRoot | BtInternal;
  // External directory B+tree leaf / internal pages (no BT_ROOT).
  private const byte BtExternalLeafFlag = BtLeaf;
  private const byte BtExternalInternalFlag = BtInternal;

  // ── directory B+tree (dtpage_t / dtslot, jfs_dtree.h) ────────────────────
  // dtslot = 32 bytes. An external dtpage is one 4 KiB block = 128 slots.
  //   header (slot 0): next(8) prev(8) flag(1) nextindex(1) freecnt(1)
  //     freelist(1) maxslot(1) stblindex(1) rsrvd(2) self pxd(8) = 32 bytes.
  //   The sorted-entry table (stbl) occupies whole 32-byte slots starting at
  //   header.stblindex; the kernel locates it via that field, so its position
  //   is flexible. We place it immediately after the header.
  private const int DtSlotSize = 32;
  private const int DtPageMaxSlot = 128;          // DTPAGEMAXSLOT (4096 / 32)
  private const int DtStblSlotIndex = 1;          // stbl starts at slot 1 in our external pages
  private const int DtRootStblOffset = 24;        // dtroot.header.stbl[8] byte offset in di_data area

  // ── dmap constants (jfs_dmap.h) ──────────────────────────────────────────
  private const int Dmap_Treesize = 256 + 64 + 16 + 4 + 1;   // 341
  private const int Dmap_Leafind = 64 + 16 + 4 + 1;          // 85
  private const int Dmap_Lperdmap = 256;
  private const int Dmap_L2lperdmap = 8;
  private const int Dmap_Bperdmap = 8192;                     // BPERDMAP
  private const int Dmap_L2bperdmap = 13;
  private const int Dmap_Budmin = 5;                          // L2DBWORD
  private const int Dmapctl_Treesize = 1024 + 256 + 64 + 16 + 4 + 1;   // 1365
  private const int Dmapctl_Leafind = 256 + 64 + 16 + 4 + 1;           // 341
  private const int Dmapctl_Lperctl = 1024;
  private const int Dmapctl_L2lperctl = 10;
  private const sbyte Dmap_Nofree = -1;

  // ── imap constants (jfs_imap.h) ──────────────────────────────────────────
  private const int Iag_Smapsz = 4;
  private const int Iag_Extsperiag = 128;

  // ── layout (block addresses) — fixed at 4 KB block size ──────────────────
  // jfs_filsys.h fixes these at: SUPER1_OFF=0x8000 (block 8), AIMAP_OFF=0x9000
  // (block 9), AITBL_OFF=0xB000 (block 11), SUPER2_OFF=0xF000 (block 15),
  // BMAP_OFF=0x10000 (block 16). Following blocks are arranged to leave room
  // for the level-0 dmapctl + dmaps before the secondary AIM/AIT and fileset
  // structures.
  private const int Super1Block = 8;              // primary superblock (fixed)
  private const int AimBlock = 9;                 // primary AIM (2 blocks: dinomap + IAG)
  private const int AitBlock = 11;                // primary AIT (4 blocks)
  private const int Super2Block = 15;             // secondary superblock (fixed)
  private const int BmapBlock = 16;               // dbmap control_page (fixed)
  // For ≤ 8 M blocks (level 0): next_page = BMAP_OFF + PSIZE + PSIZE*(2-0) = 16+1+2 = 19.
  // L0 dmapctl at block 19, dmaps follow at 20, 21, ... one per Dmap_Bperdmap blocks.
  private const int L0DmapctlBlock = 19;
  private const int FirstDmapBlock = 20;

  // The remaining structures float after the BMAP. We pick fixed positions for
  // small images (the only case this writer targets) so the layout is deterministic.
  // For one dmap (≤ 8192 usable blocks), BMAP occupies blocks 16..20 (5 blocks).
  // For two dmaps (≤ 16384 usable blocks), BMAP occupies blocks 16..21 (6 blocks).
  // We always reserve up to 2 dmaps to keep the layout simple.
  /// <summary>
  /// Blocks the block map occupies with a single dmap: the control page, the two
  /// reserved level-skip placeholders, the L0 dmapctl and one dmap. A larger
  /// volume needs one dmap per <see cref="Dmap_Bperdmap" /> blocks, and the map
  /// grows to suit.
  /// </summary>
  private const int BmapMinBlocks = 5;            // 16..20 inclusive

  /// <summary>Dmaps one L0 dmapctl indexes (jfs_dmap.h LPERCTL).</summary>
  private const int Dmap_Lperctl = 1024;
  // These follow the block map, which grows with the volume — one dmap per
  // Dmap_Bperdmap blocks — so they are computed per image rather than pinned to
  // the two-dmap positions the old layout assumed.
  private int _secondaryAimBlock = 22;            // 2 blocks
  private int _secondaryAitBlock = 24;            // 4 blocks
  private int _filesetAimBlock = 28;              // 2 blocks (dinomap + fileset IAG)
  private int _fsitBlock = 30;                    // fileset inode table (first extent)

  /// <summary>Positions the structures that follow the block map for a volume of that size.</summary>
  private void PlaceAfterBlockMap(int usableBlocks) {
    var next = BmapBlock + BmapBlocksFor(usableBlocks);
    // The old layout left one block of slack past a two-dmap map; keep the small
    // volume byte-identical by starting at 22 when the map ends at 21.
    if (next < 22) next = 22;
    this._secondaryAimBlock = next;
    this._secondaryAitBlock = next + 2;
    this._filesetAimBlock = this._secondaryAitBlock + InodeExtentBlocks;
    this._fsitBlock = this._filesetAimBlock + 2;
  }
  private const int MinUsableBlocks = 4096;       // 16 MB minimum (kernel hard floor)

  // fsck computes fsck_blkmap_size = ceil(agg/BITSPERPAGE)+1+50 pages = 52 pages
  // for small images; mkfs.jfs's default inline log is 256 blocks.
  private const int FsckWspBlocks = 52;
  private const int InlineLogBlocks = 256;

  // The fileset inode table grows to as many contiguous 4-block extents as the
  // node count needs; external directory B+tree pages and file data follow it.
  // These two are computed per-image in WriteTo and recorded for the writers.
  private int _fsitExtentCount = 1;               // contiguous inode extents in the fileset table
  private int _dataStartBlock = 30 + InodeExtentBlocks; // first block past the FSIT

  // ── directory tree node ──────────────────────────────────────────────────
  // A node is either a directory (Data == null) or a regular file. Directories
  // hold their children in insertion order; the inline dtree carries them as
  // dtree leaf slots {name, inode}. The root directory is the implicit parent
  // of every top-level entry and always keeps inode RootIno (2).
  private sealed class Node {
    public required string Name;                  // single path component (leaf name)
    public byte[]? Data;                          // null ⇒ directory
    public int Ino;                               // assigned fileset inode number
    public int ParentIno;                         // inode of the containing directory
    public readonly List<Node> Children = [];     // directory children (empty for files)
    public int DataBlock;                         // first data block (files only)
    public int BlockCount;                        // data blocks (files only)
    // Streaming file: when StreamOpener is non-null the file's bytes are not
    // buffered in Data — Data stays empty and the body is pulled from the
    // opener in BuildToStreaming's second pass. StreamSize is the declared
    // byte length used for all geometry (xtree extent, di_size, block count).
    public long? StreamSize;
    public Func<Stream>? StreamOpener;
    public long EffectiveLength => this.StreamSize ?? (this.Data?.Length ?? 0L);
    public bool IsDirectory => this.Data == null;

    // External directory B+tree (directories with more children than the
    // inline dtroot can hold). DtreePages is the flat list of allocated pages
    // (leaf pages first, then internal pages, root-router last); the dinode's
    // inline dtroot routes to RootChildPages.
    public List<DtreePage>? DtreePages;           // null ⇒ inline dtroot
    public List<DtreePage>? TopLayer;             // top-level pages the inline router addresses
  }

  // A built directory B+tree page (leaf or internal) ready to be serialised.
  // `Block` is its absolute image block; `Entries` are the sorted directory
  // entries it carries (for a leaf: child file/dir entries; for an internal
  // page or the root router: one routing key per child page).
  private sealed class DtreePage {
    public int Block;                             // absolute block in the image
    public bool IsLeaf;                           // BT_LEAF vs BT_INTERNAL
    public readonly List<DtreeEntry> Entries = [];
    // Sibling chain within the page's B+tree level. fsck walks each level in
    // key order and requires header.next/prev to address the adjacent page in
    // that level (0 at the ends). Block addresses, set by BuildExternalDtree.
    public int NextSibling;                       // 0 ⇒ last in level
    public int PrevSibling;                       // 0 ⇒ first in level
  }

  // One sorted directory key. For a leaf it addresses a child inode; for a
  // router it addresses a child dtree page (single-block extent at ChildBlock).
  private readonly struct DtreeEntry(string name, int childIno, int childBlock) {
    public string Name { get; } = name;
    public int ChildIno { get; } = childIno;      // leaf entries only
    public int ChildBlock { get; } = childBlock;  // router entries only
  }

  private readonly Node _root = new() { Name = "", ParentIno = RootIno };

  // Optional volume label written into s_label[16] at superblock offset 152.
  // Defaults to the canonical "JFS Workbench"; ASCII, NUL-padded, max 16 bytes.
  private string _volumeLabel = "JFS Workbench";

  /// <summary>
  /// Sets the volume label written into the superblock <c>s_label[16]</c> field
  /// (offset 152). ASCII, NUL-padded, truncated to 16 bytes.
  /// </summary>
  public void SetVolumeLabel(string label) => this._volumeLabel = label ?? "";

  private readonly byte[] _volumeUuid = Guid.NewGuid().ToByteArray();
  private readonly byte[] _logUuid = Guid.NewGuid().ToByteArray();
  private uint _writeTimestamp;                                                           // captured at WriteTo() start so primary/secondary copies match byte-for-byte

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._root.Ino = RootIno;

    // Split the path into components, creating intermediate directories as we
    // descend. Backslashes are accepted as separators too (Windows-style input).
    var parts = name.Split('/', '\\').Where(p => p.Length > 0).ToArray();
    if (parts.Length == 0)
      throw new ArgumentException("File name must contain at least one path component.", nameof(name));

    var dir = this._root;
    for (var i = 0; i < parts.Length - 1; i++) {
      var part = parts[i];
      var child = dir.Children.FirstOrDefault(c => c.IsDirectory && c.Name == part);
      if (child == null) {
        child = new Node { Name = part };
        dir.Children.Add(child);
      }
      dir = child;
    }

    var leaf = parts[^1];
    if (dir.Children.Any(c => c.Name == leaf))
      throw new InvalidOperationException($"Duplicate entry '{leaf}' in the same directory.");
    dir.Children.Add(new Node { Name = leaf, Data = data });
  }

  /// <summary>
  /// Adds a streaming file whose <paramref name="size"/> drives extent + inode
  /// sizing in pass 1 of <see cref="BuildToStreaming"/>; the body is pulled from
  /// <paramref name="openStream"/> in 64 KiB chunks in pass 2 and never buffered
  /// as a <c>byte[]</c>. JFS has no data checksums and stores file bodies in
  /// dedicated xtree extents, so the on-disk image is byte-identical to the
  /// classic <see cref="WriteTo"/> path for the same inputs.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    this._root.Ino = RootIno;

    var parts = name.Split('/', '\\').Where(p => p.Length > 0).ToArray();
    if (parts.Length == 0)
      throw new ArgumentException("File name must contain at least one path component.", nameof(name));

    var dir = this._root;
    for (var i = 0; i < parts.Length - 1; i++) {
      var part = parts[i];
      var child = dir.Children.FirstOrDefault(c => c.IsDirectory && c.Name == part);
      if (child == null) {
        child = new Node { Name = part };
        dir.Children.Add(child);
      }
      dir = child;
    }

    var leaf = parts[^1];
    if (dir.Children.Any(c => c.Name == leaf))
      throw new InvalidOperationException($"Duplicate entry '{leaf}' in the same directory.");
    // Data is an empty (non-null) array so the node reads as a regular file;
    // the real bytes flow through StreamOpener in BuildToStreaming.
    dir.Children.Add(new Node { Name = leaf, Data = [], StreamSize = size, StreamOpener = openStream });
  }

  /// <summary>
  /// When non-null, every streaming file's (absolute data byte-offset, byte
  /// length, opener) is recorded here during <see cref="WriteTo"/>'s data pass
  /// so <see cref="BuildToStreaming"/> can post-fill the bodies. Null in the
  /// classic buffered path, where file bytes are copied from <c>Node.Data</c>.
  /// </summary>
  private List<(long ByteOffset, long Size, Func<Stream> Opener)>? _streamingSink;

  /// <summary>
  /// Assigns fileset inode numbers (4+) to every directory and file in the tree
  /// (root keeps <see cref="RootIno"/>), in a deterministic pre-order walk, and
  /// returns the flat node list excluding the root.
  /// </summary>
  private List<Node> AssignInodes() {
    var ordered = new List<Node>();
    var nextIno = FirstFileIno;
    void Walk(Node dir) {
      foreach (var child in dir.Children) {
        child.Ino = nextIno++;
        child.ParentIno = dir.Ino;
        ordered.Add(child);
        if (child.IsDirectory) Walk(child);
      }
    }
    this._root.Ino = RootIno;
    Walk(this._root);
    // The fileset inode map is a single IAG, which addresses MaxNodesPerIag
    // inodes (4096). Reserve 4 for the always-present metadata inodes (0..3).
    if (FirstFileIno + ordered.Count > MaxNodesPerIag)
      throw new InvalidOperationException($"JfsWriter supports at most {MaxNodesPerIag - FirstFileIno} directories+files in a single fileset IAG.");
    return ordered;
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = this.Build();
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    var image = this.BuildImageBytes();
    image.WriteTo(output);
    this._payloads!.FlushTo(output, basePosition);
    output.Position = basePosition + image.TotalBytes;
    output.Flush();
  }

  /// <summary>Materialises the whole aggregate.</summary>
  public byte[] Build() {
    var image = this.BuildImageBytes();
    return this._payloads!.Materialise(image);
  }

  /// <summary>
  /// Builds the full aggregate image into a byte array. Streaming files (added
  /// via <see cref="AddStreamingFile"/>) leave their data extent zero and, when
  /// <see cref="_streamingSink"/> is set, record their (offset, size, opener)
  /// for the post-fill pass in <see cref="BuildToStreaming"/>.
  /// </summary>
  private DeferredPayloads? _payloads;

  private SparseBlockImage BuildImageBytes() {
    this._writeTimestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // ── lay out the directory tree and assign inodes ──────────────────────
    var nodes = this.AssignInodes();
    var files = nodes.Where(n => !n.IsDirectory).ToList();
    var directories = nodes.Where(n => n.IsDirectory).ToList();

    // ── size the fileset inode table ───────────────────────────────────────
    // Every directory + file gets one dinode; inodes 0..3 are metadata. Pack
    // them into contiguous 16 KiB (4-block) extents so the reader can resolve
    // any inode as this._fsitBlock-relative offset.
    var inodesUsed = FirstFileIno + nodes.Count;
    this._fsitExtentCount = (inodesUsed + InodesPerExtent - 1) / InodesPerExtent;
    if (this._fsitExtentCount < 1) this._fsitExtentCount = 1;
    var fsitBlocks = this._fsitExtentCount * InodeExtentBlocks;

    // The block map takes one dmap per Dmap_Bperdmap blocks of volume, and the
    // structures after it shift along. Size it from the payload before laying
    // anything out, or a large volume's dmaps would land on the secondary AIM.
    var payloadBlocks = 0L;
    foreach (var file in files)
      payloadBlocks += Math.Max(1, (file.EffectiveLength + BlockSize - 1) / BlockSize);
    var estimatedBlocks = 64 + fsitBlocks + payloadBlocks + directories.Count + FsckWspBlocks + InlineLogBlocks;
    this.PlaceAfterBlockMap((int)Math.Min(int.MaxValue, Math.Max(MinUsableBlocks, estimatedBlocks * 2)));

    this._dataStartBlock = this._fsitBlock + fsitBlocks;

    // ── build external directory B+trees and allocate their pages ──────────
    // A directory whose children no longer fit the inline dtroot spills into
    // external dtpage_t leaf pages with the inline dtroot promoted to a router.
    // The inline dtroot has exactly InlineDirEntries (8) slots; each child
    // consumes one head slot plus one continuation slot per name chunk beyond
    // the head's inline capacity, so the spill decision must sum the per-name
    // slot cost rather than count children — a directory packed with multi-slot
    // long names can blow the 8-slot budget well before the child count reaches
    // 8. Pages are single-block extents laid out right after the fileset inode
    // table.
    var nextBlock = this._dataStartBlock;
    foreach (var dir in directories.Prepend(this._root))
      if (InlineSlotsRequired(dir.Children) > InlineDirEntries)
        nextBlock = BuildExternalDtree(dir, nextBlock);

    // ── allocate file data blocks ─────────────────────────────────────────
    // Directories are stored inline (or in external dtree pages above); only
    // regular files claim data blocks here.
    foreach (var file in files) {
      file.DataBlock = nextBlock;
      file.BlockCount = Math.Max(1, (int)((file.EffectiveLength + BlockSize - 1) / BlockSize));
      nextBlock += file.BlockCount;
    }

    // ── image sizing ──────────────────────────────────────────────────────
    // Image must hold all data + fsck workspace + inline log past the visible
    // filesystem boundary. fsck computes fsck_blkmap_size = ceil(agg/BITSPERPAGE)+1+50
    // pages = 52 pages for small images. mkfs.jfs default inline log = 256 blocks.
    var usableBlocks = Math.Max(MinUsableBlocks, nextBlock + 4);
    var totalBlocks = usableBlocks + FsckWspBlocks + InlineLogBlocks;
    // Only the blocks the filesystem populates are held: file payloads are
    // placed by seek afterwards, so a volume past what a byte[] can address
    // costs its metadata rather than its size.
    var image = new SparseBlockImage(BlockSize, (long)totalBlocks * BlockSize);
    this._payloads = new DeferredPayloads();

    WriteSuperblock(image, usableBlocks, FsckWspBlocks, InlineLogBlocks);

    // Track allocated blocks for the dmap pmap/wmap. All metadata + file data is
    // marked allocated; the rest of the usable region is free.
    var allocated = new bool[usableBlocks];
    MarkRange(allocated, 0, Super1Block + 1);                      // boot sectors + super1
    MarkRange(allocated, AimBlock, 2);                             // AIM (dinomap + IAG)
    MarkRange(allocated, AitBlock, InodeExtentBlocks);             // primary AIT (4 blocks)
    MarkRange(allocated, Super2Block, 1);                          // super2
    MarkRange(allocated, BmapBlock, BmapBlocksFor(usableBlocks));  // dbmap + L0 dmapctl + dmaps
    MarkRange(allocated, this._secondaryAimBlock, 2);                    // secondary AIM
    MarkRange(allocated, this._secondaryAitBlock, InodeExtentBlocks);    // secondary AIT
    MarkRange(allocated, this._filesetAimBlock, 2);                      // fileset AIM
    MarkRange(allocated, this._fsitBlock, fsitBlocks);                   // fileset inode table (all extents)
    foreach (var dir in directories.Prepend(this._root))
      if (dir.DtreePages != null)
        foreach (var page in dir.DtreePages)
          MarkRange(allocated, page.Block, 1);                     // external dtree page
    foreach (var file in files)
      MarkRange(allocated, file.DataBlock, file.BlockCount);

    // ── write metadata structures ─────────────────────────────────────────
    // Primary and secondary AIM/AIT are redundant copies. fsck's AIS_inode_check
    // compares every field EXCEPT di_ixpxd between the two AITs, then requires
    // each copy's di_ixpxd to equal the extent it was read from: the primary
    // inodes' di_ixpxd → AitBlock, the secondary inodes' di_ixpxd → the address
    // recorded in s_ait2 (this._secondaryAitBlock). They must therefore differ only in
    // di_ixpxd. The fileset inode map counts every directory + file dinode.
    WriteAggregateInodeMap(image, AimBlock, agStart: 0, inoextBlock: AitBlock);
    this.WriteAggregateInodeTable(image, AitBlock, ixpxdBlock: AitBlock, aimBlock: AimBlock, usableBlocks);
    WriteAggregateInodeMap(image, this._secondaryAimBlock, agStart: 0, inoextBlock: this._secondaryAitBlock);
    this.WriteAggregateInodeTable(image, this._secondaryAitBlock, ixpxdBlock: this._secondaryAitBlock, aimBlock: this._secondaryAimBlock, usableBlocks);
    this.WriteFilesetInodeMap(image, this._filesetAimBlock, nodeCount: nodes.Count);
    this.WriteFilesetInodeTable(image, nodes);
    WriteBlockMap(image, usableBlocks, allocated);

    // External directory B+tree pages (leaves + internal/router pages).
    foreach (var dir in directories.Prepend(this._root))
      if (dir.DtreePages != null)
        foreach (var page in dir.DtreePages)
          WriteExternalDtreePage(image, page);

    // File data. Streaming files leave their extent zero here and record into
    // the sink for BuildToStreaming's chunked post-fill; buffered files copy
    // their Data straight into the extent.
    foreach (var file in files) {
      var byteOffset = (long)file.DataBlock * BlockSize;
      if (file.StreamOpener != null) {
        if (this._streamingSink != null && file.EffectiveLength > 0)
          this._streamingSink.Add((byteOffset, file.EffectiveLength, file.StreamOpener));
        continue;
      }
      var data = file.Data!;
      if (data.Length > 0)
        this._payloads.Add(byteOffset, data);
    }

    return image;
  }

  /// <summary>
  /// Two-pass streaming write: pass 1 builds the complete image byte array
  /// (file data extents left zero, recorded into a sink); pass 2 seeks to each
  /// streaming file's extent and copies its body from the opener in 64 KiB
  /// chunks. The emitted bytes are identical to <see cref="WriteTo"/> for the
  /// same inputs — JFS carries no data checksum, so only WHERE the body bytes
  /// originate changes. Requires a writable, seekable stream.
  /// </summary>
  public void BuildToStreaming(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(long ByteOffset, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    SparseBlockImage image;
    try {
      image = this.BuildImageBytes();
    } finally {
      this._streamingSink = null;
    }

    output.Position = 0;
    image.WriteTo(output);
    this._payloads!.FlushTo(output);

    var buf = new byte[64 * 1024];
    foreach (var (byteOffset, size, opener) in sink) {
      if (size <= 0) continue;
      if (byteOffset < 0 || byteOffset >= output.Length) continue;
      output.Position = byteOffset;
      using var src = opener();
      long copied = 0;
      while (copied < size) {
        var want = (int)Math.Min(buf.Length, size - copied);
        var n = src.Read(buf, 0, want);
        if (n <= 0) break;
        output.Write(buf, 0, n);
        copied += n;
      }
      // Extent tail past `size` keeps the zero from the image init.
    }
    output.Flush();
  }

  private static void MarkRange(bool[] map, int start, int count) {
    var end = Math.Min(map.Length, start + count);
    for (var b = start; b < end; b++) map[b] = true;
  }

  // ── superblock (jfs_superblock, le) ──────────────────────────────────────
  private void WriteSuperblock(SparseBlockImage image, int usableBlocks, int fsckWspBlocks, int logBlocks) {
    WriteSuperblockAt(image, SuperblockOffset, usableBlocks, fsckWspBlocks, logBlocks);
    var super2Offset = Super2Block * BlockSize;
    WriteSuperblockAt(image, super2Offset, usableBlocks, fsckWspBlocks, logBlocks);
  }

  private void WriteSuperblockAt(SparseBlockImage image, int offset, int usableBlocks, int fsckWspBlocks, int logBlocks) {
    var sb = image.At(offset, BlockSize);
    "JFS1"u8.CopyTo(sb);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[4..], 1);                                  // s_version
    BinaryPrimitives.WriteUInt64LittleEndian(sb[8..], (ulong)usableBlocks * (BlockSize / SectorSize)); // s_size in HW blocks
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], BlockSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[20..], L2BSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[22..], L2BFactor);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[24..], SectorSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[28..], L2PBSize);
    BinaryPrimitives.WriteUInt16LittleEndian(sb[30..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], (uint)Dmap_Bperdmap);              // s_agsize = 8192 (1<<L2BPERDMAP)
    const uint JfsLinux = 0x10000000u;
    const uint JfsGroupCommit = 0x00000100u;
    const uint JfsInlineLog = 0x00000800u;
    // JFS_DIR_INDEX selects the modern (11-char head) ldtentry layout in which
    // the head slot is followed by a 4-byte dir_table cookie at +28. fsck.jfs
    // keys the head's inline name capacity off this flag: set ⇒ DTLHDRDATALEN
    // (11), clear ⇒ DTLHDRDATALEN_LEGACY (13). Our dtree writer emits the
    // 11-char-head + index@28 layout, so the flag must be set or fsck splits
    // long names at the wrong offset and rejects the directory ("DF2 corrupt
    // data"). mkfs.jfs sets this bit for every Linux JFS volume.
    const uint JfsDirIndex = 0x00200000u;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[36..], JfsLinux | JfsDirIndex | JfsGroupCommit | JfsInlineLog);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[40..], 0);                                // s_state = FM_CLEAN
    BinaryPrimitives.WriteUInt32LittleEndian(sb[44..], 0);                                // s_compress
    WritePxd(sb[48..], length: (uint)InodeExtentBlocks, address: (ulong)this._secondaryAitBlock);      // s_ait2
    WritePxd(sb[56..], length: 2, address: (ulong)this._secondaryAimBlock);                            // s_aim2
    BinaryPrimitives.WriteUInt32LittleEndian(sb[64..], 0);                                // s_logdev (inline)
    BinaryPrimitives.WriteUInt32LittleEndian(sb[68..], 1);                                // s_logserial
    var fsckAddr = (ulong)usableBlocks;
    var logAddr = fsckAddr + (ulong)fsckWspBlocks;
    WritePxd(sb[72..], length: (uint)logBlocks, address: logAddr);                        // s_logpxd
    WritePxd(sb[80..], length: (uint)fsckWspBlocks, address: fsckAddr);                   // s_fsckpxd
    BinaryPrimitives.WriteUInt32LittleEndian(sb[88..], this._writeTimestamp);             // s_time.tv_sec
    BinaryPrimitives.WriteUInt32LittleEndian(sb[92..], 0);                                // s_time.tv_nsec
    BinaryPrimitives.WriteUInt32LittleEndian(sb[96..], 50);                               // s_fsckloglen
    sb[100] = 0;                                                                          // s_fscklog
    var pack = Encoding.ASCII.GetBytes("JFS-WORKBENCH\0");
    pack.AsSpan(0, Math.Min(pack.Length, 11)).CopyTo(sb[101..]);
    BinaryPrimitives.WriteUInt64LittleEndian(sb[112..], (ulong)usableBlocks * (BlockSize / SectorSize)); // s_xsize
    WritePxd(sb[120..], length: (uint)fsckWspBlocks, address: fsckAddr);                  // s_xfsckpxd
    WritePxd(sb[128..], length: (uint)logBlocks, address: logAddr);                       // s_xlogpxd
    this._volumeUuid.CopyTo(sb[136..]);                                                   // s_uuid[16]
    var label = Encoding.ASCII.GetBytes(this._volumeLabel);
    sb.Slice(152, 16).Clear();
    label.AsSpan(0, Math.Min(label.Length, 16)).CopyTo(sb[152..]);                        // s_label[16]
    this._logUuid.CopyTo(sb[168..]);                                                      // s_loguuid[16]
  }

  // ── aggregate inode map: block AimBlock=dinomap, AimBlock+1=IAG #0 ──────
  // Aggregate AIM has 5 metadata inodes initialized (0..4) plus FILESYSTEM_I (16).
  // wmap[0] = pmap[0] = bits for inodes 0..4 (high bits) + bit for inode 16
  //                   = 0xF8000000 | 0x00008000 = 0xF8008000
  // The bit ordering in JFS bitmaps is MSB=lowest inode, LSB=highest.
  private static void WriteAggregateInodeMap(SparseBlockImage image, int aimBlock, long agStart, int inoextBlock) {
    var dinomapOff = (long)aimBlock * BlockSize;
    var iagOff = dinomapOff + BlockSize;

    // ── dinomap (control page) ──────────────────────────────────────────
    var dm = image.At(dinomapOff, BlockSize);
    dm.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(dm[0..], -1);            // in_freeiag
    BinaryPrimitives.WriteInt32LittleEndian(dm[4..], 1);             // in_nextiag
    BinaryPrimitives.WriteInt32LittleEndian(dm[8..], InodesPerExtent); // in_numinos
    BinaryPrimitives.WriteInt32LittleEndian(dm[12..], InodesPerExtent - 6); // in_numfree (6 = 5 aggr + FS)
    BinaryPrimitives.WriteInt32LittleEndian(dm[16..], InodeExtentBlocks); // in_nbperiext = 4
    BinaryPrimitives.WriteInt32LittleEndian(dm[20..], 2);            // in_l2nbperiext
    BinaryPrimitives.WriteInt32LittleEndian(dm[24..], 0);            // in_diskblock
    BinaryPrimitives.WriteInt32LittleEndian(dm[28..], 0);            // in_maxag
    // in_agctl[0] at offset 2048 (dmap pad section starts at 32, ends at 2047)
    // struct iagctl { int32 inofree; int32 extfree; int32 numinos; int32 numfree; }
    BinaryPrimitives.WriteInt32LittleEndian(dm[2048..], 0);          // inofree = iag 0
    BinaryPrimitives.WriteInt32LittleEndian(dm[2052..], 0);          // extfree = iag 0
    BinaryPrimitives.WriteInt32LittleEndian(dm[2056..], InodesPerExtent); // numinos
    BinaryPrimitives.WriteInt32LittleEndian(dm[2060..], InodesPerExtent - 6); // numfree
    // in_agctl[1..127] = (inofree=-1, extfree=-1, numinos=0, numfree=0) — leave zeroed except inofree/extfree=-1
    for (var i = 1; i < 128; i++) {
      var off = 2048 + i * 16;
      BinaryPrimitives.WriteInt32LittleEndian(dm[off..], -1);
      BinaryPrimitives.WriteInt32LittleEndian(dm[(off + 4)..], -1);
    }

    // ── IAG #0 ──────────────────────────────────────────────────────────
    var iag = image.At(iagOff, BlockSize);
    iag.Clear();
    BinaryPrimitives.WriteInt64LittleEndian(iag[0..], agStart);      // agstart
    BinaryPrimitives.WriteInt32LittleEndian(iag[8..], 0);            // iagnum
    BinaryPrimitives.WriteInt32LittleEndian(iag[12..], -1);          // inofreefwd
    BinaryPrimitives.WriteInt32LittleEndian(iag[16..], -1);          // inofreeback
    BinaryPrimitives.WriteInt32LittleEndian(iag[20..], -1);          // extfreefwd
    BinaryPrimitives.WriteInt32LittleEndian(iag[24..], -1);          // extfreeback
    BinaryPrimitives.WriteInt32LittleEndian(iag[28..], -1);          // iagfree
    // inosmap[0..3]: bit n = "extent n has all-allocated OR not backed".
    // Extent 0 is backed but not all-allocated → bit 31 (highest, MSB) = 0; extents 1..31 not backed → bits 30..0 = 1.
    // → inosmap[0] = 0x7FFFFFFF, inosmap[1..3] = 0xFFFFFFFF (extents 32..127 not backed).
    BinaryPrimitives.WriteUInt32LittleEndian(iag[32..], 0x7FFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[36..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[40..], 0xFFFFFFFFu);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[44..], 0xFFFFFFFFu);
    // extsmap[0..3]: bit n = "extent n is backed".
    // Only extent 0 backed → bit 31 of extsmap[0] = 1, all others = 0.
    BinaryPrimitives.WriteUInt32LittleEndian(iag[48..], 0x80000000u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[52..], 0x00000000u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[56..], 0x00000000u);
    BinaryPrimitives.WriteUInt32LittleEndian(iag[60..], 0x00000000u);
    BinaryPrimitives.WriteInt32LittleEndian(iag[64..], InodesPerExtent - 6); // nfreeinos = 26
    BinaryPrimitives.WriteInt32LittleEndian(iag[68..], Iag_Extsperiag - 1);  // nfreeexts = 127
    // wmap[0] / pmap[0]: bits for inodes 0..4 (top 5) and 16 (bit 15 from the top).
    // MSB = inode 0, LSB = inode 31. inodes {0,1,2,3,4} → 0xF8000000; inode 16 → 0x00008000.
    const uint AggrUsedBitmap = 0xF8000000u | 0x00008000u;           // 0xF8008000
    BinaryPrimitives.WriteUInt32LittleEndian(iag[2048..], AggrUsedBitmap);   // wmap[0]
    BinaryPrimitives.WriteUInt32LittleEndian(iag[2560..], AggrUsedBitmap);   // pmap[0]
    // inoext[0]: pxd(len=4, addr=inoextBlock). fsck (AIM_inode_check) requires
    // the secondary AIM's inoext[0] to equal s_ait2, so the secondary copy must
    // address the secondary AIT extent rather than the primary. All other
    // inoext entries are zero.
    WritePxd(iag[3072..], length: (uint)InodeExtentBlocks, address: (ulong)inoextBlock);
  }

  // ── fileset inode map: block this._filesetAimBlock=dinomap, +1=IAG #0 ─────────
  // Fileset AIM has FILESET_RSVD_I (0), FILESET_EXT_I (1), ROOT_I (2), ACL_I (3)
  // always allocated, plus user file inodes at index 4+.
  private void WriteFilesetInodeMap(SparseBlockImage image, int aimBlock, int nodeCount) {
    var dinomapOff = (long)aimBlock * BlockSize;
    var iagOff = dinomapOff + BlockSize;
    var inodesUsed = 4 + nodeCount;                                  // 0,1,2(root),3(acl) + dir/file nodes
    var nExtents = this._fsitExtentCount;                            // backed inode extents
    var backedInodes = nExtents * InodesPerExtent;                   // total dinodes the table can hold
    var freeInodes = backedInodes - inodesUsed;

    var dm = image.At(dinomapOff, BlockSize);
    dm.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(dm[0..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(dm[4..], 1);
    BinaryPrimitives.WriteInt32LittleEndian(dm[8..], backedInodes);
    BinaryPrimitives.WriteInt32LittleEndian(dm[12..], freeInodes);
    BinaryPrimitives.WriteInt32LittleEndian(dm[16..], InodeExtentBlocks);
    BinaryPrimitives.WriteInt32LittleEndian(dm[20..], 2);
    BinaryPrimitives.WriteInt32LittleEndian(dm[24..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(dm[28..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(dm[2048..], 0);          // in_agctl[0].inofree
    BinaryPrimitives.WriteInt32LittleEndian(dm[2052..], 0);          // .extfree
    BinaryPrimitives.WriteInt32LittleEndian(dm[2056..], backedInodes);
    BinaryPrimitives.WriteInt32LittleEndian(dm[2060..], freeInodes);
    for (var i = 1; i < 128; i++) {
      var off = 2048 + i * 16;
      BinaryPrimitives.WriteInt32LittleEndian(dm[off..], -1);
      BinaryPrimitives.WriteInt32LittleEndian(dm[(off + 4)..], -1);
    }

    var iag = image.At(iagOff, BlockSize);
    iag.Clear();
    BinaryPrimitives.WriteInt64LittleEndian(iag[0..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(iag[8..], 0);
    BinaryPrimitives.WriteInt32LittleEndian(iag[12..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[16..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[20..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[24..], -1);
    BinaryPrimitives.WriteInt32LittleEndian(iag[28..], -1);
    // inosmap[e]/extsmap[e]: 4 words × 32 bits, MSB = lowest extent. extsmap bit
    // set ⇒ extent backed; inosmap bit set ⇒ extent fully allocated OR not backed.
    // All used inodes are packed into the lowest extents; the first extent that
    // still has free inodes is the only backed extent with inosmap bit 0.
    var fullExtents = inodesUsed / InodesPerExtent;                 // extents with no free inode
    Span<uint> inosmap = stackalloc uint[Iag_Smapsz];
    Span<uint> extsmap = stackalloc uint[Iag_Smapsz];
    for (var e = 0; e < Iag_Extsperiag; e++) {
      var word = e >> 5;
      var bit = 0x80000000u >> (e & 31);
      var backed = e < nExtents;
      if (backed) extsmap[word] |= bit;
      // inosmap bit set when not backed, or backed and fully allocated.
      if (!backed || e < fullExtents) inosmap[word] |= bit;
    }
    for (var w = 0; w < Iag_Smapsz; w++) {
      BinaryPrimitives.WriteUInt32LittleEndian(iag[(32 + w * 4)..], inosmap[w]);
      BinaryPrimitives.WriteUInt32LittleEndian(iag[(48 + w * 4)..], extsmap[w]);
    }
    BinaryPrimitives.WriteInt32LittleEndian(iag[64..], freeInodes);
    BinaryPrimitives.WriteInt32LittleEndian(iag[68..], Iag_Extsperiag - nExtents);
    // wmap[]/pmap[]: one bit per inode (MSB = lowest inode). The lowest
    // `inodesUsed` inodes are allocated; the rest are free.
    for (var i = 0; i < inodesUsed; i++) {
      var word = i >> 5;
      var bit = 0x80000000u >> (i & 31);
      var wmapOff = 2048 + word * 4;
      var pmapOff = 2560 + word * 4;
      var w = BinaryPrimitives.ReadUInt32LittleEndian(iag[wmapOff..]) | bit;
      BinaryPrimitives.WriteUInt32LittleEndian(iag[wmapOff..], w);
      BinaryPrimitives.WriteUInt32LittleEndian(iag[pmapOff..], w);
    }
    // inoext[e]: pxd(len=4, addr=this._fsitBlock + e*4) for each backed extent.
    for (var e = 0; e < nExtents; e++)
      WritePxd(iag[(3072 + e * 8)..], length: (uint)InodeExtentBlocks,
        address: (ulong)(this._fsitBlock + e * InodeExtentBlocks));
  }

  // ── aggregate inode table (4 blocks at aitBlock) ─────────────────────────
  // Holds: 0=AGGR_RESERVED_I, 1=AGGREGATE_I, 2=BMAP_I, 3=LOG_I, 4=BADBLOCK_I, 16=FILESYSTEM_I.
  // All have di_fileset = AGGREGATE_I (1) and di_ixpxd = (length=4, addr=ixpxdBlock).
  // <paramref name="ixpxdBlock"/> is the location of THIS copy (primary AitBlock
  // for the primary AIT, this._secondaryAitBlock for the secondary), because fsck
  // requires each copy's di_ixpxd to equal the extent it was read from.
  // <paramref name="aimBlock"/> is the AIM this copy's AGGREGATE_I xtree points
  // at: fsck's AIM_check requires the secondary AGGREGATE_I's xad to equal
  // s_aim2 (the secondary AIM), while BMAP_I/LOG_I/BADBLOCK_I/FILESYSTEM_I
  // xtrees must stay byte-identical between the two copies.
  private void WriteAggregateInodeTable(SparseBlockImage image, int aitBlock, int ixpxdBlock, int aimBlock, int usableBlocks) {
    var aitOff = (long)aitBlock * BlockSize;

    // Inode 0: AGGR_RESERVED_I (di_nlink=1, IFJOURNAL|IFREG, no data)
    var ino0 = (int)(aitOff + (long)AggrReservedI * InodeSize);
    this.WriteAitInode(image, ino0, AggrReservedI, IfJournal | IfReg, ixpxdBlock,
      size: 0, nblocks: 0, hasXtreeData: false, xtreeEntries: null);

    // Inode 1: AGGREGATE_I — xtree → AIM (this copy's AIM, 2 blocks)
    var ino1 = (int)(aitOff + (long)AggregateI * InodeSize);
    this.WriteAitInode(image, ino1, AggregateI, IfJournal | IfReg, ixpxdBlock,
      size: 2L * BlockSize, nblocks: 2, hasXtreeData: true,
      xtreeEntries: [(0, 2u, (ulong)aimBlock)]);

    // Inode 2: BMAP_I — xtree → BMAP, which is one dmap per 8192 blocks of volume.
    // The dmaps past the first are a second extent, so the map is two entries.
    var bmapBlocks = BmapBlocksFor(usableBlocks);
    var ino2 = (int)(aitOff + (long)BmapI * InodeSize);
    this.WriteAitInode(image, ino2, BmapI, IfJournal | IfReg, ixpxdBlock,
      size: (long)bmapBlocks * BlockSize, nblocks: bmapBlocks, hasXtreeData: true,
      xtreeEntries: [(0, (uint)bmapBlocks, (ulong)BmapBlock)]);

    // Inode 3: LOG_I — no data (inline log inode placeholder)
    var ino3 = (int)(aitOff + (long)LogI * InodeSize);
    this.WriteAitInode(image, ino3, LogI, IfJournal | IfReg, ixpxdBlock,
      size: 0, nblocks: 0, hasXtreeData: true, xtreeEntries: []);

    // Inode 4: BADBLOCK_I — sparse, no data
    var ino4 = (int)(aitOff + (long)BadblockI * InodeSize);
    this.WriteAitInode(image, ino4, BadblockI, IfJournal | IfReg | ISparse, ixpxdBlock,
      size: 0, nblocks: 0, hasXtreeData: true, xtreeEntries: []);

    // Inode 16: FILESYSTEM_I — xtree → fileset AIM (this._filesetAimBlock, 2 blocks)
    var ino16 = (int)(aitOff + (long)FilesetIno * InodeSize);
    this.WriteAitInode(image, ino16, FilesetIno, IfJournal | IfReg, ixpxdBlock,
      size: 2L * BlockSize, nblocks: 2, hasXtreeData: true,
      xtreeEntries: [(0, 2u, (ulong)this._filesetAimBlock)], gengen: 1);
  }

  // ── fileset inode table: 4 blocks at this._fsitBlock ───────────────────────────
  // Holds: 0=FILESET_RSVD_I, 1=FILESET_EXT_I, 2=ROOT_I (dtroot inline),
  // 3=ACL_I, 4+=directory and user file inodes (one xtree extent per file,
  // inline dtree per directory).
  private void WriteFilesetInodeTable(SparseBlockImage image, List<Node> nodes) {
    var fsitOff = (long)this._fsitBlock * BlockSize;

    // FILESET_RSVD_I (0)
    var ino0 = (int)(fsitOff + 0 * InodeSize);
    this.WriteFsitInode(image, ino0, ino: 0, fileset: FilesetIno,
      mode: IfJournal | IfReg, size: 0, nblocks: 0,
      hasXtreeData: true, xtreeEntries: []);

    // FILESET_EXT_I (1)
    var ino1 = (int)(fsitOff + 1 * InodeSize);
    this.WriteFsitInode(image, ino1, ino: FilesetExtI, fileset: FilesetIno,
      mode: IfJournal | IfReg, size: 0, nblocks: 0,
      hasXtreeData: true, xtreeEntries: []);

    // ROOT_I (2) — directory with inline dtroot. The root's parent is itself.
    this.WriteDirectoryInode(image, (int)(fsitOff + RootIno * InodeSize), this._root, parentIno: RootIno);

    // ACL_I (3)
    var ino3 = (int)(fsitOff + 3 * InodeSize);
    this.WriteFsitInode(image, ino3, ino: AclIno, fileset: FilesetIno,
      mode: IfJournal | IfReg, size: 0, nblocks: 0,
      hasXtreeData: true, xtreeEntries: []);

    // Directory and file inodes start at FirstFileIno = 4.
    foreach (var node in nodes) {
      var inoOff = (int)(fsitOff + (long)node.Ino * InodeSize);
      if (node.IsDirectory) {
        this.WriteDirectoryInode(image, inoOff, node, parentIno: node.ParentIno);
      } else {
        this.WriteFsitInode(image, inoOff, ino: (uint)node.Ino, fileset: FilesetIno,
          mode: IfJournal | IfReg | 0x1A4,                          // 0644
          size: node.EffectiveLength, nblocks: node.BlockCount,
          hasXtreeData: true,
          xtreeEntries: [(0, (uint)node.BlockCount, (ulong)node.DataBlock)]);
      }
    }
  }

  // ── directory inode (inline dtroot) ──────────────────────────────────────
  // A directory stores its child entries inline in the dinode's di_data union.
  // di_size for an inline-rooted directory is IDATASIZE = 256 (sizeof(dinode)
  // - offsetof(di_inlinedata)); fsck enforces di_size <= IDATASIZE when there
  // are no out-of-line blocks. di_nlink = 2 (self + ".") plus one for each
  // child subdirectory's ".." back-link. idotdot is set to the parent inode.
  private void WriteDirectoryInode(SparseBlockImage image, int inoOff, Node dir, int parentIno) {
    const int IdataSize = 256;
    var subdirs = dir.Children.Count(c => c.IsDirectory);

    if (dir.DtreePages == null) {
      // Inline dtroot: every child fits in the dinode's 8-slot dtroot.
      this.WriteFsitInode(image, inoOff, ino: (uint)dir.Ino, fileset: FilesetIno,
        mode: IfJournal | IfDir | 0x1ED,                            // 0755
        size: IdataSize, nblocks: 0,
        hasXtreeData: false, xtreeEntries: null,
        nlink: (uint)(2 + subdirs), nextIndex: 2);
      WriteInlineDtree(image.At(inoOff + XtreeDataOffset, DiDataSize), dir.Children, parentIno);
      return;
    }

    // External dtree: the inline dtroot is promoted to a router addressing the
    // top-level dtree pages. The leaf pages are out-of-line, so they count
    // toward di_nblocks. di_size, however, stays IDATASIZE: with JFS_DIR_INDEX
    // set (DO_INDEX), the kernel never folds dtree-page bytes into the
    // directory's di_size — only legacy (non-indexed) directories grow di_size
    // by PSIZE per page. fsck.jfs enforces this: under DIR_INDEX it excludes
    // dtree pages from the directory's data_size, so a page-sized di_size would
    // exceed max_size (IDATASIZE) and the root would be rejected as "Invalid
    // data format".
    this.WriteFsitInode(image, inoOff, ino: (uint)dir.Ino, fileset: FilesetIno,
      mode: IfJournal | IfDir | 0x1ED,                              // 0755
      size: IdataSize, nblocks: dir.DtreePages.Count,
      hasXtreeData: false, xtreeEntries: null,
      nlink: (uint)(2 + subdirs), nextIndex: 2);
    WriteRouterDtree(image.At(inoOff + XtreeDataOffset, DiDataSize), dir, parentIno);
  }

  // ── helpers: writing inodes ──────────────────────────────────────────────
  private void WriteAitInode(SparseBlockImage image, int ioff, uint ino, uint mode, int aitBlock,
      long size, long nblocks, bool hasXtreeData,
      (ulong offset, uint length, ulong address)[]? xtreeEntries,
      uint gengen = 0) {
    this.WriteCommonInodeHeader(image, ioff, fileset: AggregateI, ino: ino, mode: mode,
      ixpxdLength: (uint)InodeExtentBlocks, ixpxdAddress: (ulong)aitBlock,
      size: size, nblocks: nblocks, nlink: 1, nextIndex: 2);

    if (gengen != 0) {
      // di_gengen lives in di_data union at u._file._u1._imap._gengen, offset 128 + 4 = 132.
      BinaryPrimitives.WriteUInt32LittleEndian(image.At(ioff + 132, 4), gengen);
    }

    if (hasXtreeData) {
      WriteXtreeRoot(image.At(ioff + XtreeDataOffset, DiDataSize), xtreeEntries ?? []);
    }
  }

  private void WriteFsitInode(SparseBlockImage image, int ioff, uint ino, int fileset, uint mode,
      long size, long nblocks, bool hasXtreeData,
      (ulong offset, uint length, ulong address)[]? xtreeEntries,
      uint nlink = 1, uint nextIndex = 2) {
    // di_ixpxd must address the inode extent that physically contains THIS
    // dinode, not the start of the table: fsck's inode_is_in_use compares each
    // dinode's di_ixpxd against the extent it was read from, so an inode in the
    // second extent (numbers 32..63) needs di_ixpxd = (4, this._fsitBlock + 4), etc.
    var extentBlock = this._fsitBlock + (int)(ino / InodesPerExtent) * InodeExtentBlocks;
    this.WriteCommonInodeHeader(image, ioff, fileset: fileset, ino: ino, mode: mode,
      ixpxdLength: (uint)InodeExtentBlocks, ixpxdAddress: (ulong)extentBlock,
      size: size, nblocks: nblocks, nlink: nlink, nextIndex: nextIndex);

    if (hasXtreeData) {
      WriteXtreeRoot(image.At(ioff + XtreeDataOffset, DiDataSize), xtreeEntries ?? []);
    }
  }

  private void WriteCommonInodeHeader(SparseBlockImage image, int ioff, int fileset, uint ino,
      uint mode, uint ixpxdLength, ulong ixpxdAddress, long size, long nblocks,
      uint nlink, uint nextIndex) {
    var di = image.At(ioff, InodeSize);
    di.Clear();
    BinaryPrimitives.WriteInt32LittleEndian(di[0..], InostampFixed);
    BinaryPrimitives.WriteInt32LittleEndian(di[4..], fileset);
    BinaryPrimitives.WriteUInt32LittleEndian(di[8..], ino);
    BinaryPrimitives.WriteUInt32LittleEndian(di[12..], 1);                // di_gen
    WritePxd(di[16..], ixpxdLength, ixpxdAddress);                        // di_ixpxd
    BinaryPrimitives.WriteInt64LittleEndian(di[24..], size);              // di_size
    BinaryPrimitives.WriteInt64LittleEndian(di[32..], nblocks);           // di_nblocks
    BinaryPrimitives.WriteUInt32LittleEndian(di[40..], nlink);            // di_nlink
    BinaryPrimitives.WriteUInt32LittleEndian(di[44..], 0);                // di_uid
    BinaryPrimitives.WriteUInt32LittleEndian(di[48..], 0);                // di_gid
    BinaryPrimitives.WriteUInt32LittleEndian(di[52..], mode);             // di_mode
    for (var t = 0; t < 4; t++) {
      BinaryPrimitives.WriteUInt32LittleEndian(di[(56 + t * 8)..], this._writeTimestamp);
      BinaryPrimitives.WriteUInt32LittleEndian(di[(60 + t * 8)..], 0);
    }
    BinaryPrimitives.WriteUInt32LittleEndian(di[120..], nextIndex);       // di_next_index
    BinaryPrimitives.WriteUInt32LittleEndian(di[124..], 0);               // di_acltype
    // di_data union starts at offset 128. Caller fills it in (xtree or dtree).
  }

  // ── xtree root in di_data (288 bytes at offset 224..511) ────────────────
  // Layout: next(8) + prev(8) + flag(1) + rsrvd1(1) + nextindex(le16) +
  //         maxentry(le16) + rsrvd2(le16) + self pxd(8) + xad slots[16].
  // First xad starts at slot index 2 (XTENTRYSTART).
  // flag = DXD_INDEX | BT_ROOT | BT_LEAF = 0x83.
  private static void WriteXtreeRoot(Span<byte> data, (ulong offset, uint length, ulong address)[] entries) {
    data.Clear();
    const int XtentryStart = 2;
    var maxEntry = data.Length / 16;                                      // 18 for 288-byte area
    data[16] = BtRootLeafFlag;                                            // 0x83
    data[17] = 0;                                                         // rsrvd1
    BinaryPrimitives.WriteUInt16LittleEndian(data[18..], (ushort)(XtentryStart + entries.Length));
    BinaryPrimitives.WriteUInt16LittleEndian(data[20..], (ushort)maxEntry);
    BinaryPrimitives.WriteUInt16LittleEndian(data[22..], 0);
    WritePxd(data[24..], length: 0, address: 0);                          // self
    for (var i = 0; i < entries.Length; i++) {
      var entryOff = (XtentryStart + i) * 16;
      WriteXad(data.Slice(entryOff, 16), entries[i].offset, entries[i].length, entries[i].address);
    }
  }

  private static void WriteXad(Span<byte> dst, ulong offset, uint length, ulong address) {
    dst.Clear();
    dst[0] = 0;
    dst[1] = 0; dst[2] = 0;
    dst[3] = (byte)((offset >> 32) & 0xFF);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(offset & 0xFFFFFFFF));
    WritePxd(dst[8..], length, address);
  }

  internal static void WritePxd(Span<byte> dst, uint length, ulong address) {
    var lenMasked = length & 0xFFFFFFu;
    var addrHi = (uint)((address >> 32) & 0xFF) << 24;
    BinaryPrimitives.WriteUInt32LittleEndian(dst[0..], lenMasked | addrHi);
    BinaryPrimitives.WriteUInt32LittleEndian(dst[4..], (uint)(address & 0xFFFFFFFF));
  }

  // ── inline dtroot directory at offset 224, 288 bytes total ──────────────
  // Slot 0 = header (DASD 16 + flag 1 + nextindex 1 + freecnt 1 + freelist 1 +
  //          idotdot 4 + stbl[8]). Slots 1..8 = dtslot[32]: a head ldtentry per
  //          entry plus chained continuation slots for names longer than the
  //          head's inline capacity. Each entry references a child inode (a file
  //          or subdirectory) by its UCS-2 leaf name. "." and ".." are not stored
  //          as slots; "." is the directory's own inode and ".." is recorded in
  //          idotdot.
  //
  // ldtentry (head) layout: inumber(le32) + next(s8) + namlen(u8) +
  //   name[DTLHDRDATALEN=11] UCS-2 (22 bytes) + index(le32). The `next` byte is
  //   the slot index of the first continuation dtslot (or -1).
  // dtslot (continuation) layout: next(s8) + cnt(u8) + name[DTSLOTDATALEN=15]
  //   UCS-2 (30 bytes). `cnt` is the number of UCS-2 chars carried in this slot.
  private const int DtHeadNameChars = 11;   // DTLHDRDATALEN
  private const int DtSlotNameChars = 15;    // DTSLOTDATALEN

  private static void WriteInlineDtree(Span<byte> data, IReadOnlyList<Node> children, int parentIno) {
    data.Clear();
    var count = children.Count;
    // fsck (dTree_key_compare_leaflvl) requires the sorted-entry table to list
    // keys in strictly ascending UCS-2 ordinal order. Children arrive in
    // insertion order, so sort by name here exactly as the external dtree does.
    var sorted = children.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    // DASD (16 bytes) at +0 — zero
    data[16] = BtRootLeafFlag;                                            // flag = 0x83
    data[17] = (byte)count;                                               // nextindex (count of populated stbl entries)
    BinaryPrimitives.WriteUInt32LittleEndian(data[20..], (uint)parentIno);// idotdot (".." inode)

    // Allocate dtslots sequentially (indices 1..8). Each entry takes one head
    // slot plus one continuation slot per extra DtSlotNameChars of its name.
    var nextSlot = 1;
    var usedSlots = new bool[InlineDirEntries + 1];

    for (var i = 0; i < count; i++) {
      var child = sorted[i];
      var name = child.Name;
      var headSlot = nextSlot++;
      if (headSlot > InlineDirEntries)
        throw new InvalidOperationException($"Directory entries (including long-name continuation slots) exceed the {InlineDirEntries}-slot inline dtree.");
      usedSlots[headSlot] = true;
      data[24 + i] = (byte)headSlot;                                      // stbl[i]

      var headOff = headSlot * 32;
      BinaryPrimitives.WriteUInt32LittleEndian(data[headOff..], (uint)child.Ino);
      var headChars = Math.Min(name.Length, DtHeadNameChars);
      data[headOff + 5] = (byte)name.Length;                             // total namlen
      for (var c = 0; c < headChars; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(data[(headOff + 6 + c * 2)..], name[c]);
      // ldtentry.index is the dir_table cookie (DTLHDRDATALEN layout, +28).
      // 0 means "no persistent index entry": fsck.jfs explicitly tolerates a
      // zero cookie (the live FS lazily rebuilds the dir_table on first use),
      // so we keep di_next_index = 2 and emit no inline dir_table. A nonzero
      // cookie would have to match a populated dir_table_slot or fsck reports
      // it dirty.
      BinaryPrimitives.WriteUInt32LittleEndian(data[(headOff + 28)..], 0u);

      // Chain continuation slots for any characters beyond the head capacity.
      var prevNextOff = headOff + 4;                                      // head ldtentry `next` byte
      var written = headChars;
      while (written < name.Length) {
        var contSlot = nextSlot++;
        if (contSlot > InlineDirEntries)
          throw new InvalidOperationException($"Directory entries (including long-name continuation slots) exceed the {InlineDirEntries}-slot inline dtree.");
        usedSlots[contSlot] = true;
        data[prevNextOff] = (byte)contSlot;                              // link from previous slot
        var contOff = contSlot * 32;
        var contChars = Math.Min(name.Length - written, DtSlotNameChars);
        data[contOff + 1] = (byte)contChars;                             // cnt
        for (var c = 0; c < contChars; c++)
          BinaryPrimitives.WriteUInt16LittleEndian(data[(contOff + 2 + c * 2)..], name[written + c]);
        written += contChars;
        prevNextOff = contOff;                                           // continuation `next` byte at +0
      }
      data[prevNextOff] = unchecked((byte)-1);                           // terminate the name chain
    }

    // Free-list chain over the unused dtslots. Each free slot points at the next
    // free slot (-1 for the last) and carries cnt = 1, mirroring mkfs.jfs.
    var firstFree = -1;
    var freeCount = 0;
    var prevFreeOff = -1;
    for (var s = 1; s <= InlineDirEntries; s++) {
      if (usedSlots[s]) continue;
      ++freeCount;
      if (firstFree < 0) firstFree = s;
      var slotOff = s * 32;
      if (prevFreeOff >= 0) data[prevFreeOff] = (byte)s;
      data[slotOff] = unchecked((byte)-1);                               // next (overwritten if a later free slot follows)
      data[slotOff + 1] = 1;                                             // cnt = 1
      prevFreeOff = slotOff;
    }

    data[18] = (byte)freeCount;                                          // freecnt
    data[19] = (byte)(freeCount == 0 ? -1 : firstFree);                  // freelist head
  }

  // ── external directory B+tree ───────────────────────────────────────────
  // When a directory's children outgrow the inline dtroot's 8 slots, the
  // entries spill into external dtpage_t leaf pages (one 4 KiB block each).
  // The inline dtroot is promoted to an internal router whose idtentry slots
  // address the top-level pages. For directories large enough that the top
  // layer would itself exceed the 8-slot inline router, we add intermediate
  // internal page layers, forming a real (bottom-up balanced) B+tree.
  //
  // Page slot budget (128 slots of 32 bytes): slot 0 = header; slots
  // DtStblSlotIndex..DtStblSlotIndex+ExternalStblSlots-1 = sorted-table; the
  // remainder hold entry head slots + long-name continuation slots.
  private const int ExternalStblSlots = 4;                          // 4 stbl slots ⇒ ≤128 sorted entries
  private const int ExternalFirstEntrySlot = DtStblSlotIndex + ExternalStblSlots; // first usable entry slot (5)
  private const int ExternalEntrySlots = DtPageMaxSlot - ExternalFirstEntrySlot;  // 123 slots for entries

  // Greedily fills pages of the given kind from the sorted entry list. Returns
  // the built pages (each carrying the entries assigned to it), allocating one
  // image block per page starting at <paramref name="nextBlock"/> (updated by ref).
  private static List<DtreePage> PackPages(List<DtreeEntry> entries, bool leaf, ref int nextBlock) {
    var pages = new List<DtreePage>();
    var page = new DtreePage { Block = nextBlock++, IsLeaf = leaf };
    var slotsLeft = ExternalEntrySlots;
    foreach (var e in entries) {
      var cost = EntrySlotCost(e.Name);
      if (cost > slotsLeft && page.Entries.Count > 0) {
        pages.Add(page);
        page = new DtreePage { Block = nextBlock++, IsLeaf = leaf };
        slotsLeft = ExternalEntrySlots;
      }
      page.Entries.Add(e);
      slotsLeft -= cost;
    }
    pages.Add(page);
    return pages;
  }

  // dtslots one entry consumes: one head slot + one continuation slot per
  // name chunk beyond the head's inline capacity.
  private static int EntrySlotCost(string name) {
    if (name.Length <= DtHeadNameChars) return 1;
    return 1 + (name.Length - DtHeadNameChars + DtSlotNameChars - 1) / DtSlotNameChars;
  }

  // Total inline dtroot slots a directory's children would consume if kept
  // inline. The inline dtroot has InlineDirEntries (8) entry slots; the spill
  // decision compares this sum (not the child count) against that budget so a
  // directory packed with multi-slot long names spills before it overflows.
  private static int InlineSlotsRequired(IReadOnlyList<Node> children) {
    var total = 0;
    foreach (var child in children)
      total += EntrySlotCost(child.Name);
    return total;
  }

  // Builds the directory's external B+tree (leaves bottom-up, then internal
  // layers until the top layer fits the inline router) and records the page
  // list and the top-layer pages the inline router must address.
  private static int BuildExternalDtree(Node dir, int nextBlock) {
    // Sorted leaf entries — ordinal (UCS-2 code-unit) order matches the JFS key
    // ordering. Children are file/subdirectory leaf entries.
    var leafEntries = dir.Children
      .Select(c => new DtreeEntry(c.Name, c.Ino, 0))
      .OrderBy(e => e.Name, StringComparer.Ordinal)
      .ToList();

    var pages = new List<DtreePage>();
    var leaves = PackPages(leafEntries, leaf: true, ref nextBlock);
    ChainSiblings(leaves);
    pages.AddRange(leaves);

    // Build internal layers until the current layer fits the 8-slot router.
    var layer = leaves;
    while (layer.Count > InlineDirEntries) {
      var routers = layer
        .Select(p => new DtreeEntry(FirstKey(p), 0, p.Block))
        .ToList();
      var internals = PackPages(routers, leaf: false, ref nextBlock);
      ChainSiblings(internals);
      pages.AddRange(internals);
      layer = internals;
    }

    dir.DtreePages = pages;
    // The inline router addresses the surviving top layer.
    dir.TopLayer = layer;
    return nextBlock;
  }

  // The smallest (first) key in a page's subtree — used as a routing key.
  private static string FirstKey(DtreePage page) => page.Entries.Count > 0 ? page.Entries[0].Name : "";

  // Threads the forward/backward sibling chain across one B+tree level. fsck
  // (dTree_node_not_first/last_in_level) walks each level in key order and
  // requires header.next/prev to address the adjacent page (0 at the ends).
  private static void ChainSiblings(List<DtreePage> level) {
    for (var i = 0; i < level.Count; i++) {
      level[i].PrevSibling = i > 0 ? level[i - 1].Block : 0;
      level[i].NextSibling = i < level.Count - 1 ? level[i + 1].Block : 0;
    }
  }

  // ── inline dtroot promoted to a router (idtentry slots) ─────────────────
  // Same on-disk dtroot layout as the inline leaf form, but flag = BT_ROOT |
  // BT_INTERNAL and the entries are idtentry (pxd → child page + routing key).
  private static void WriteRouterDtree(Span<byte> data, Node dir, int parentIno) {
    data.Clear();
    var top = dir.TopLayer!;
    data[16] = BtRootInternalFlag;                                       // flag = 0x85
    data[17] = (byte)top.Count;                                          // nextindex
    BinaryPrimitives.WriteUInt32LittleEndian(data[20..], (uint)parentIno); // idotdot

    var nextSlot = 1;
    var usedSlots = new bool[InlineDirEntries + 1];
    for (var i = 0; i < top.Count; i++) {
      var key = FirstKey(top[i]);
      var headSlot = nextSlot++;
      if (headSlot > InlineDirEntries)
        throw new InvalidOperationException($"Router entries exceed the {InlineDirEntries}-slot inline dtroot.");
      usedSlots[headSlot] = true;
      data[DtRootStblOffset + i] = (byte)headSlot;                       // stbl[i]
      nextSlot = WriteIdtentry(data, headSlot, top[i].Block, key, nextSlot, InlineDirEntries, usedSlots);
    }

    WriteFreelist(data, InlineDirEntries, usedSlots);
  }

  // Writes one idtentry head (pxd + next + namlen + inline name) plus any
  // continuation dtslots into the slot array, returning the next free slot.
  private static int WriteIdtentry(Span<byte> data, int headSlot, int childBlock, string name,
      int nextSlot, int maxSlot, bool[] usedSlots) {
    var headOff = headSlot * DtSlotSize;
    WritePxd(data[headOff..], length: 1, address: (ulong)childBlock);    // idtentry.xd
    data[headOff + 9] = (byte)name.Length;                              // namlen at +9 (after pxd 8 + next 1)
    var headChars = Math.Min(name.Length, DtHeadNameChars);
    for (var c = 0; c < headChars; c++)
      BinaryPrimitives.WriteUInt16LittleEndian(data[(headOff + 10 + c * 2)..], name[c]); // name at +10
    nextSlot = WriteNameContinuation(data, headOff + 8, name, headChars, nextSlot, maxSlot, usedSlots);
    return nextSlot;
  }

  // Chains continuation dtslots for the part of <paramref name="name"/> beyond
  // the head's inline capacity. <paramref name="headNextOff"/> is the byte
  // offset of the head entry's `next` field (idtentry: +8, ldtentry: +4).
  private static int WriteNameContinuation(Span<byte> data, int headNextOff, string name,
      int headChars, int nextSlot, int maxSlot, bool[] usedSlots) {
    var prevNextOff = headNextOff;
    var written = headChars;
    while (written < name.Length) {
      var contSlot = nextSlot++;
      if (contSlot > maxSlot)
        throw new InvalidOperationException($"Directory entry name overflows the {maxSlot}-slot dtree page.");
      usedSlots[contSlot] = true;
      data[prevNextOff] = (byte)contSlot;
      var contOff = contSlot * DtSlotSize;
      var contChars = Math.Min(name.Length - written, DtSlotNameChars);
      data[contOff + 1] = (byte)contChars;                             // cnt
      for (var c = 0; c < contChars; c++)
        BinaryPrimitives.WriteUInt16LittleEndian(data[(contOff + 2 + c * 2)..], name[written + c]);
      written += contChars;
      prevNextOff = contOff;
    }
    data[prevNextOff] = unchecked((byte)-1);                           // terminate name chain
    return nextSlot;
  }

  // Threads the freelist over the unused slots [1..maxSlot] of a dtroot.
  private static void WriteFreelist(Span<byte> data, int maxSlot, bool[] usedSlots) {
    var firstFree = -1;
    var freeCount = 0;
    var prevFreeOff = -1;
    for (var s = 1; s <= maxSlot; s++) {
      if (usedSlots[s]) continue;
      ++freeCount;
      if (firstFree < 0) firstFree = s;
      var slotOff = s * DtSlotSize;
      if (prevFreeOff >= 0) data[prevFreeOff] = (byte)s;
      data[slotOff] = unchecked((byte)-1);
      data[slotOff + 1] = 1;
      prevFreeOff = slotOff;
    }
    data[18] = (byte)freeCount;                                        // freecnt
    data[19] = (byte)(freeCount == 0 ? -1 : firstFree);                // freelist head
  }

  // ── external dtpage_t (one 4 KiB block) ─────────────────────────────────
  // header (slot 0): next(8) prev(8) flag(1) nextindex(1) freecnt(1)
  //   freelist(1) maxslot(1) stblindex(1) rsrvd(2) self pxd(8). The sorted
  //   table lives at slot DtStblSlotIndex; entries occupy slots from
  //   ExternalFirstEntrySlot onward. Leaf pages carry ldtentry, internal pages
  //   carry idtentry. A full-tree traversal by the reader follows every entry,
  //   so router keys need not be search-exact.
  private static void WriteExternalDtreePage(SparseBlockImage image, DtreePage page) {
    var pageOff = page.Block * BlockSize;
    var data = image.At(pageOff, BlockSize);
    data.Clear();

    var count = page.Entries.Count;
    BinaryPrimitives.WriteInt64LittleEndian(data[0..], page.NextSibling); // header.next (block addr, 0 if last)
    BinaryPrimitives.WriteInt64LittleEndian(data[8..], page.PrevSibling); // header.prev (block addr, 0 if first)
    data[16] = page.IsLeaf ? BtExternalLeafFlag : BtExternalInternalFlag;
    data[17] = (byte)count;                                            // nextindex
    data[20] = (byte)DtPageMaxSlot;                                    // maxslot = 128
    data[21] = (byte)DtStblSlotIndex;                                  // stblindex
    WritePxd(data[24..], length: 1, address: (ulong)page.Block);       // self pxd

    var stblOff = DtStblSlotIndex * DtSlotSize;
    var nextSlot = ExternalFirstEntrySlot;
    var usedSlots = new bool[DtPageMaxSlot];
    for (var s = DtStblSlotIndex; s < ExternalFirstEntrySlot; s++) usedSlots[s] = true; // stbl slots

    for (var i = 0; i < count; i++) {
      var e = page.Entries[i];
      var headSlot = nextSlot++;
      if (headSlot >= DtPageMaxSlot)
        throw new InvalidOperationException("dtree page entry slot overflow.");
      usedSlots[headSlot] = true;
      data[stblOff + i] = (byte)headSlot;                              // stbl[i]
      if (page.IsLeaf) {
        var headOff = headSlot * DtSlotSize;
        BinaryPrimitives.WriteUInt32LittleEndian(data[headOff..], (uint)e.ChildIno);
        data[headOff + 5] = (byte)e.Name.Length;                       // namlen
        var headChars = Math.Min(e.Name.Length, DtHeadNameChars);
        for (var c = 0; c < headChars; c++)
          BinaryPrimitives.WriteUInt16LittleEndian(data[(headOff + 6 + c * 2)..], e.Name[c]);
        BinaryPrimitives.WriteUInt32LittleEndian(data[(headOff + 28)..], 0u); // dir_table cookie (0 ⇒ unindexed; see WriteInlineDtree)
        nextSlot = WriteNameContinuation(data, headOff + 4, e.Name, headChars, nextSlot, DtPageMaxSlot - 1, usedSlots);
      } else {
        nextSlot = WriteIdtentry(data, headSlot, e.ChildBlock, e.Name, nextSlot, DtPageMaxSlot - 1, usedSlots);
      }
    }

    // Freelist over the remaining unused slots [1..127] (slot 0 = header).
    var firstFree = -1;
    var freeCount = 0;
    var prevFreeOff = -1;
    for (var s = 1; s < DtPageMaxSlot; s++) {
      if (usedSlots[s]) continue;
      ++freeCount;
      if (firstFree < 0) firstFree = s;
      var slotOff = s * DtSlotSize;
      if (prevFreeOff >= 0) data[prevFreeOff] = (byte)s;
      data[slotOff] = unchecked((byte)-1);
      data[slotOff + 1] = 1;
      prevFreeOff = slotOff;
    }
    data[18] = (byte)freeCount;                                         // freecnt (≤127, fits sbyte)
    data[19] = (byte)(freeCount == 0 ? -1 : firstFree);                 // freelist head
  }

  // ── block allocation map (BMAP) ────────────────────────────────────────
  // Layout for level 0 (≤ MAXL0SIZE = 1<<23 = 8M blocks):
  //   block 16: dbmap (control_page)
  //   block 17, 18: reserved (level skip placeholder per `next_page` calc)
  //   block 19: L0 dmapctl
  //   block 20+: dmaps, one per BPERDMAP=8192 blocks
  //
  // We allocate space for up to 2 dmaps (covering 16384 blocks). For the WORM
  // image any usableBlocks ≤ Dmap_Bperdmap = 8192 needs 1 dmap; up to 16384
  // needs 2. The BMAP_I xtree claims the blocks BmapBlocksFor computes
  // (16..21) so the layout after BMAP is fixed.
  /// <summary>Dmaps a volume of <paramref name="usableBlocks" /> blocks needs.</summary>
  private static int DmapCount(int usableBlocks) {
    var ndmaps = (usableBlocks + Dmap_Bperdmap - 1) / Dmap_Bperdmap;
    return Math.Max(1, ndmaps);
  }

  /// <summary>Blocks the block map occupies for a volume of that size.</summary>
  private static int BmapBlocksFor(int usableBlocks) {
    var ndmaps = DmapCount(usableBlocks);
    if (ndmaps > Dmap_Lperctl)
      throw new InvalidOperationException(
        $"JFS: {ndmaps} dmaps exceed the {Dmap_Lperctl} one L0 dmapctl indexes.");
    // Never fewer than the six blocks the two-dmap layout always reserved: a
    // smaller map would free block 21 and shrink BMAP_I, which fsck.jfs rejects.
    return Math.Max(6, BmapMinBlocks - 1 + ndmaps);
  }

  /// <summary>The block holding dmap <paramref name="index" />: the map is contiguous.</summary>
  private static int DmapBlockAt(int index) => FirstDmapBlock + index;

  /// <summary>
  /// Writes the block allocation map — every dmap page, the level-0 dmapctl
  /// above them and the bmap control page — for the given allocation, over an
  /// existing volume.
  /// </summary>
  /// <remarks>
  /// Moving a file changes which blocks are taken, and the map records that in
  /// more than a bitmap: each dmap carries a free-block count and a tree of
  /// free-buddy exponents, and the pages above it summarise those. Flipping the
  /// bits alone leaves the summaries describing a volume that no longer exists,
  /// which fsck.jfs reports. Laying the whole map down again from the new
  /// allocation is both simpler and exact.
  /// </remarks>
  internal static void RewriteBlockMap(Stream image, int usableBlocks, bool[] allocated) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(allocated);

    var scratch = new SparseBlockImage(BlockSize, (long)usableBlocks * BlockSize);
    WriteBlockMap(scratch, usableBlocks, allocated);

    foreach (var block in BlockMapPages(usableBlocks)) {
      var at = (long)block * BlockSize;
      if (at + BlockSize > image.Length) continue;
      image.Position = at;
      image.Write(scratch.Read(at, BlockSize));
    }
    image.Flush();
  }

  /// <summary>The blocks the allocation map itself occupies.</summary>
  private static IEnumerable<int> BlockMapPages(int usableBlocks) {
    yield return BmapBlock;
    yield return L0DmapctlBlock;
    for (var i = 0; i < DmapCount(usableBlocks); ++i)
      yield return DmapBlockAt(i);
  }

  private static void WriteBlockMap(SparseBlockImage image, int usableBlocks, bool[] allocated) {
    var ndmaps = DmapCount(usableBlocks);

    var dmapMaxes = new sbyte[ndmaps];
    for (var i = 0; i < ndmaps; i++) {
      var dmapStartBlock = i * Dmap_Bperdmap;
      var dmapBlockCount = Math.Min(Dmap_Bperdmap, usableBlocks - dmapStartBlock);
      var dmapPageBlock = DmapBlockAt(i);
      dmapMaxes[i] = WriteDmap(image, dmapPageBlock, dmapStartBlock, dmapBlockCount, allocated);
    }

    var ctlMax = WriteDmapctl(image, L0DmapctlBlock, level: 0, dmapMaxes, usableBlocks);
    WriteDbmap(image, BmapBlock, usableBlocks, allocated, agl2size: Dmap_L2bperdmap,
      maxfreebud: ctlMax);
  }

  /// <summary>Writes one dmap page; returns the maximum free-buddy exponent (root of dmaptree).</summary>
  private static sbyte WriteDmap(SparseBlockImage image, int pageBlock, long startBlk, int nblocks, bool[] allocated) {
    var off = (int)((long)pageBlock * BlockSize);
    var page = image.At(off, BlockSize);
    page.Clear();

    // dmap header
    BinaryPrimitives.WriteInt32LittleEndian(page[0..], nblocks);                          // nblocks
    BinaryPrimitives.WriteInt64LittleEndian(page[8..], startBlk);                         // start

    // dmap layout (struct dmap, jfs_dmap.h):
    //   header(16) + dmaptree(360) + pad[1672] = 2048 bytes before wmap
    //   wmap[LPERDMAP=256] at offset 2048 (1024 bytes)
    //   pmap[LPERDMAP=256] at offset 3072 (1024 bytes)
    var wmapOff = 2048;
    var pmapOff = 3072;
    var nfree = 0;
    for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++) {
      var word = 0u;
      for (var bit = 0; bit < 32; bit++) {
        var blk = startBlk + leaf * 32 + bit;
        if (blk >= startBlk + nblocks) {
          // blocks past nblocks: mark as allocated (out of range)
          word |= 0x80000000u >> bit;
          continue;
        }
        var idx = (int)blk;
        if (idx < allocated.Length && allocated[idx]) {
          word |= 0x80000000u >> bit;                                                     // bit set = allocated
        } else {
          ++nfree;
        }
      }
      BinaryPrimitives.WriteUInt32LittleEndian(page[(wmapOff + leaf * 4)..], word);
      BinaryPrimitives.WriteUInt32LittleEndian(page[(pmapOff + leaf * 4)..], word);
    }

    // dmap.nfree at offset 4
    BinaryPrimitives.WriteInt32LittleEndian(page[4..], nfree);

    // ── dmaptree at offset 16 (struct dmaptree, 360 bytes) ─────────────────
    BinaryPrimitives.WriteInt32LittleEndian(page[16..], Dmap_Lperdmap);                   // nleafs = 256
    BinaryPrimitives.WriteInt32LittleEndian(page[20..], Dmap_L2lperdmap);                 // l2nleafs = 8
    BinaryPrimitives.WriteInt32LittleEndian(page[24..], Dmap_Leafind);                    // leafidx = 85
    BinaryPrimitives.WriteInt32LittleEndian(page[28..], 4);                               // height
    page[32] = Dmap_Budmin;                                                               // budmin = 5

    // dmaptree.stree[Treesize] starts at offset 33.
    // Leaves are at stree[Dmap_Leafind .. Dmap_Leafind+Dmap_Lperdmap-1] = stree[85..340].
    // Each leaf records the maximum free-string exponent in its 32-bit wmap word.
    var streeBase = 33;
    var leaves = new sbyte[Dmap_Lperdmap];
    for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++) {
      var word = BinaryPrimitives.ReadUInt32LittleEndian(page[(wmapOff + leaf * 4)..]);
      leaves[leaf] = MaxFreeStringExponent(word);
    }
    for (var leaf = 0; leaf < Dmap_Lperdmap; leaf++)
      page[streeBase + Dmap_Leafind + leaf] = unchecked((byte)leaves[leaf]);

    // Adjust internal nodes by combining buddies (ujfs_adjtree).
    // The tree uses a 4-ary aggregation but per jfs_dmap.h adjtree it's
    // log2-based: each internal node = MAX(child0, child1) + 1 if both children
    // are equal max (a "buddy combine"), else MAX over 4 children.
    // Per ujfs_adjtree the actual algorithm is: for each non-leaf node, it picks
    // MAX(MAX(c0,c1),MAX(c2,c3)) but also if all four children equal level L,
    // they combine into a buddy of level L+1 — which is bounded by budmin+l2numleavesPerNode.
    // We implement the canonical mkfs algorithm below.
    var max = AdjTree(page, streeBase, Dmap_L2lperdmap, Dmap_Budmin);
    return max;
  }

  /// <summary>Writes one dmapctl page covering up to LPERCTL leaves.</summary>
  private static sbyte WriteDmapctl(SparseBlockImage image, int pageBlock, int level, sbyte[] childMaxes, int usableBlocks) {
    var off = (int)((long)pageBlock * BlockSize);
    var page = image.At(off, BlockSize);
    page.Clear();

    BinaryPrimitives.WriteInt32LittleEndian(page[0..], Dmapctl_Lperctl);                  // nleafs = 1024
    BinaryPrimitives.WriteInt32LittleEndian(page[4..], Dmapctl_L2lperctl);                // l2nleafs = 10
    BinaryPrimitives.WriteInt32LittleEndian(page[8..], Dmapctl_Leafind);                  // leafidx = 341
    BinaryPrimitives.WriteInt32LittleEndian(page[12..], 5);                               // height (mkfs uses 5 always)
    page[16] = (byte)(Dmap_L2bperdmap + level * Dmapctl_L2lperctl);                       // budmin (13 for L0)

    var streeBase = 17;
    // Initialise all leaves to NOFREE (-1).
    for (var i = 0; i < Dmapctl_Lperctl; i++)
      page[streeBase + Dmapctl_Leafind + i] = unchecked((byte)Dmap_Nofree);

    // First N leaves get the actual child-dmap max values.
    for (var i = 0; i < childMaxes.Length; i++)
      page[streeBase + Dmapctl_Leafind + i] = unchecked((byte)childMaxes[i]);

    var max = AdjTree(page, streeBase, Dmapctl_L2lperctl, Dmap_L2bperdmap + level * Dmapctl_L2lperctl);
    return max;
  }

  /// <summary>Writes the dbmap (BMAP control_page).</summary>
  private static void WriteDbmap(SparseBlockImage image, int pageBlock, int usableBlocks, bool[] allocated,
      int agl2size, sbyte maxfreebud) {
    var off = (int)((long)pageBlock * BlockSize);
    var page = image.At(off, BlockSize);
    page.Clear();

    var nfree = 0;
    for (var i = 0; i < usableBlocks && i < allocated.Length; i++)
      if (!allocated[i]) ++nfree;

    BinaryPrimitives.WriteInt64LittleEndian(page[0..], usableBlocks);                     // dn_mapsize
    BinaryPrimitives.WriteInt64LittleEndian(page[8..], nfree);                            // dn_nfree
    BinaryPrimitives.WriteInt32LittleEndian(page[16..], 0);                               // dn_l2nbperpage
    BinaryPrimitives.WriteInt32LittleEndian(page[20..], (usableBlocks + Dmap_Bperdmap - 1) / Dmap_Bperdmap); // dn_numag
    BinaryPrimitives.WriteInt32LittleEndian(page[24..], 0);                               // dn_maxlevel
    BinaryPrimitives.WriteInt32LittleEndian(page[28..], 0);                               // dn_maxag
    BinaryPrimitives.WriteInt32LittleEndian(page[32..], 0);                               // dn_agpref
    BinaryPrimitives.WriteInt32LittleEndian(page[36..], 0);                               // dn_aglevel
    BinaryPrimitives.WriteInt32LittleEndian(page[40..], 0);                               // dn_agheigth
    BinaryPrimitives.WriteInt32LittleEndian(page[44..], 1);                               // dn_agwidth
    BinaryPrimitives.WriteInt32LittleEndian(page[48..], Dmapctl_Leafind);                 // dn_agstart = 341
    BinaryPrimitives.WriteInt32LittleEndian(page[52..], agl2size);                        // dn_agl2size = 13
    // dn_agfree[MAXAG=128] starts at offset 56 (8-byte aligned).
    var agSize = 1L << agl2size;
    var numAg = (usableBlocks + (int)agSize - 1) / (int)agSize;
    for (var ag = 0; ag < numAg; ag++) {
      var agStart = ag * agSize;
      var agEnd = Math.Min(agStart + agSize, usableBlocks);
      var agFree = 0L;
      for (var b = agStart; b < agEnd && b < allocated.Length; b++)
        if (!allocated[b]) ++agFree;
      BinaryPrimitives.WriteInt64LittleEndian(page[(56 + ag * 8)..], agFree);
    }
    // dn_agsize at 56 + 128*8 = 1080
    BinaryPrimitives.WriteInt64LittleEndian(page[1080..], agSize);
    // dn_maxfreebud at 1088
    page[1088] = unchecked((byte)maxfreebud);
  }

  // ── tree adjustment (ujfs_adjtree from jfsutils libfs/diskmap.c) ─────────
  // Two-phase algorithm:
  //   Phase 1: walk the leaf array in pairs of size `bsize` (1, 2, 4, ...).
  //     If two adjacent buddies both equal `l2free`, combine: left leaf gets
  //     `l2free+1`, right leaf gets NOFREE (-1). l2free starts at l2min and
  //     increments to l2min + l2leaves.
  //   Phase 2: bubble values up. Each parent at index `(leaf-1)/4` is set to
  //     MAX(four children). Walk from leaf level up to the root (index 0).
  //
  // Tree layout in stree (4-ary):
  //   index 0      = root
  //   index 1..4   = level 1
  //   index 5..20  = level 2
  //   index 21..84 = level 3
  //   index 85..   = leaves (for dmap, 256 leaves → 85..340)
  //   leaf_index = (nleaves - 1) / 3
  private static sbyte AdjTree(Span<byte> page, int streeBase, int l2leaves, int l2min) {
    var nleaves = 1 << l2leaves;
    var leafIndex = (nleaves - 1) / 3;
    var l2max = l2min + l2leaves;

    // Phase 1: combine buddies in the leaf array.
    var bsize = 1;
    for (var l2free = l2min; l2free < l2max; l2free++, bsize <<= 1) {
      var nextb = bsize << 1;
      for (var idx = 0; idx < nleaves; idx += nextb) {
        var leftIdx = streeBase + leafIndex + idx;
        var rightIdx = streeBase + leafIndex + idx + bsize;
        if ((sbyte)page[leftIdx] == l2free && (sbyte)page[rightIdx] == l2free) {
          page[leftIdx] = (byte)(l2free + 1);
          page[rightIdx] = unchecked((byte)Dmap_Nofree);
        }
      }
    }

    // Phase 2: bubble up the tree, 4 children → parent (max of 4).
    var leaf = leafIndex;
    var numAtLevel = nleaves >> 2;
    while (numAtLevel > 0) {
      var parent = (leaf - 1) >> 2;
      for (var i = 0; i < numAtLevel; i++) {
        var c0 = (sbyte)page[streeBase + leaf + i * 4 + 0];
        var c1 = (sbyte)page[streeBase + leaf + i * 4 + 1];
        var c2 = (sbyte)page[streeBase + leaf + i * 4 + 2];
        var c3 = (sbyte)page[streeBase + leaf + i * 4 + 3];
        var max = (sbyte)Math.Max(Math.Max(c0, c1), Math.Max(c2, c3));
        page[streeBase + parent + i] = unchecked((byte)max);
      }
      numAtLevel >>= 2;
      leaf = parent;
    }

    return (sbyte)page[streeBase + 0];
  }

  /// <summary>Compute the maximum buddy-free exponent for a 32-bit wmap word.</summary>
  /// <remarks>
  /// Bit-1 = allocated, bit-0 = free. Returns:
  ///   <list type="bullet">
  ///     <item>BUDMIN (5) if word == 0 (all 32 bits free).</item>
  ///     <item>BUDMIN-1 (4) if either u16 half is 0 (16 contiguous free bits).</item>
  ///     <item>NOFREE (-1) if word == 0xFFFFFFFF (all allocated).</item>
  ///     <item>Otherwise max(budtab[byte0..byte3]) where budtab maps each byte
  ///       to its largest free-buddy exponent (0..3).</item>
  ///   </list>
  /// Mirrors <c>ujfs_maxbuddy</c> from jfsutils libfs/diskmap.c.
  /// </remarks>
  private static sbyte MaxFreeStringExponent(uint word) {
    if (word == 0u) return Dmap_Budmin;
    if (word == 0xFFFFFFFFu) return Dmap_Nofree;
    var hi = (ushort)(word >> 16);
    var lo = (ushort)(word & 0xFFFF);
    if (hi == 0 || lo == 0) return Dmap_Budmin - 1;
    var b0 = BudTab[(byte)(word >> 24)];
    var b1 = BudTab[(byte)(word >> 16)];
    var b2 = BudTab[(byte)(word >> 8)];
    var b3 = BudTab[(byte)word];
    return (sbyte)Math.Max(Math.Max(b0, b1), Math.Max(b2, b3));
  }

  /// <summary>Buddy table from jfsutils libfs/diskmap.c (256 entries, sbyte).</summary>
  private static readonly sbyte[] BudTab = [
    3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0,
    2, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, -1,
  ];
}

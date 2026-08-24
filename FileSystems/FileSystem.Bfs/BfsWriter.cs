#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Bfs;

/// <summary>
/// Builds a minimal BFS (BeOS / Haiku) filesystem image from scratch.
/// Produces a 4 MB image with 1024-byte blocks, 1 allocation group,
/// a chain of B+ tree leaves per directory (entries spill across sibling
/// leaves linked by left_link/right_link when they exceed one node), and
/// direct block_run extents for file data (no indirect/double-indirect).
/// </summary>
/// <remarks>
/// <para><b>On-disk layout:</b></para>
/// <list type="number">
///   <item>Block 0: unused (boot block area on BFS with 512-byte offset convention — we use offset 0)</item>
///   <item>Block 1: superblock (1024 bytes, magic BFS1 at offset 32)</item>
///   <item>Blocks 2..9: log area (8 blocks, clean/empty)</item>
///   <item>Block 10: AG 0 bitmap (1 bit per block)</item>
///   <item>Block 11: root dir inode (magic InNd, mode=dir)</item>
///   <item>Block 12: root dir B+ tree leaf (magic BPLV, file→inode entries)</item>
///   <item>Block 13: indices dir inode (magic InNd, mode=dir, empty)</item>
///   <item>Block 14: indices dir B+ tree leaf (magic BPLV, empty)</item>
///   <item>Blocks 15..: file inodes (1 block each) then file data blocks</item>
/// </list>
/// <para>
/// Each entry costs ~10 + name_length bytes in a leaf; a 1024-byte leaf holds
/// roughly 40–60 short-named files. Directories larger than that spill across
/// additional leaf blocks chained via right_link, so a single directory can hold
/// thousands of entries (bounded only by the image size).
/// </para>
/// </remarks>
internal sealed class BfsWriter {

  private const uint Magic1 = 0x42465331;  // 'BFS1' in LE
  private const uint Magic2 = 0xDD121031;
  private const uint Magic3 = 0x15B6830E;
  /// <summary>
  /// The magic every BFS inode begins with.
  /// </summary>
  /// <remarks>
  /// One nibble of this was wrong — 0x3BDE0AD9 for 0x3BBE0AD9 — in the writer,
  /// the reader and the in-place modifier alike, so all three agreed and the
  /// kernel's befs driver rejected the root inode of every volume this project
  /// had ever written: "Inode has a bad magic header - inode = 11".
  /// </remarks>
  private const uint InodeMagic = 0x3BBE0AD9;

  /// <summary>BEFS_INODE_IN_USE — this inode is live, not merely present.</summary>
  private const uint InodeInUse = 0x00000001;
  private const uint BplusLeafMagic = 0x69F6C2E8; // 'BPLV' B+ tree leaf node magic

  private const int BlockSize = 1024;
  private const int BlockShift = 10;       // 1 << 10 = 1024
  private const int DefaultImageBlocks = 4096;  // 4 MB
  private const int LogStartBlock = 2;
  private const int LogBlocks = 8;
  private const int AgBitmapBlock = 10;    // first allocation-group bitmap block

  /// <summary>
  /// Blocks one allocation group holds. A block_run addresses its start as a u16
  /// within a group, so the group has to be at most 65536 blocks — and with the
  /// group's own index in the run's u32 AG field, a volume can then be far larger
  /// than one group. A single group was what capped this writer at 64 MB.
  /// </summary>
  private const int BlocksPerAg = 1 << 16;

  /// <summary>log2 of <see cref="BlocksPerAg" />, the superblock's ag_shift.</summary>
  private const int AgShift = 16;

  /// <summary>Largest run a block_run can express: its length is a u16.</summary>
  private const int MaxRunBlocks = ushort.MaxValue;

  // Layout chosen per build; the bitmap's size shifts everything after it.
  private int _bitmapBlocks = 1;
  private int _rootDirInodeBlock;
  private int _indicesInodeBlock;

  /// <summary>Block runs one indirect block holds.</summary>
  private const int RunsPerBlock = BlockSize / 8;

  // BFS inode mode bits (POSIX-compatible)
  private const uint S_IFDIR = 0x4000;
  private const uint S_IFREG = 0x8000;
  private const uint S_IRWXU = 0x01C0; // rwx for owner

  // BFS inode size
  private const int InodeSize = 1024;

  // data_stream offsets within inode (after 176 bytes of fixed fields)
  // Inode layout:
  //   0: magic1 (u32)
  //   4: inode_num (block_run = 8 bytes)
  //  12: uid (u32)
  //  16: gid (u32)
  //  20: mode (u32)
  //  24: flags (u32)
  //  28: create_time (i64)
  //  36: last_modified_time (i64)
  //  44: parent (block_run = 8 bytes)
  //  52: attributes (block_run = 8 bytes)
  //  60: type (u32) = file type code
  //  64: inode_size (u32) = size of this inode on disk
  //  68: etc. (unused, up to 176)
  // small_data: 176..end of inode block (we don't use it)
  // data_stream starts at offset 100:
  //   100: direct[0..11] = 12 block_runs, each 8 bytes = 96 bytes total (offsets 100..195)
  //   196: max_direct_range (i64)
  //   204: indirect (block_run = 8 bytes)
  //   212: max_indirect_range (i64)
  //   220: double_indirect (block_run = 8 bytes)
  //   228: max_double_indirect_range (i64)
  //   236: size (i64) = logical file size
  // Total data_stream: 144 bytes

  // Actually let's follow the Haiku source more carefully:
  // struct bfs_inode (from Haiku src/add-ons/kernel/file_systems/bfs/bfs.h):
  //   int32  magic1;              // 0
  //   inode_addr  inode_num;      // 4  (block_run = 8 bytes)
  //   int32  uid;                 // 12
  //   int32  gid;                 // 16
  //   int32  mode;                // 20
  //   int32  flags;               // 24
  //   bigtime_t create_time;      // 28 (i64, microseconds since epoch)
  //   bigtime_t last_modified_time; // 36 (i64)
  //   inode_addr parent;          // 44 (block_run = 8 bytes)
  //   inode_addr attributes;      // 52 (block_run = 8 bytes)
  //   uint32 type;                // 60
  //   int32  inode_size;          // 64
  //   uint32 etc;                 // 68 (unused padding = 0)
  //   // data_stream starts at offset 72 (Giampaolo book)
  //   // BUT Haiku actually puts padding and then data_stream
  //   // Let me check: small_data_start at offset 68? No.
  //
  // Per Giampaolo's book (p.159), the inode is:
  //   0-3:   magic1 (0x3BBE0AD9)
  //   4-11:  inode_num (block_run)
  //   12-15: uid
  //   16-19: gid
  //   20-23: mode
  //   24-27: flags
  //   28-35: create_time
  //   36-43: last_modified_time
  //   44-51: parent (block_run)
  //   52-59: attributes (block_run)
  //   60-63: type
  //   64-67: inode_size
  //   68-71: etc/pad
  //
  //   72: data_stream begins
  //     72-167:  direct[0..11] = 12 * 8 = 96 bytes
  //     168-175: max_direct_range (i64)
  //     176-183: indirect (block_run)
  //     184-191: max_indirect_range (i64)
  //     192-199: double_indirect (block_run)
  //     200-207: max_double_indirect_range (i64)
  //     208-215: size (i64)
  //
  //   216: small_data starts (rest of inode, variable)

  private const int InodeDataStreamOffset = 72;
  private const int NumDirectBlocks = 12;

  private readonly List<(string Path, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];

  /// <summary>
  /// Streaming-allocations side-effect: when non-null, every streaming entry's
  /// (absolute data-block byte offset, size, opener) is appended for use by
  /// <see cref="BuildToStreaming"/>'s post-stream pass. When null, the writer
  /// behaves identically to before.
  /// </summary>
  private List<(long ByteOffset, long Size, Func<Stream> Opener)>? _streamingSink;

  /// <summary>
  /// Adds a file to the image. The name may contain '/' (or '\') separators,
  /// in which case the leading segments become real subdirectory inodes and the
  /// file is stored inside the corresponding directory tree.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    var normalized = name.Replace('\\', '/').Trim('/');
    if (string.IsNullOrEmpty(normalized))
      throw new ArgumentException("File name must not be empty.", nameof(name));
    _files.Add((normalized, data, null, null));
  }

  /// <summary>
  /// Adds a streaming file: <paramref name="size"/> drives extent + inode + AG
  /// sizing in pass 1; bytes are pulled from <paramref name="openStream"/> in
  /// pass 2 of <see cref="BuildToStreaming"/>. Never buffered as <c>byte[]</c>.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(openStream);
    var normalized = name.Replace('\\', '/').Trim('/');
    if (string.IsNullOrEmpty(normalized))
      throw new ArgumentException("File name must not be empty.", nameof(name));
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    _files.Add((normalized, System.Array.Empty<byte>(), size, openStream));
  }

  /// <summary>
  /// In-memory directory tree node assembled from the flat file list before
  /// blocks are allocated. Directories own a child map (sorted by name for a
  /// stable B+ tree order); files carry their payload.
  /// </summary>
  private sealed class TreeNode {
    public required string Name;
    public bool IsDir;
    public byte[]? Data;
    public long? StreamingSize;
    public Func<Stream>? StreamOpener;
    public readonly SortedDictionary<string, TreeNode> Children =
      new(StringComparer.Ordinal);

    // Block assignments filled in during layout.
    public long InodeBlock;
    public long BtreeBlock;      // directories only — first B+ tree leaf block
    public int BtreeBlocks = 1;  // directories only — number of chained leaf blocks
    public readonly List<int> LeafBlocks = []; // directories only — every leaf block, in chain order
    public long DataStartBlock;  // files only
    public long DataBlocks;      // files only
    public long IndirectBlock;   // files only — first block of the indirect run array
    public int IndirectBlockCount;
    public long ParentInodeBlock;
  }

  /// <summary>Declared size of the volume the last build laid out.</summary>
  private long TotalImageBytes { get; set; }

  private DeferredPayloads? _payloads;

  /// <summary>Builds the BFS image and returns the raw bytes.</summary>
  public byte[] Build() {
    var image = this.BuildSparse();
    return this._payloads!.Materialise(image);
  }

  private SparseBlockImage BuildSparse() {
    // 1) Build the directory tree from the flat file list. The synthetic root
    //    uses the fixed root-dir inode/B+ tree blocks (11/12).
    var root = new TreeNode { Name = string.Empty, IsDir = true };
    foreach (var (path, data, streamingSize, opener) in _files) {
      var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
      var cursor = root;
      for (var i = 0; i < segments.Length; i++) {
        var seg = segments[i];
        var isLast = i == segments.Length - 1;
        if (isLast) {
          // Leaf segment: the file itself. Reject collisions with a directory.
          if (cursor.Children.TryGetValue(seg, out var existing) && existing.IsDir)
            throw new InvalidOperationException($"BFS: '{path}' collides with an existing directory of the same name.");
          cursor.Children[seg] = new TreeNode { Name = seg, IsDir = false, Data = data, StreamingSize = streamingSize, StreamOpener = opener };
        } else {
          if (!cursor.Children.TryGetValue(seg, out var child)) {
            child = new TreeNode { Name = seg, IsDir = true };
            cursor.Children[seg] = child;
          } else if (!child.IsDir) {
            throw new InvalidOperationException($"BFS: '{path}' uses '{seg}' as a directory, but it is already a file.");
          }
          cursor = child;
        }
      }
    }

    // 2) Allocate blocks. Fixed metadata occupies blocks 0..14; everything we
    //    create starts at block 15. Each directory needs an inode block plus a
    //    chain of B+ tree leaf blocks (one leaf per ~40-60 short entries); each
    //    file needs an inode block plus its data blocks.
    //
    //    The root directory already owns its inode (block 11) and its first leaf
    //    (block 12). If the root needs more leaves, the overflow leaves come from
    //    the free pool (block 15+); the reader chains them via right_link, so they
    //    need not be contiguous with block 12.
    // The allocation bitmap covers every block in the volume, so it grows with
    // the volume and the fixed metadata after it shifts along. Its size depends
    // on the block count, which depends on the payload, so it is sized from the
    // payload up front.
    var payloadBlocks = 0L;
    foreach (var (_, data, streamingSize, _) in _files) {
      var length = streamingSize ?? (data?.Length ?? 0);
      payloadBlocks += (length + BlockSize - 1) / BlockSize + 1;   // data + its inode
    }
    var estimate = Math.Max(DefaultImageBlocks, AgBitmapBlock + payloadBlocks + 64);
    var bitmapBlocks = (int)(((estimate + 7) / 8 + BlockSize - 1) / BlockSize);
    // One more pass: the bitmap itself pushes the volume out, which can need one
    // more bitmap block.
    bitmapBlocks = (int)(((estimate + bitmapBlocks + 7) / 8 + BlockSize - 1) / BlockSize);

    // A directory's contents are a B+ tree, and a B+ tree begins with its own
    // superblock — magic, node size, and where the root node sits inside the
    // stream — with the nodes after it. The stream used to start at the first
    // node, so the driver read a node header where the tree's own header should
    // be and said "Index header has bad magic". Each directory therefore gets one
    // block for that header and its leaves contiguously behind it, which keeps
    // the whole stream a single run.
    var rootLeaves = CountLeafBlocks(root);
    var rootDirInodeBlock = AgBitmapBlock + bitmapBlocks;
    var rootDirBtreeBlock = rootDirInodeBlock + 1;
    var indicesInodeBlock = rootDirBtreeBlock + 1 + rootLeaves;
    var indicesBtreeBlock = indicesInodeBlock + 1;
    var nextBlock = indicesBtreeBlock + 2;   // its header and one empty leaf

    root.InodeBlock = rootDirInodeBlock;
    root.BtreeBlock = rootDirBtreeBlock;
    this._bitmapBlocks = bitmapBlocks;
    this._rootDirInodeBlock = rootDirInodeBlock;
    this._indicesInodeBlock = indicesInodeBlock;
    for (var i = 0; i < rootLeaves; i++)
      root.LeafBlocks.Add((int)root.BtreeBlock + 1 + i);
    root.BtreeBlocks = root.LeafBlocks.Count;
    AssignBlocks(root, root.InodeBlock, ref nextBlock);

    var totalUsedBlocks = nextBlock;
    var numBlocks = Math.Max(DefaultImageBlocks, totalUsedBlocks);
    this.TotalImageBytes = (long)numBlocks * BlockSize;
    // Only the blocks the filesystem populates are held: file payloads are
    // placed by seek afterwards, so a volume past what a byte[] can address
    // costs its metadata rather than its size. The layout is untouched, so the
    // bytes are the same either way.
    var image = new SparseBlockImage(BlockSize, this.TotalImageBytes);
    this._payloads = new DeferredPayloads();

    // --- Superblock at block 0 (offset 0) ---
    this.WriteSuperblock(image, numBlocks, totalUsedBlocks);

    // --- Log area (blocks 2..9) — all zeroes = clean ---

    // --- Allocation bitmap, starting at block 10 ---
    WriteAgBitmap(image, totalUsedBlocks, bitmapBlocks);

    // --- Root dir inode + its B+ tree ---
    WriteDirectoryInode(image, root.InodeBlock, root.BtreeBlock, 0, 0, root.BtreeBlocks);
    WriteBtreeSuper(image, root.BtreeBlock, root.BtreeBlocks);
    WriteDirBtreeLeaf(image, root);

    // --- Indices dir inode + empty B+ tree ---
    WriteDirectoryInode(image, indicesInodeBlock, indicesBtreeBlock, rootDirInodeBlock, 0, 1);
    WriteBtreeSuper(image, indicesBtreeBlock, 1);
    WriteEmptyBtreeLeaf(image, indicesBtreeBlock + 1);

    // --- All non-root directories and files (depth-first) ---
    WriteNode(image, root);

    return image;
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 lays out the identical BFS image (same
  /// block allocation + inodes + B+ trees as <see cref="Build"/>) with each
  /// streaming file's data run left zero; pass 2 seeks to each file's first
  /// data-block byte offset and copies its bytes from the opener in 64 KB
  /// chunks. Output is byte-for-byte identical to <see cref="Build"/>'s result
  /// for the same inputs.
  /// </summary>
  public void BuildToStreaming(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(long ByteOffset, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    SparseBlockImage image;
    try {
      image = this.BuildSparse();
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
      // Block tail past `size` retains zero from the image init.
    }
    output.Flush();
  }

  /// <summary>
  /// Walks the tree depth-first assigning on-disk blocks. The current
  /// directory's inode/B+ tree blocks are assumed pre-assigned; this routine
  /// assigns blocks for its children and recurses into subdirectories.
  /// </summary>
  private static void AssignBlocks(TreeNode dir, long dirInodeBlock, ref int nextBlock) {
    foreach (var child in dir.Children.Values) {
      child.ParentInodeBlock = dirInodeBlock;
      if (child.IsDir) {
        child.InodeBlock = nextBlock++;
        // A directory's children are spilled across one or more chained B+ tree
        // leaves; allocate that many contiguous blocks. The first is BtreeBlock.
        var leafCount = CountLeafBlocks(child);
        child.BtreeBlock = nextBlock++;        // the tree's own header
        for (var i = 0; i < leafCount; i++)
          child.LeafBlocks.Add(nextBlock++);
        child.BtreeBlocks = leafCount;
      } else {
        child.InodeBlock = nextBlock++;
        var length = child.StreamingSize ?? (child.Data?.Length ?? 0);
        child.DataBlocks = (length + BlockSize - 1) / BlockSize;

        // The blocks are contiguous, but a run neither crosses an allocation
        // group nor exceeds its u16 length. Past twelve runs the rest live in an
        // indirect array, whose blocks are reserved ahead of the data so the data
        // itself stays in one span.
        var runs = SplitIntoRuns(nextBlock + 1L, child.DataBlocks);
        if (runs.Count > NumDirectBlocks) {
          child.IndirectBlockCount = (runs.Count - NumDirectBlocks + RunsPerBlock - 1) / RunsPerBlock;
          child.IndirectBlock = nextBlock;
          nextBlock += child.IndirectBlockCount;
        }

        child.DataStartBlock = nextBlock;
        nextBlock += (int)child.DataBlocks;
      }
    }

    // Recurse only after all direct children are laid out, so each directory's
    // data blocks stay contiguous behind its inode.
    foreach (var child in dir.Children.Values)
      if (child.IsDir)
        AssignBlocks(child, child.InodeBlock, ref nextBlock);
  }

  private const int BtreeLeafHeaderSize = 28;

  /// <summary>
  /// Greedily partitions a directory's (sorted) children into leaf-sized groups.
  /// Each leaf holds the 28-byte header, a u16 key-length table entry plus the UTF-8
  /// name bytes per child, and a u64 value per child written from the block's tail.
  /// An entry is placed in the current leaf if it still fits; otherwise a new leaf
  /// is started. A single name that cannot fit even alone is rejected.
  /// </summary>
  private static List<List<(string Name, long InodeBlock)>> PartitionEntries(TreeNode dir) {
    var leaves = new List<List<(string Name, long InodeBlock)>>();
    var current = new List<(string Name, long InodeBlock)>();
    var used = BtreeLeafHeaderSize;

    foreach (var child in dir.Children.Values) {
      var nameLen = Encoding.UTF8.GetByteCount(child.Name);
      var cost = 2 + nameLen + 8; // key-length table slot + name bytes + value slot
      if (BtreeLeafHeaderSize + cost > BlockSize)
        throw new InvalidOperationException(
          $"BFS: directory entry '{child.Name}' is too large to fit in a single {BlockSize}-byte B+ tree leaf.");

      if (used + cost > BlockSize) {
        leaves.Add(current);
        current = [];
        used = BtreeLeafHeaderSize;
      }

      current.Add((child.Name, child.InodeBlock));
      used += cost;
    }

    leaves.Add(current); // always at least one leaf, even for an empty directory
    return leaves;
  }

  /// <summary>Number of chained B+ tree leaf blocks a directory's children require.</summary>
  private static int CountLeafBlocks(TreeNode dir) => PartitionEntries(dir).Count;

  /// <summary>Writes every non-root node (inodes, B+ trees, file data) depth-first.</summary>
  private void WriteNode(SparseBlockImage image, TreeNode dir) {
    foreach (var child in dir.Children.Values) {
      if (child.IsDir) {
        WriteDirectoryInode(image, child.InodeBlock, child.BtreeBlock, child.ParentInodeBlock, 0,
          child.BtreeBlocks);
        WriteBtreeSuper(image, child.BtreeBlock, child.BtreeBlocks);
        WriteDirBtreeLeaf(image, child);
      } else {
        var length = child.StreamingSize ?? (child.Data?.Length ?? 0);
        WriteFileInode(image, child.InodeBlock, child.ParentInodeBlock, child.DataStartBlock,
          child.DataBlocks, length, child.IndirectBlock, child.IndirectBlockCount);
        if (child.StreamOpener != null) {
          // Streaming entry: leave the data blocks zero; the streaming pass
          // post-fills from this absolute byte offset. Block tail past the
          // logical size stays zero (matches the in-memory path).
          if (this._streamingSink != null && length > 0)
            this._streamingSink.Add(((long)child.DataStartBlock * BlockSize, length, child.StreamOpener));
        } else {
          // Inline payloads are placed by seek like streamed ones: the image
          // buffer holds metadata, never file contents.
          var data = child.Data ?? [];
          if (data.Length > 0)
            this._payloads!.Add((long)child.DataStartBlock * BlockSize, data);
        }
      }
    }

    foreach (var child in dir.Children.Values)
      if (child.IsDir)
        WriteNode(image, child);
  }

  private void WriteSuperblock(SparseBlockImage image, int numBlocks, int usedBlocks) {
    // Superblock at offset 0 (block 0). Some BFS implementations use offset 512;
    // we use offset 0 for simplicity (our reader checks both locations).
    var off = 0;

    // name[32] — volume name "BFS Volume"
    image.Write(off, Encoding.ASCII.GetBytes("BFS Volume"));

    // magic1 at 32
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 32, 4), Magic1);

    // fs_byte_order at 36 = BIGE (0x42494745) — standard BFS byte order marker
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 36, 4), 0x42494745u);

    // block_size at 40
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 40, 4), BlockSize);

    // block_shift at 44
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 44, 4), BlockShift);

    // num_blocks at 48
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 48, 8), numBlocks);

    // used_blocks at 56
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 56, 8), usedBlocks);

    // inode_size at 64
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 64, 4), InodeSize);

    // magic2 at 68
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 68, 4), Magic2);

    // blocks_per_ag at 72 — a block_run's start is a u16, so a group is 65536 blocks
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 72, 4), BlocksPerAg);

    // ag_shift at 76 — log2 of blocks_per_ag
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 76, 4), AgShift);

    // num_ags at 80
    var numAgs = (uint)((numBlocks + BlocksPerAg - 1) / BlocksPerAg);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 80, 4), numAgs);

    // flags at 84 = 0 (clean)
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 84, 4), 0);

    // log_blocks block_run at 88: AG=0, start=LogStartBlock, len=LogBlocks
    WriteBlockAsRun(image, off + 88, LogStartBlock, LogBlocks);

    // log_start at 96 = log_end (clean journal: no pending transactions)
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 96, 8), 0);
    // log_end at 104
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 104, 8), 0);

    // magic3 at 112
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 112, 4), Magic3);

    // root_dir block_run at 116
    WriteBlockAsRun(image, off + 116, this._rootDirInodeBlock, 1);

    // indices_dir block_run at 124
    WriteBlockAsRun(image, off + 124, this._indicesInodeBlock, 1);
  }

  private static void WriteAgBitmap(SparseBlockImage image, long usedBlocks, int bitmapBlocks) {
    var bitmapOffset = (long)AgBitmapBlock * BlockSize;
    var capacity = (long)bitmapBlocks * BlockSize * 8;
    if (usedBlocks > capacity)
      throw new InvalidOperationException(
        $"BFS: {usedBlocks:N0} used blocks need more than the {bitmapBlocks} bitmap blocks reserved.");

    // Set bits 0..usedBlocks-1 as allocated (1 = used), MSB-first within each byte
    // (the BFS convention). A whole byte at a time keeps a multi-gigabyte volume's
    // bitmap from costing a pass per block.
    var wholeBytes = usedBlocks / 8;
    for (var b = 0L; b < wholeBytes; ++b)
      image[bitmapOffset + b] = 0xFF;
    for (var i = wholeBytes * 8; i < usedBlocks; ++i)
      image[bitmapOffset + i / 8] |= (byte)(1 << (int)(7 - i % 8));
  }

  private static void WriteDirectoryInode(SparseBlockImage image, long inodeBlock, long btreeBlock,
      long parentBlock, int inodeNum, int leafCount = 1) {
    var off = inodeBlock * BlockSize;

    // magic1
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off, 4), InodeMagic);

    // inode_num (block_run): AG=0, start=inodeBlock, len=1
    WriteBlockAsRun(image, off + 4, inodeBlock, 1);

    // uid=0, gid=0
    // mode = S_IFDIR | S_IRWXU
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 20, 4), S_IFDIR | S_IRWXU);

    // flags: BEFS_INODE_IN_USE. Left at zero, an inode is one the driver walks
    // past — "inode is not used - inode = 11" — because a BFS inode's slot and
    // the inode being live are two different things.
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 24, 4), InodeInUse);
    // create_time at 28 = 0
    // last_modified_time at 36 = 0

    // parent (block_run) at 44
    WriteBlockAsRun(image, off + 44, parentBlock, parentBlock > 0 ? 1 : 0);

    // attributes (block_run) at 52 = 0,0,0
    // type at 60 = 0 (directory)
    // inode_size at 64
    BinaryPrimitives.WriteInt32LittleEndian(image.At(off + 64, 4), InodeSize);

    // data_stream at 72: one run covering the tree's header and its leaves.
    var streamBlocks = 1 + leafCount;
    var streamBytes = (long)streamBlocks * BlockSize;
    WriteBlockAsRun(image, off + InodeDataStreamOffset, btreeBlock, streamBlocks);

    // max_direct_range at 72 + 96 = 168
    BinaryPrimitives.WriteInt64LittleEndian(
      image.At(off + InodeDataStreamOffset + NumDirectBlocks * 8, 8), streamBytes);

    // size at 72 + 136 = 208
    BinaryPrimitives.WriteInt64LittleEndian(
      image.At(off + InodeDataStreamOffset + 136, 8), streamBytes);
  }

  private static void WriteDirBtreeLeaf(SparseBlockImage image, TreeNode dir) {
    // Children are already ordered (SortedDictionary, ordinal). The BFS B+ tree
    // requires entries sorted by key; this provides a stable, sorted order. When
    // they do not all fit in one 1024-byte leaf the entries spill across several
    // leaf blocks chained through left_link/right_link; the reader follows
    // right_link from the first leaf to read every entry.
    var partitions = PartitionEntries(dir);
    var leafBlocks = dir.LeafBlocks;

    for (var i = 0; i < partitions.Count; i++) {
      var leftLink = i > 0 ? leafBlocks[i - 1] : -1L;
      var rightLink = i < partitions.Count - 1 ? leafBlocks[i + 1] : -1L;
      WriteBtreeLeaf(image, leafBlocks[i], partitions[i], leftLink, rightLink);
    }
  }

  /// <summary>
  /// The header a B+ tree stream begins with: what the tree is, how big its nodes
  /// are, and where the root node sits inside the stream.
  /// </summary>
  /// <remarks>
  /// Offsets in a BFS tree are measured from the start of the stream, not in
  /// blocks of the volume — so the root node, which follows this header, is at
  /// one node's width in.
  /// </remarks>
  private static void WriteBtreeSuper(SparseBlockImage image, long superBlock, int leafCount) {
    var off = superBlock * BlockSize;
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off, 4), BplusLeafMagic);        // magic
    BinaryPrimitives.WriteInt32LittleEndian(image.At(off + 4, 4), BlockSize);          // node_size
    BinaryPrimitives.WriteInt32LittleEndian(image.At(off + 8, 4), 1);                  // max_depth
    BinaryPrimitives.WriteInt32LittleEndian(image.At(off + 12, 4), 0);                 // data_type: string
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 16, 8), BlockSize);         // root_node_ptr
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 24, 8), -1L);               // free_node_ptr
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 32, 8),
      (long)(1 + leafCount) * BlockSize);                                              // max_size
  }

  private static void WriteBtreeLeaf(SparseBlockImage image, int btreeBlock, List<(string Name, long InodeBlock)> entries, long leftLink, long rightLink) {
    var off = btreeBlock * BlockSize;

    // BFS B+ tree header (node header):
    // Per Giampaolo's book (p.170-175):
    //   0: left_link (i64) = -1 for no link
    //   8: right_link (i64) = -1 for no link
    //  16: overflow_link (i64) = -1 for leaf
    //  24: all_key_count (u16) = number of keys
    //  26: all_key_length (u16) = total bytes of all key strings
    //  28: (padding/reserved)

    // B+ tree node header. left_link/right_link chain sibling leaves so the
    // reader can walk every leaf of an over-large directory in key order.
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off, 8), leftLink);      // left_link
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 8, 8), rightLink); // right_link
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 16, 8), -1L);      // overflow_link
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 24, 2), (ushort)entries.Count); // all_key_count
    // all_key_length computed below

    // After the 28-byte header:
    // Key length table: u16[key_count] — cumulative length of keys (each entry = total length so far)
    // Then: key data (concatenated names, no separators)
    // Then: value table (from the end of the block): u64[key_count] — inode addresses
    //   Each value = block_run encoded as i64 (AG << 48 | start << 32 | ... or simply the
    //   block offset reinterpreted). Actually per Haiku source, values are off_t = i64.
    //   For directories: value = inode block offset within the AG shifted by ag_shift etc.
    //   Simplest: value = (AG << (ag_shift + 16)) | (start << 16) | length
    //   Actually Haiku stores it as an off_t that can be converted to a block_run:
    //     off_t = ((off_t)run.AllocationGroup() << shiftValue) | (off_t)run.Start()
    //   where shiftValue = blocks_per_ag_shift + 16 (for the block_run bit layout)
    //   But for a single-AG image with AG=0: off_t = start_block (since AG << X = 0)

    // The body of a node runs: the keys themselves, then — rounded up to an
    // eight-byte boundary — the index of where each one ends, then the values.
    // This used to put the index first and the values at the far end of the
    // block, so the driver read key data as an index and came out with a key
    // "of size 16942".
    const int keyLenAlign = 8;
    var headerSize = BtreeLeafHeaderSize;

    var keyBytes = new List<byte>();
    var cumulativeLengths = new List<ushort>();
    foreach (var (name, _) in entries) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      keyBytes.AddRange(nameBytes);
      cumulativeLengths.Add((ushort)keyBytes.Count);
    }

    var keyDataOffset = off + headerSize;
    var indexStart = headerSize + keyBytes.Count;
    var padding = indexStart % keyLenAlign;
    if (padding != 0) indexStart += keyLenAlign - padding;
    var keyLenTableOffset = off + indexStart;
    var keyLenTableSize = entries.Count * 2;               // u16 per key
    var valuesSize = entries.Count * 8;                    // u64 per value
    var valuesStart = keyLenTableOffset + keyLenTableSize;

    var totalUsed = indexStart + keyLenTableSize + valuesSize;
    if (totalUsed > BlockSize)
      throw new InvalidOperationException(
        $"BFS B+ tree leaf overflow: {totalUsed} bytes needed but block size is {BlockSize} (partitioning invariant violated).");

    // Write all_key_length
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 26, 2), (ushort)keyBytes.Count);

    // Write the key-length index (each entry is where that key ends)
    for (var i = 0; i < cumulativeLengths.Count; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(image.At(keyLenTableOffset + i * 2, 2), cumulativeLengths[i]);

    // Write key data
    image.Write(keyDataOffset, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(keyBytes));

    // Values follow the index, one per key, in the same order.
    for (var i = 0; i < entries.Count; i++) {
      // off_t = block number for single-AG (AG=0, shift irrelevant when AG=0)
      BinaryPrimitives.WriteInt64LittleEndian(image.At(valuesStart + i * 8, 8), entries[i].InodeBlock);
    }
  }

  private static void WriteEmptyBtreeLeaf(SparseBlockImage image, long block) {
    var off = block * BlockSize;
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off, 8), -1L);      // left_link
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 8, 8), -1L);  // right_link
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + 16, 8), -1L); // overflow_link
    // all_key_count = 0, all_key_length = 0 — already zero
  }

  private static void WriteFileInode(SparseBlockImage image, long inodeBlock, long parentInodeBlock,
    long dataStartBlock, long dataBlocks, long fileSize, long indirectBlock, int indirectBlockCount) {
    var off = inodeBlock * BlockSize;

    // magic1
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off, 4), InodeMagic);

    // inode_num (block_run)
    WriteBlockAsRun(image, off + 4, inodeBlock, 1);

    // uid=0, gid=0
    // mode = S_IFREG | S_IRWXU
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 20, 4), S_IFREG | S_IRWXU);

    // flags: BEFS_INODE_IN_USE. Left at zero, an inode is one the driver walks
    // past — "inode is not used - inode = 11" — because a BFS inode's slot and
    // the inode being live are two different things.
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 24, 4), InodeInUse);
    // create_time at 28 = 0
    // last_modified_time at 36 = 0

    // parent = containing directory inode
    WriteBlockAsRun(image, off + 44, parentInodeBlock, 1);

    // inode_size at 64
    BinaryPrimitives.WriteInt32LittleEndian(image.At(off + 64, 4), InodeSize);

    // data_stream at 72. The file's blocks are contiguous, but a run neither
    // crosses an allocation group nor exceeds its u16 length, so the span is split
    // into runs: the first twelve go in direct[], and the rest into an indirect
    // block holding a plain array of block_runs.
    if (dataBlocks > 0) {
      var runs = SplitIntoRuns(dataStartBlock, dataBlocks);
      var direct = Math.Min(NumDirectBlocks, runs.Count);
      long directBlocks = 0;
      for (var i = 0; i < direct; ++i) {
        WriteBlockAsRun(image, off + InodeDataStreamOffset + i * 8, runs[i].Block, runs[i].Length);
        directBlocks += runs[i].Length;
      }
      BinaryPrimitives.WriteInt64LittleEndian(image.At(off + InodeDataStreamOffset + NumDirectBlocks * 8, 8),
        directBlocks * BlockSize);

      if (runs.Count > direct) {
        if (indirectBlockCount <= 0)
          throw new InvalidOperationException(
            $"BFS: {runs.Count} data runs need an indirect block that was not reserved.");
        WriteBlockAsRun(image, off + InodeDataStreamOffset + NumDirectBlocks * 8 + 8,
          indirectBlock, indirectBlockCount);

        long indirectBlocks = 0;
        for (var i = direct; i < runs.Count; ++i) {
          var slot = i - direct;
          var block = indirectBlock + slot / RunsPerBlock;
          var within = slot % RunsPerBlock * 8;
          WriteBlockAsRun(image, (int)(block * BlockSize + within), runs[i].Block, runs[i].Length);
          indirectBlocks += runs[i].Length;
        }
        BinaryPrimitives.WriteInt64LittleEndian(
          image.At(off + InodeDataStreamOffset + NumDirectBlocks * 8 + 16, 8),
          (directBlocks + indirectBlocks) * BlockSize);
      }
    }

    // size at offset 72 + 136 = 208
    BinaryPrimitives.WriteInt64LittleEndian(image.At(off + InodeDataStreamOffset + 136, 8), fileSize);
  }

  /// <summary>
  /// Writes a block_run (allocation_group: u32, start: u16, length: u16) at the given offset.
  /// </summary>
  /// <summary>
  /// Writes a block_run for the absolute block <paramref name="block" />: a run
  /// carries its allocation group in a u32 and its start as a u16 within that
  /// group, so the group index is the block number's high bits.
  /// </summary>
  private static void WriteBlockAsRun(SparseBlockImage image, long offset, long block, long length) {
    var ag = block / BlocksPerAg;
    var start = block % BlocksPerAg;
    if (ag > uint.MaxValue)
      throw new InvalidOperationException($"BFS: block {block} is past the addressable range.");
    WriteBlockRun(image, offset, (uint)ag, (int)start, (int)length);
  }

  /// <summary>
  /// Splits a contiguous span of blocks into the runs a data stream can express:
  /// no run crosses an allocation-group boundary, and none exceeds the u16 length
  /// a block_run carries.
  /// </summary>
  private static List<(long Block, int Length)> SplitIntoRuns(long firstBlock, long blockCount) {
    var runs = new List<(long Block, int Length)>();
    var block = firstBlock;
    var remaining = blockCount;
    while (remaining > 0) {
      var toAgEnd = BlocksPerAg - block % BlocksPerAg;
      var take = (int)Math.Min(Math.Min(remaining, toAgEnd), MaxRunBlocks);
      runs.Add((block, take));
      block += take;
      remaining -= take;
    }
    return runs;
  }

  private static void WriteBlockRun(SparseBlockImage image, long offset, uint ag, int start, int length) {
    // A block_run addresses its start and length as uint16 within one allocation
    // group. Truncating either produced a volume that listed its files and
    // returned the wrong bytes, and the guard that was meant to catch it never
    // fired because the call sites cast to ushort first — so the callers pass the
    // untruncated values and this check is live.
    if (start > ushort.MaxValue || length > ushort.MaxValue)
      throw new InvalidOperationException(
        $"BFS writer: a block_run's start and length are uint16 within its allocation group, so " +
        $"neither can exceed {ushort.MaxValue} (got start={start}, length={length}).");

    BinaryPrimitives.WriteUInt32LittleEndian(image.At(offset, 4), ag);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(offset + 4, 2), (ushort)start);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(offset + 6, 2), (ushort)length);
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// Builds a complete OCFS2 (Oracle Cluster Filesystem 2) image from scratch in the
/// single-node "local" (non-clustered) variant — the layout the reference
/// <c>mkfs.ocfs2 -M local -N 1</c> produces with feature set
/// <c>local | extended-slotmap | inline-data | append-dio</c> (incompat 0x8148)
/// and <c>strict-journal-super</c> (compat 0x2). No metaecc, so the per-block
/// <c>ocfs2_block_check</c> (CRC32C + ECC) stays zero. The Linux kernel driver
/// mounts the result and <c>fsck.ocfs2 -fn</c> passes it, before and after a
/// kernel read-write mount.
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
/// The global_inode_alloc group at block 4 owns blocks 4..17; every system dinode
/// is a bit within it. Heartbeat, journal and slot_map data follow; then the
/// per-slot inode_alloc groups, which own lost+found and all user dinodes;
/// finally user file and directory data.
///
/// <para>The global bitmap is split into cluster groups of
/// <see cref="ClustersPerGroup"/> clusters, as mkfs does: group 0's descriptor
/// sits at block 3, group <c>k</c>'s at cluster <c>k * ClustersPerGroup</c>,
/// where it is the group's own first (allocated) bit. No extent covers a group
/// descriptor, so a file's data is split into one extent record per group it
/// crosses.</para>
/// </summary>
internal sealed class Ocfs2Writer {

  private readonly List<(string Name, byte[] Data, FilePayload Payload)> _files = [];

  /// <summary>Volume label written into <c>s_label</c> (64-byte field, NUL-padded). Capped at 63 ASCII bytes.</summary>
  private string _label = "OCFS2VOL";

  internal const int BlockSize = 4096;
  internal const int ClusterSize = 4096;
  internal const int BlockSizeBits = 12;
  internal const int ClusterSizeBits = 12;

  /// <summary>
  /// Free bits guaranteed in the per-slot inode groups so files can be added later.
  /// </summary>
  /// <remarks>
  /// Groups are whole (<see cref="InodeGroupBits"/> wide), so this only decides
  /// when one more group is laid out: a volume whose last group would keep fewer
  /// free bits than this gets another.
  /// </remarks>
  internal const int SpareInodeBits = 32;

  /// <summary>
  /// Bits per per-slot inode group (<c>cl_cpg</c> of <c>inode_alloc:NNNN</c>):
  /// 1024 at 4 KiB blocks, as measured on a <c>mkfs.ocfs2</c> volume.
  /// </summary>
  internal const int InodeGroupBits = 1024;

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
  // The cluster allocation bitmap of group 0 lives in the global_bitmap group
  // descriptor (block 3) at byte offset BitmapInGroupOffset within that block.
  internal const int BitmapDataBlkno = GlobalBitmapGroupBlkno; // block 3 (group desc holds bg_bitmap)
  internal const int BitmapInGroupOffset = 0x40;               // ocfs2_group_desc.bg_bitmap
  internal const int FirstFileBlkno = TruncateLogBlkno + 1;    // first non-system block

  // Tunables (kept small but spec-valid; fsck does not enforce mkfs minimums).
  private const int HeartbeatClusters = 1;   // mkfs uses 256; a local volume never heartbeats

  /// <summary>
  /// The smallest journal the kernel mounts: <c>OCFS2_MIN_JOURNAL_SIZE</c>
  /// (4 MiB, <c>fs/ocfs2/ocfs2_fs.h</c>). <c>ocfs2_journal_init</c> refuses a
  /// smaller <c>journal:NNNN</c> with "Journal file size (…) is too small", and
  /// fsck.ocfs2 does not check it — a 64 KiB journal passed fsck and never
  /// mounted. 4 MiB is also <c>JBD2_MIN_JOURNAL_BLOCKS</c> (1024) at 4 KiB blocks.
  /// </summary>
  internal const long MinJournalBytes = 4L * 1024 * 1024;

  /// <summary>
  /// Journal size for a volume of <paramref name="volumeBytes"/>, stepped as
  /// <c>mkfs.ocfs2 1.8.7 -M local -b 4096 -C 4096</c> was measured to pick it:
  /// 4 MiB below 128 MiB, 16 MiB below 1 GiB, 64 MiB from there on (mkfs grows
  /// past 64 MiB only from 16 GiB; the kernel asks for no more than the minimum).
  /// </summary>
  internal static int JournalClustersFor(long volumeBytes) {
    var bytes = volumeBytes switch {
      < 128L << 20 => MinJournalBytes,
      < 1L << 30 => 16L << 20,
      _ => 64L << 20,
    };
    return (int)(bytes / ClusterSize);
  }

  private static readonly byte[] SuperSignature = "OCFSV2"u8.ToArray();
  private static readonly byte[] InodeSignature = "INODE01"u8.ToArray();
  private static readonly byte[] GroupSignature = "GROUP01"u8.ToArray();

  // What tells one volume from another: its identity, the number every structure
  // in it is stamped with, and the moment it was made. mkfs draws all three fresh
  // per volume, so a volume that always reports the same three is one no mkfs made.
  private uint _fsGeneration = NewGeneration();
  private byte[] _uuid = NewUuid();
  private ulong _mkTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

  /// <summary>Fixes the volume's identity and creation time, for a build that has to come out the same twice.</summary>
  /// <param name="uuid">The volume's identity, sixteen bytes.</param>
  /// <param name="generation">The number stamped on the volume's structures.</param>
  /// <param name="createdAt">When the volume claims it was made.</param>
  public void SetIdentity(ReadOnlySpan<byte> uuid, uint generation, DateTimeOffset createdAt) {
    if (uuid.Length != 16)
      throw new ArgumentException("An OCFS2 volume identity is sixteen bytes.", nameof(uuid));

    this._uuid = uuid.ToArray();
    this._fsGeneration = generation;
    this._mkTime = (ulong)createdAt.ToUnixTimeSeconds();
  }

  private static byte[] NewUuid() {
    var uuid = new byte[16];
    System.Security.Cryptography.RandomNumberGenerator.Fill(uuid);
    return uuid;
  }

  private static uint NewGeneration() {
    Span<byte> bytes = stackalloc byte[4];
    System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
    return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
  }

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
  internal const int ChainListCount = 243;

  // ocfs2_dinode.id1.bitmap1 lives at byte 0xB8 (i_used) / 0xBC (i_total).
  private const int Id1UsedOffset = 0xB8;
  private const int Id1TotalOffset = 0xBC;

  /// <summary>
  /// Clusters per global-bitmap group (<c>cl_cpg</c>) at 4 KB blocks: a group
  /// descriptor's <c>bg_bitmap</c> holds (blocksize − 0x40) × 8 bits. mkfs.ocfs2
  /// uses the same 32256, and places group <c>k</c>'s descriptor at cluster
  /// <c>k × 32256</c>.
  /// </summary>
  internal const int ClustersPerGroup = (BlockSize - 0x40) * 8; // 32256

  private long _minimumBlocks;

  /// <summary>
  /// Makes the volume at least <paramref name="bytes"/> long, the rest free space
  /// for files added later — what choosing a device size gives <c>mkfs.ocfs2</c>.
  /// A volume is never made smaller than its contents need.
  /// </summary>
  public void SetMinimumSize(long bytes) {
    ArgumentOutOfRangeException.ThrowIfNegative(bytes);
    this._minimumBlocks = bytes / BlockSize;
  }

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
    _files.Add((normalized, data, FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the layout is settled from
  /// it before a byte is read.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    var normalized = name.Replace('\\', '/').Trim('/');
    if (string.IsNullOrEmpty(Path.GetFileName(normalized)))
      throw new ArgumentException("File name must not be empty.", nameof(name));
    _files.Add((normalized, [], FilePayload.FromStream(size, openStream)));
  }

  /// <summary>A run of clusters: where it starts and how many it covers.</summary>
  private readonly record struct Run(long Start, long Clusters);

  /// <summary>A chain-allocator group: its descriptor block, its bits, and how many are taken.</summary>
  private readonly record struct Group(long Blkno, int Bits, int Used);

  /// <summary>A node in the directory tree assembled from the added file paths.</summary>
  private sealed class TreeNode {
    public required string Name;
    public bool IsDir;
    public byte[] Data = [];
    /// <summary>A file's content, which may be produced on demand.</summary>
    public FilePayload Payload = FilePayload.Empty;
    public readonly Dictionary<string, TreeNode> Children = new(StringComparer.Ordinal);
    public readonly List<TreeNode> Order = [];

    // Layout assignment.
    public long DinodeBlkno;
    public long ParentBlkno;
    /// <summary>The clusters holding the node's data, in file order; empty when inline.</summary>
    public List<Run> Runs = [];
    public long DataClusters;
    public int InodeAllocBit; // bit index within its inode_alloc group
  }

  private TreeNode BuildTree() {
    var root = new TreeNode { Name = "", IsDir = true };
    foreach (var (path, data, payload) in _files) {
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
        if (isLeaf && !child.IsDir) { child.Data = data; child.Payload = payload; }
        cur = child;
      }
    }
    return root;
  }

  // ───────────────────────── cluster allocation ─────────────────────────

  /// <summary>
  /// Hands out clusters front to back, never covering a global-bitmap group
  /// descriptor, and remembers every run it handed out — those, and nothing
  /// else, are what the bitmap marks used.
  /// </summary>
  private sealed class ClusterCursor {
    private readonly List<Run> _allocated = [];

    public ClusterCursor(long reservedPrefix) {
      this._allocated.Add(new Run(0, reservedPrefix));
      this.Next = reservedPrefix;
    }

    /// <summary>The first cluster not yet handed out.</summary>
    public long Next { get; private set; }

    public IReadOnlyList<Run> Allocated => this._allocated;

    internal static bool IsGroupDescriptor(long cluster) => cluster >= ClustersPerGroup && cluster % ClustersPerGroup == 0;
    private static long NextDescriptor(long cluster) => (cluster / ClustersPerGroup + 1) * ClustersPerGroup;

    /// <summary>A run of <paramref name="count"/> clusters that lies inside one group.</summary>
    public long Contiguous(long count) {
      if (count <= 0 || count >= ClustersPerGroup)
        throw new InvalidOperationException($"OCFS2: a contiguous run of {count} clusters does not fit one cluster group.");
      if (IsGroupDescriptor(this.Next)) ++this.Next;
      if (this.Next + count > NextDescriptor(this.Next)) this.Next = NextDescriptor(this.Next) + 1;
      var start = this.Next;
      this.Take(start, count);
      return start;
    }

    /// <summary>
    /// <paramref name="count"/> clusters as consecutive runs, split where a group
    /// descriptor sits and where an extent record's 16-bit length runs out.
    /// </summary>
    public List<Run> Runs(long count) {
      var runs = new List<Run>();
      while (count > 0) {
        if (IsGroupDescriptor(this.Next)) ++this.Next;
        var take = Math.Min(count, Math.Min(NextDescriptor(this.Next) - this.Next, ushort.MaxValue));
        runs.Add(new Run(this.Next, take));
        this.Take(this.Next, take);
        count -= take;
      }
      return runs;
    }

    private void Take(long start, long count) {
      var last = this._allocated[^1];
      if (last.Start + last.Clusters == start)
        this._allocated[^1] = last with { Clusters = last.Clusters + count };
      else
        this._allocated.Add(new Run(start, count));
      this.Next = start + count;
    }
  }

  // ───────────────────────── layout plan ─────────────────────────

  private sealed class Plan {
    public long TotalBlocks;
    public int InodeAllocGroupBits;   // bg_bits of the global_inode_alloc group (owns blocks 4..)
    public long HeartbeatData;        // first block of heartbeat data
    public int JournalClusters;
    public long JournalData;          // first block of journal data
    public long SlotMapData;          // slot_map data block
    public List<Group> InodeGroups = null!;   // inode_alloc:0000 groups
    public long LostFoundBlkno;
    public List<Group> ClusterGroups = null!; // global_bitmap groups
    public IReadOnlyList<Run> Allocated = null!;
    public TreeNode Root = null!;
    public List<TreeNode> Dirs = null!;
    public List<TreeNode> Files = null!;
  }

  private Plan BuildPlan() {
    // The journal scales with the volume and the volume contains the journal:
    // lay out with the smallest, and again with whatever the result asks for,
    // until the two agree (monotone, so at most a couple of passes).
    var journal = JournalClustersFor(0);
    for (;;) {
      var plan = this.BuildPlan(journal);
      var wanted = JournalClustersFor(plan.TotalBlocks * ClusterSize);
      if (wanted <= journal) return plan;
      journal = wanted;
    }
  }

  private Plan BuildPlan(int journalClusters) {
    var root = BuildTree();
    root.DinodeBlkno = RootDirBlkno;

    var dirs = new List<TreeNode>();
    var files = new List<TreeNode>();
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

    // Blocks 0..17: reserved, superblock, global bitmap group 0 descriptor and
    // the global_inode_alloc group, which owns every system dinode.
    var cursor = new ClusterCursor(FirstFileBlkno);
    var heartbeatData = cursor.Contiguous(HeartbeatClusters);
    var journalData = cursor.Contiguous(journalClusters);
    var slotMapData = cursor.Contiguous(1);

    // Per-slot inode groups. Inode n (lost+found is 0) is bit 1 + n % 1023 of
    // group n / 1023 — bit 0 of every group is its own descriptor. Every group
    // is a whole cl_cpg wide: fsck.ocfs2 takes a group to own cl_cpg clusters
    // whatever its bg_bits says, and the kernel grows the allocator by that
    // much, so a shorter last group reads as clusters past the end of the volume.
    var inodeCount = 1 + userInodes.Count;
    var groupCount = (inodeCount + SpareInodeBits + InodeGroupBits - 2) / (InodeGroupBits - 1);
    var groupStarts = new List<(long Blkno, int Bits)>(groupCount);
    for (var g = 0; g < groupCount; ++g)
      groupStarts.Add((cursor.Contiguous(InodeGroupBits), InodeGroupBits));
    var perGroup = InodeGroupBits - 1;
    (long Blkno, int Bit) InodeAt(int n) => (groupStarts[n / perGroup].Blkno + 1 + n % perGroup, 1 + n % perGroup);
    var inodeGroups = new List<Group>(groupStarts.Count);
    for (var g = 0; g < groupStarts.Count; ++g) {
      var inGroup = Math.Clamp(inodeCount - g * perGroup, 0, perGroup);
      inodeGroups.Add(new Group(groupStarts[g].Blkno, groupStarts[g].Bits, 1 + inGroup));
    }

    var lostFoundBlkno = InodeAt(0).Blkno;
    for (var i = 0; i < userInodes.Count; i++)
      (userInodes[i].DinodeBlkno, userInodes[i].InodeAllocBit) = InodeAt(1 + i);

    // Parent back-references.
    void SetParents(TreeNode node) {
      foreach (var c in node.Order) {
        c.ParentBlkno = node.DinodeBlkno;
        if (c.IsDir) SetParents(c);
      }
    }
    SetParents(root);

    // Files: inline-small files keep their bytes in the dinode; larger ones get
    // clusters, one extent record per run.
    foreach (var f in files) {
      if (f.Payload.Size <= MaxInline) continue;
      f.DataClusters = (f.Payload.Size + ClusterSize - 1) / ClusterSize;
      f.Runs = cursor.Runs(f.DataClusters);
      if (f.Runs.Count > ExtentListCount)
        throw new NotSupportedException(
          $"OCFS2: '{f.Name}' needs {f.Runs.Count} extent records, more than the {ExtentListCount} a dinode holds " +
          "without an extent tree, which this writer does not build.");
    }

    // Directories that overflow the inline area become extent-backed. The root
    // also carries a lost+found entry, so size it with that included.
    foreach (var dir in dirs.Prepend(root)) {
      var isRoot = dir.DinodeBlkno == RootDirBlkno;
      var inlineLen = InlineDirLength(dir, extraLostFound: isRoot, lostFoundBlkno: lostFoundBlkno);
      if (inlineLen <= MaxInline) continue;
      var blocks = BuildExtentDirBlocks(dir, isRoot ? lostFoundBlkno : 0);
      dir.Data = blocks;
      dir.DataClusters = blocks.Length / ClusterSize;
      dir.Runs = cursor.Runs(dir.DataClusters);
      if (dir.Runs.Count > ExtentListCount)
        throw new NotSupportedException($"OCFS2: directory '{dir.Name}' needs more extent records than a dinode holds.");
    }

    // A little tail of free space, as mkfs always leaves slack — or the size asked for.
    var totalBlocks = Math.Max(Math.Max(cursor.Next + 8, 64), this._minimumBlocks);

    var clusterGroups = new List<Group>();
    for (long first = 0; first < totalBlocks; first += ClustersPerGroup) {
      var bits = (int)Math.Min(ClustersPerGroup, totalBlocks - first);
      var blkno = first == 0 ? GlobalBitmapGroupBlkno : first;
      clusterGroups.Add(new Group(blkno, bits, Used: 0)); // counted from the bitmap when written
    }

    return new Plan {
      TotalBlocks = totalBlocks,
      InodeAllocGroupBits = SystemDinodeCount,
      HeartbeatData = heartbeatData,
      JournalClusters = journalClusters,
      JournalData = journalData,
      SlotMapData = slotMapData,
      InodeGroups = inodeGroups,
      LostFoundBlkno = lostFoundBlkno,
      ClusterGroups = clusterGroups,
      Allocated = cursor.Allocated,
      Root = root,
      Dirs = dirs,
      Files = files,
    };
  }

  /// <summary>Builds the OCFS2 image and returns the raw bytes.</summary>
  public byte[] Build() {
    var plan = BuildPlan();
    var image = this.BuildCore(plan, out var payloads);
    return payloads.Materialise(image);
  }

  /// <summary>
  /// Writes the volume into <paramref name="output" />: the blocks the filesystem
  /// populates, then each file's bytes at the block it was allocated. Only a
  /// non-seekable target has to materialise the volume, so a seekable one is
  /// bounded by the disk rather than by what a byte[] can address.
  /// </summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = this.Build();
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    var image = this.BuildCore(this.BuildPlan(), out var payloads);
    image.WriteTo(output);
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + image.TotalBytes;
    output.Flush();
  }

  private SparseBlockImage BuildCore(Plan plan, out DeferredPayloads payloads) {
    // Only the blocks the filesystem populates are held: file payloads are
    // placed by seek afterwards, so a volume past what a byte[] can address
    // costs its metadata rather than its size.
    var image = new SparseBlockImage(BlockSize, plan.TotalBlocks * BlockSize);
    payloads = new DeferredPayloads();

    WriteSuperblock(image, plan);
    WriteGlobalBitmap(image, plan);
    WriteGlobalInodeAlloc(image, plan);
    WriteRootDir(image, plan);
    WriteSystemDir(image);
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
    foreach (var dir in plan.Dirs.Prepend(plan.Root)) {
      long consumed = 0;
      foreach (var run in dir.Runs) {
        image.Write(run.Start * BlockSize, dir.Data.AsSpan((int)(consumed * ClusterSize), (int)(run.Clusters * ClusterSize)));
        consumed += run.Clusters;
      }
    }

    foreach (var f in plan.Files) {
      WriteFileDinode(image, f);
      long offset = 0;
      foreach (var run in f.Runs) {
        var length = Math.Min(run.Clusters * ClusterSize, f.Payload.Size - offset);
        payloads.Add(run.Start * BlockSize, Slice(f.Payload, offset, length));
        offset += length;
      }
    }

    return image;
  }

  /// <summary>The <paramref name="length"/> bytes of a payload that start at <paramref name="offset"/>.</summary>
  private static FilePayload Slice(FilePayload payload, long offset, long length) {
    if (offset == 0 && length == payload.Size) return payload;
    if (payload.Data is { } bytes && offset + length <= Array.MaxLength)
      return FilePayload.FromStream(length, () => new MemoryStream(bytes, (int)offset, (int)length, writable: false));
    return FilePayload.FromStream(length, () => {
      var s = payload.Open();
      if (s.CanSeek) s.Position = offset;
      else {
        var skip = new byte[64 * 1024];
        for (var left = offset; left > 0;) {
          var n = s.Read(skip, 0, (int)Math.Min(skip.Length, left));
          if (n <= 0) break;
          left -= n;
        }
      }
      return s;
    });
  }

  // ───────────────────────── dinode header ─────────────────────────

  private void WriteDinodeHeader(
      SparseBlockImage image, long blkno, uint mode, uint flags, long size, ushort links,
      int suballocSlot, int suballocBit) {
    var off = blkno * BlockSize;
    InodeSignature.CopyTo(image.At(off, InodeSignature.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x08, 4), this._fsGeneration);
    BinaryPrimitives.WriteInt16LittleEndian(image.At(off + 0x0C, 2), (short)suballocSlot);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x0E, 2), (ushort)suballocBit);

    var clusters = size > 0 ? (uint)((size + ClusterSize - 1) / ClusterSize) : 0;
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x14, 4), clusters);

    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x20, 8), (ulong)size);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x28, 2), (ushort)mode);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x2A, 2), links);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x2C, 4), flags);

    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x30, 8), this._mkTime); // atime
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x38, 8), this._mkTime); // ctime
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x40, 8), this._mkTime); // mtime

    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x50, 8), (ulong)blkno);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x60, 4), this._fsGeneration);
  }

  /// <summary>Writes a leaf extent list holding one record per run, numbered in file order.</summary>
  private static void WriteExtentList(SparseBlockImage image, long blkno, IReadOnlyList<Run> runs) {
    SetExtentListHeader(image, blkno, ExtentListCount, (ushort)runs.Count);
    long cpos = 0;
    for (var i = 0; i < runs.Count; ++i) {
      SetExtentRecord(image, blkno, i, (uint)cpos, (ushort)runs[i].Clusters, runs[i].Start);
      cpos += runs[i].Clusters;
    }
  }

  private static void SetExtentRecord(SparseBlockImage image, long blkno, int recIdx, uint cpos, ushort clusters, long dataBlkno) {
    var rec = blkno * BlockSize + Id2Offset + ListHeaderLen + recIdx * 16;
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(rec + 0, 4), cpos);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(rec + 4, 2), clusters);
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(rec + 8, 8), (ulong)dataBlkno);
  }

  private static void SetExtentListHeader(SparseBlockImage image, long blkno, ushort count, ushort nextFree) {
    var off = blkno * BlockSize + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0, 2), 0);        // l_tree_depth
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 2, 2), count);    // l_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 4, 2), nextFree); // l_next_free_rec
  }

  // l_count for a leaf extent list inline in a dinode: (4096-0xC0-0x10)/16 = 243.
  internal const int ExtentListCount = 243;

  // ───────────────────────── superblock ─────────────────────────

  private void WriteSuperblock(SparseBlockImage image, Plan plan) {
    WriteDinodeHeader(image, SuperBlockBlkno, 0, FlValid | FlSystem | FlSuperBlock, 0, 0, -1, 0xFFFF);
    var dinodeOff = SuperBlockBlkno * BlockSize;
    image.At(dinodeOff, 8).Clear();
    SuperSignature.CopyTo(image.At(dinodeOff, SuperSignature.Length));

    // i_clusters = total clusters; i_blkno stays 2; i_size 0.
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(dinodeOff + 0x14, 4), (uint)plan.TotalBlocks);

    var off = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x00, 2), 0);    // s_major_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x02, 2), 90);   // s_minor_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x06, 2), 20);   // s_max_mnt_count
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x10, 8), this._mkTime); // s_lastcheck
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x1C, 4), 0x0002);   // s_feature_compat: JBD2_SB
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x20, 4), 0x8148);   // s_feature_incompat: append-dio|extended-slotmap|inline-data|local
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x24, 4), 0);        // s_feature_ro_compat
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x28, 8), RootDirBlkno);
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x30, 8), SystemDirBlkno);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x38, 4), BlockSizeBits);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x3C, 4), ClusterSizeBits);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x40, 2), 1);        // s_max_slots
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x48, 8), GlobalBitmapGroupBlkno); // s_first_cluster_group
    // s_label[64] @ +0x50 — NUL-padded ASCII. The field is zeroed by the
    // image allocation, so a short label leaves the tail clean for the reader.
    var labelBytes = Encoding.ASCII.GetBytes(this._label);
    labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 64)).CopyTo(image.At(off + 0x50, 64));
    this._uuid.CopyTo(image.At(off + 0x90, 16)); // s_uuid
  }

  // ───────────────────────── chain allocators ─────────────────────────

  /// <summary>
  /// Writes an <c>ocfs2_group_desc</c> (GROUP01) header at <paramref name="blkno"/>
  /// whose <c>bg_bitmap</c> has already been filled in, counting the free bits
  /// (and the longest free run) from that bitmap. Returns the free count.
  /// </summary>
  private int WriteGroupDesc(SparseBlockImage image, long blkno, int bits, int chain, long nextGroup, long parentInode) {
    var off = blkno * BlockSize;
    GroupSignature.CopyTo(image.At(off, GroupSignature.Length));
    var bitmap = image.At(off + BitmapInGroupOffset, BlockSize - BitmapInGroupOffset);
    int free = 0, contig = 0, run = 0;
    for (var i = 0; i < bits; ++i) {
      if ((bitmap[i >> 3] & (1 << (i & 7))) != 0) { run = 0; continue; }
      ++free;
      contig = Math.Max(contig, ++run);
    }

    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x08, 2), (ushort)(BlockSize - BitmapInGroupOffset)); // bg_size
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x0A, 2), (ushort)bits);          // bg_bits
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x0C, 2), (ushort)free);          // bg_free_bits_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x0E, 2), (ushort)chain);         // bg_chain
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(off + 0x10, 4), this._fsGeneration);    // bg_generation
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x14, 2), (ushort)contig);        // bg_contig_free_bits
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x18, 8), (ulong)nextGroup);      // bg_next_group
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x20, 8), (ulong)parentInode);    // bg_parent_dinode
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(off + 0x28, 8), (ulong)blkno);          // bg_blkno
    return free;
  }

  /// <summary>Marks bits <c>[0, used)</c> of a group's bitmap.</summary>
  private static void SetLeadingBits(SparseBlockImage image, long groupBlkno, int used) {
    var bmp = groupBlkno * BlockSize + BitmapInGroupOffset;
    for (var i = 0; i < used; i++)
      image[bmp + (i >> 3)] |= (byte)(1 << (i & 7));
  }

  /// <summary>
  /// Writes a chain allocator: its dinode's bitmap totals and chain list, and the
  /// headers of its groups. Group <c>g</c> joins chain <c>g % 243</c>, linked to
  /// the next group of that chain through <c>bg_next_group</c>, as mkfs spreads
  /// them. The groups' bitmaps must already be filled in.
  /// </summary>
  private void WriteChainAllocator(SparseBlockImage image, long dinodeBlkno, int clustersPerGroup, IReadOnlyList<Group> groups, long sizeBytes) {
    WriteDinodeHeader(image, dinodeBlkno, ModeFile, FlValid | FlSystem | FlBitmap | FlChain,
      sizeBytes, 1, -1, (int)(dinodeBlkno - InodeAllocGroupBlkno));

    var chains = Math.Min(groups.Count, ChainListCount);
    var chainFree = new long[chains];
    var chainTotal = new long[chains];
    long usedTotal = 0, bitsTotal = 0;
    for (var g = 0; g < groups.Count; ++g) {
      var chain = g % ChainListCount;
      var next = g + ChainListCount < groups.Count ? groups[g + ChainListCount].Blkno : 0;
      var free = WriteGroupDesc(image, groups[g].Blkno, groups[g].Bits, chain, next, dinodeBlkno);
      chainFree[chain] += free;
      chainTotal[chain] += groups[g].Bits;
      usedTotal += groups[g].Bits - free;
      bitsTotal += groups[g].Bits;
    }

    var dinodeOff = dinodeBlkno * BlockSize;
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(dinodeOff + Id1UsedOffset, 4), (uint)usedTotal);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(dinodeOff + Id1TotalOffset, 4), (uint)bitsTotal);

    var ch = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x00, 2), (ushort)clustersPerGroup); // cl_cpg
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x02, 2), 1);                        // cl_bpc
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x04, 2), ChainListCount);           // cl_count
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x06, 2), (ushort)chains);           // cl_next_free_rec
    for (var c = 0; c < chains; ++c) {
      var rec = ch + ListHeaderLen + c * 16;
      BinaryPrimitives.WriteUInt32LittleEndian(image.At(rec + 0x00, 4), (uint)chainFree[c]);  // c_free
      BinaryPrimitives.WriteUInt32LittleEndian(image.At(rec + 0x04, 4), (uint)chainTotal[c]); // c_total
      BinaryPrimitives.WriteUInt64LittleEndian(image.At(rec + 0x08, 8), (ulong)groups[c].Blkno); // c_blkno
    }
  }

  private void WriteGlobalBitmap(SparseBlockImage image, Plan plan) {
    // A set bit is an allocated cluster: every run handed out, plus each group's
    // own descriptor — group 0's lies inside the reserved prefix already.
    void Mark(long cluster) {
      var group = cluster / ClustersPerGroup;
      var blkno = group == 0 ? GlobalBitmapGroupBlkno : group * ClustersPerGroup;
      var bit = cluster % ClustersPerGroup;
      image[blkno * BlockSize + BitmapInGroupOffset + (bit >> 3)] |= (byte)(1 << (int)(bit & 7));
    }
    foreach (var run in plan.Allocated)
      for (var c = run.Start; c < run.Start + run.Clusters; ++c)
        Mark(c);
    for (var g = 1; g < plan.ClusterGroups.Count; ++g)
      Mark(g * (long)ClustersPerGroup);

    WriteChainAllocator(image, GlobalBitmapBlkno, ClustersPerGroup, plan.ClusterGroups, plan.TotalBlocks * ClusterSize);
  }

  private void WriteGlobalInodeAlloc(SparseBlockImage image, Plan plan) {
    // The group descriptor IS block 4 and counts as bit 0; bits 1..13 are the
    // system dinodes at blocks 5..17, all in use.
    var bits = plan.InodeAllocGroupBits;
    SetLeadingBits(image, InodeAllocGroupBlkno, SystemDinodeCount);
    WriteChainAllocator(image, GlobalInodeAllocBlkno, bits, [new Group(InodeAllocGroupBlkno, bits, SystemDinodeCount)],
      (long)bits * BlockSize);
  }

  private void WritePerSlotInodeAlloc(SparseBlockImage image, Plan plan) {
    foreach (var g in plan.InodeGroups)
      SetLeadingBits(image, g.Blkno, g.Used);
    WriteChainAllocator(image, InodeAllocBlkno, InodeGroupBits, plan.InodeGroups,
      plan.InodeGroups.Sum(g => (long)g.Bits) * BlockSize);
  }

  private void WriteExtentAlloc(SparseBlockImage image) {
    // Empty chain allocator for extent blocks.
    WriteDinodeHeader(image, ExtentAllocBlkno, ModeFile, FlValid | FlSystem | FlBitmap | FlChain, 0, 1, -1, ExtentAllocBlkno - InodeAllocGroupBlkno);
    var ch = ExtentAllocBlkno * BlockSize + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x00, 2), 1024); // cl_cpg
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x02, 2), 1);    // cl_bpc
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x04, 2), ChainListCount);
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(ch + 0x06, 2), 0);    // cl_next_free_rec = empty
  }

  // ───────────────────────── system directories & files ─────────────────────────

  private void WriteRootDir(SparseBlockImage image, Plan plan) {
    var node = plan.Root;
    var links = (ushort)(2 + node.Order.Count(c => c.IsDir) + 1); // +1 for lost+found

    if (node.DataClusters > 0) {
      WriteExtentDir(image, node, RootDirBlkno, node.DataClusters * ClusterSize, links,
        FlValid | FlSystem, RootDirBlkno - InodeAllocGroupBlkno);
      return;
    }
    var inline = BuildInlineDir(node, extraLostFound: true, lostFoundBlkno: plan.LostFoundBlkno);
    WriteInlineDir(image, RootDirBlkno, inline, links, FlValid | FlSystem, RootDirBlkno - InodeAllocGroupBlkno);
  }

  private void WriteSystemDir(SparseBlockImage image) {
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

  private void WriteBadBlocks(SparseBlockImage image) {
    WriteDinodeHeader(image, BadBlocksBlkno, ModeFile, FlValid | FlSystem, 0, 1, -1, BadBlocksBlkno - InodeAllocGroupBlkno);
    SetExtentListHeader(image, BadBlocksBlkno, ExtentListCount, 0);
  }

  private void WriteSlotMap(SparseBlockImage image, Plan plan) {
    // slot_map is a regular file with one cluster of data; extended-slotmap means
    // the data is ocfs2_extended_slot[] — all zero (no node mounted) is valid.
    WriteDinodeHeader(image, SlotMapBlkno, ModeFile, FlValid | FlSystem, ClusterSize, 1, -1, SlotMapBlkno - InodeAllocGroupBlkno);
    WriteExtentList(image, SlotMapBlkno, [new Run(plan.SlotMapData, 1)]);
  }

  private void WriteHeartbeat(SparseBlockImage image, Plan plan) {
    var size = (long)HeartbeatClusters * ClusterSize;
    WriteDinodeHeader(image, HeartbeatBlkno, ModeFile, FlValid | FlSystem | FlHeartbeat, size, 1, -1, HeartbeatBlkno - InodeAllocGroupBlkno);
    WriteExtentList(image, HeartbeatBlkno, [new Run(plan.HeartbeatData, HeartbeatClusters)]);
  }

  private void WriteOrphanDir(SparseBlockImage image) {
    var entries = new List<(long Inode, string Name, byte Type)> {
      (OrphanDirBlkno, ".", FtDir),
      (SystemDirBlkno, "..", FtDir),
    };
    var inline = BuildDirEntriesFillingInline(entries);
    WriteInlineDir(image, OrphanDirBlkno, inline, 2, FlValid | FlSystem, OrphanDirBlkno - InodeAllocGroupBlkno);
  }

  private void WriteJournal(SparseBlockImage image, Plan plan) {
    var size = (long)plan.JournalClusters * ClusterSize;
    WriteDinodeHeader(image, JournalBlkno, ModeFile, FlValid | FlSystem | FlJournal, size, 1, -1, JournalBlkno - InodeAllocGroupBlkno);
    WriteExtentList(image, JournalBlkno, [new Run(plan.JournalData, plan.JournalClusters)]);

    // JBD2 journal superblock (big-endian) in the first journal data block, as
    // mkfs.ocfs2 leaves it: a clean journal, s_start = 0 — anything else asks
    // the kernel to replay a log that was never written.
    var jb = plan.JournalData * BlockSize;
    BinaryPrimitives.WriteUInt32BigEndian(image.At(jb + 0x00, 4), 0xC03B3998u); // h_magic
    BinaryPrimitives.WriteUInt32BigEndian(image.At(jb + 0x04, 4), 4);            // h_blocktype = JBD2_SUPERBLOCK_V2
    // h_sequence @0x08 = 0
    BinaryPrimitives.WriteUInt32BigEndian(image.At(jb + 0x0C, 4), BlockSize);    // s_blocksize
    BinaryPrimitives.WriteUInt32BigEndian(image.At(jb + 0x10, 4), (uint)plan.JournalClusters); // s_maxlen
    BinaryPrimitives.WriteUInt32BigEndian(image.At(jb + 0x14, 4), 1);            // s_first
    BinaryPrimitives.WriteUInt32BigEndian(image.At(jb + 0x18, 4), 1);            // s_sequence
    // s_start @0x1C = 0: nothing to recover
    this._uuid.CopyTo(image.At(jb + 0x30, 16));                                  // s_uuid
    BinaryPrimitives.WriteUInt32BigEndian(image.At(jb + 0x40, 4), 1);            // s_nr_users
  }

  private void WriteLocalAlloc(SparseBlockImage image) {
    WriteDinodeHeader(image, LocalAllocBlkno, ModeFile, FlValid | FlSystem | FlLocalAlloc | FlBitmap, 0, 1, -1, LocalAllocBlkno - InodeAllocGroupBlkno);
    var off = LocalAllocBlkno * BlockSize + Id2Offset;
    // ocfs2_local_alloc header is 16 bytes (la_bm_off u32 + la_size u16 +
    // la_reserved1 u16 + la_reserved2 u64); la_bitmap follows.
    var laSize = BlockSize - Id2Offset - 16; // 3888
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x04, 2), (ushort)laSize);
  }

  private void WriteTruncateLog(SparseBlockImage image) {
    WriteDinodeHeader(image, TruncateLogBlkno, ModeFile, FlValid | FlSystem | FlDealloc, 0, 1, -1, TruncateLogBlkno - InodeAllocGroupBlkno);
    var off = TruncateLogBlkno * BlockSize + Id2Offset;
    // ocfs2_truncate_log: tl_count @0x00 = max records that fit, tl_used @0x02 = 0.
    var tlCount = (BlockSize - Id2Offset - 8) / 8; // (4096-0xC0-8)/sizeof(rec=8)
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off + 0x00, 2), (ushort)tlCount);
  }

  private void WriteLostFound(SparseBlockImage image, Plan plan) {
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

  private void WriteDirDinode(SparseBlockImage image, TreeNode node) {
    var links = (ushort)(2 + node.Order.Count(c => c.IsDir));
    if (node.DataClusters > 0) {
      WriteExtentDir(image, node, node.DinodeBlkno, node.DataClusters * ClusterSize, links, FlValid, node.InodeAllocBit, slot: 0);
      return;
    }
    var inline = BuildInlineDir(node);
    WriteInlineDirWithSlot(image, node.DinodeBlkno, inline, links, FlValid, 0, node.InodeAllocBit);
  }

  private void WriteFileDinode(SparseBlockImage image, TreeNode f) {
    // The real size: clamping it to the inline limit sent every large file down
    // the inline branch, where its bytes do not exist.
    var size = f.Payload.Size;
    WriteDinodeHeader(image, f.DinodeBlkno, ModeFile, FlValid, size, 1, 0, f.InodeAllocBit);
    if (size <= MaxInline) {
      var dinodeOff = f.DinodeBlkno * BlockSize;
      // Inline files keep their bytes in the dinode → i_clusters must be 0.
      BinaryPrimitives.WriteUInt32LittleEndian(image.At(dinodeOff + 0x14, 4), 0);
      BinaryPrimitives.WriteUInt16LittleEndian(image.At(dinodeOff + DynFeaturesOffset, 2), DynInlineData);
      var off = dinodeOff + Id2Offset;
      BinaryPrimitives.WriteUInt16LittleEndian(image.At(off, 2), MaxInline); // id_count
      // From the payload, not the byte[]: a streamed entry carries no bytes, and
      // an inline file is at most MaxInline long so reading it costs nothing.
      if (size > 0) image.Write(off + InlineHeaderLen, f.Payload.ToArray().AsSpan(0, (int)size));
      return;
    }
    WriteExtentList(image, f.DinodeBlkno, f.Runs);
  }

  private void WriteExtentDir(SparseBlockImage image, TreeNode node, long blkno, long size, ushort links, uint flags, int bit, int slot = -1) {
    WriteDinodeHeader(image, blkno, ModeDir, flags, size, links, slot, bit);
    WriteExtentList(image, blkno, node.Runs);
  }

  private void WriteInlineDir(SparseBlockImage image, long blkno, byte[] inline, ushort links, uint flags, int bit) =>
    WriteInlineDirWithSlot(image, blkno, inline, links, flags, -1, bit);

  private void WriteInlineDirWithSlot(SparseBlockImage image, long blkno, byte[] inline, ushort links, uint flags, int slot, int bit) {
    WriteDinodeHeader(image, blkno, ModeDir, flags, MaxInline, links, slot, bit);
    var dinodeOff = blkno * BlockSize;
    // i_size for inline dirs == id_count (full inline area), matching mkfs.
    BinaryPrimitives.WriteUInt64LittleEndian(image.At(dinodeOff + 0x20, 8), MaxInline);
    BinaryPrimitives.WriteUInt32LittleEndian(image.At(dinodeOff + 0x14, 4), 0); // i_clusters = 0
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(dinodeOff + DynFeaturesOffset, 2), DynInlineData);
    var off = dinodeOff + Id2Offset;
    BinaryPrimitives.WriteUInt16LittleEndian(image.At(off, 2), MaxInline); // id_count
    if (inline.Length > MaxInline)
      throw new InvalidOperationException("Inline dir overflow not planned as extent-backed.");
    if (inline.Length > 0) image.Write(off + InlineHeaderLen, inline);
  }

  // ───────────────────────── directory entry builders ─────────────────────────

  /// <summary>Packed byte length of a set of dir entries (no inline stretch).</summary>
  private static int InlineDirByteLength(List<(long Inode, string Name, byte Type)> entries) {
    var total = 0;
    foreach (var (_, name, _) in entries)
      total += (12 + Encoding.UTF8.GetByteCount(name) + 3) & ~3;
    return total;
  }

  /// <summary>
  /// Builds inline dir-entry bytes that EXACTLY fill the inline area (MaxInline):
  /// the final entry's rec_len is stretched to consume the remaining space, which
  /// is what mkfs and the kernel do for inline directories.
  /// </summary>
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

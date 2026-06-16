#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// Writes a UFS1 (FreeBSD FFS) filesystem image that faithfully reproduces a
/// <c>newfs -O1</c> layout: multiple cylinder groups, per-group superblock
/// backups, a root directory plus a <c>.snap</c> directory (exactly as newfs
/// emits), and direct-block-plus-single-indirect file extents.
/// <para>
/// All on-disk structures (superblock <c>struct fs</c>, cylinder-group header
/// <c>struct cg</c>, and <c>ufs1_dinode</c>) use the exact field offsets defined
/// in FreeBSD's <c>sys/ufs/ffs/fs.h</c> and <c>sys/ufs/ufs/dinode.h</c>. The
/// primary superblock lives at <c>SBLOCK_UFS1 = 8192</c>; each cylinder group
/// carries a backup at <c>cgbase(cg) + fs_sblkno</c>. Free-block / free-inode
/// bitmaps, the fragment-summary array, cluster summaries, and the per-group
/// <c>cs_*</c> summary records are populated so that <c>fsck_ffs -f -n</c> passes
/// all five phases cleanly.
/// </para>
/// </summary>
public sealed class UfsWriter {
  // ── fixed UFS1 geometry (newfs -O1 -b 8192 -f 1024 defaults) ─────────────
  internal const int SuperblockOffset = 8192;     // SBLOCK_UFS1 (byte offset)
  internal const int SuperblockSize = 1376;       // sizeof(struct fs)
  internal const int SbWriteSize = 2048;          // fs_sbsize (sector-aligned)
  internal const int BlockSize = 8192;            // fs_bsize
  internal const int FragSize = 1024;             // fs_fsize
  internal const int Frag = BlockSize / FragSize; // fs_frag = 8
  internal const int Ufs1Magic = 0x00011954;      // FS_UFS1_MAGIC
  internal const int CgMagic = 0x00090255;        // CG_MAGIC
  internal const int InodeSize = 128;             // sizeof(ufs1_dinode)
  internal const int InodesPerBlock = BlockSize / InodeSize; // fs_inopb = 64
  internal const int RootIno = 2;
  internal const int SnapIno = 3;                 // newfs emits /.snap as inode 3
  internal const int FirstUserIno = 4;
  internal const int MaxDirectBlocks = 12;        // UFS_NDADDR
  internal const int PointersPerBlock = BlockSize / 4; // single-indirect fan-out (2048)
  internal const int ContigSumSize = 16;          // fs_contigsumsize
  internal const int MaxContig = 128;             // fs_maxcontig

  // newfs places metadata at constant frag offsets for this block/frag pairing.
  internal const int SblkNo = 16;                 // fs_sblkno  (superblock backup)
  internal const int CblkNo = 24;                 // fs_cblkno  (cg header)
  internal const int IblkNo = 32;                 // fs_iblkno  (inode table)

  internal const int NumCylinderGroups = 4;       // newfs small-fs cylinder-group count
  internal const int MinImageBytes = 16 * 1024 * 1024; // 16 MB floor (matches default tests)

  internal static readonly int FsMagicOffset = SuperblockSize - 4; // 1372

  private readonly List<(string Name, byte[] Data, long? StreamingSize, Func<Stream>? StreamOpener)> _files = [];
  private readonly byte[] _volumeUuid = Guid.NewGuid().ToByteArray();

  /// <summary>
  /// Streaming-allocations side-effect: when non-null, every streaming file's
  /// (absolute byte offset of its first data fragment, logical size, opener) is
  /// appended for use by <see cref="BuildToStreaming"/>'s post-stream pass.
  /// When null the writer behaves exactly as before.
  /// </summary>
  private List<(long ByteOffset, long Size, Func<Stream> Opener)>? _streamingSink;

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name, data, null, null));
  }

  /// <summary>
  /// Adds a streaming file: <paramref name="size"/> drives image sizing and
  /// fragment allocation in pass 1; bytes are pulled from
  /// <paramref name="openStream"/> in pass 2 of <see cref="BuildToStreaming"/>.
  /// Never buffered as <c>byte[]</c>.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "size must be >= 0.");
    _files.Add((name, System.Array.Empty<byte>(), size, openStream));
  }

  // ── derived geometry for a chosen total size ─────────────────────────────
  private sealed record class Geometry(
    int TotalFrags, int Ncg, int Fpg, int Ipg, int Dblkno, int Bpg,
    int CsAddr, int CsSizeBytes, int CgSizeBytes
  );

  private static int RoundUp(int a, int b) => (a + b - 1) / b * b;
  private static int HowMany(int a, int b) => (a + b - 1) / b;

  // newfs sizing: ncg = 4 (small-fs heuristic), fpg = roundup(size/ncg, frag)+frag,
  // ipg = inopb * inode-blocks-per-cg where blocks-per-cg = (bpg-1)/16 + 1.
  private static Geometry ComputeGeometry(int totalFrags) {
    var ncg = NumCylinderGroups;
    var fpg = RoundUp(HowMany(totalFrags, ncg), Frag) + Frag;
    var bpg = fpg / Frag;                                   // blocks per group
    var inodeBlocksPerCg = (bpg - 1) / 16 + 1;
    var ipg = InodesPerBlock * inodeBlocksPerCg;            // inodes per group
    var dblkno = IblkNo + inodeBlocksPerCg * Frag;          // first data frag in a cg
    var csSizeBytes = RoundUp(ncg * 16, FragSize);          // fs_cssize (cs records)
    // fs_cgsize = roundup(cg header + bitmaps to a sector), capped at one block.
    var iusedoff = 168 + 4 + 2;
    var freeoff = iusedoff + HowMany(ipg, 8);
    var clustersumoff = RoundUp(freeoff + HowMany(fpg, 8) - 4, 4);
    var clusteroff = clustersumoff + (ContigSumSize + 1) * 4;
    var nextfreeoff = clusteroff + HowMany(fpg / Frag, 8);
    var cgSizeBytes = Math.Min(BlockSize, RoundUp(nextfreeoff, FragSize));
    return new Geometry(totalFrags, ncg, fpg, ipg, dblkno, bpg, dblkno, csSizeBytes, cgSizeBytes);
  }

  // ── in-memory directory tree ─────────────────────────────────────────────
  private sealed class TreeNode {
    public string Name = "";
    public bool IsDirectory;
    public byte[] Data = [];
    public long? StreamingSize;
    public Func<Stream>? StreamOpener;
    public int Inode;
    public TreeNode? Parent;
    public readonly Dictionary<string, TreeNode> Children = new(StringComparer.Ordinal);
    public readonly List<TreeNode> Order = [];

    // Logical byte length: declared streaming size for a streaming entry, else
    // the buffered byte[] length. Drives fragment allocation and di_size.
    public long EffectiveLength => this.StreamingSize ?? this.Data.Length;
  }

  private TreeNode BuildTree() {
    var root = new TreeNode { IsDirectory = true, Name = "" };
    foreach (var (rawName, data, streamingSize, streamOpener) in _files) {
      var parts = rawName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0) continue;
      var cursor = root;
      for (var i = 0; i < parts.Length; i++) {
        var part = parts[i];
        var isLeaf = i == parts.Length - 1;
        if (cursor.Children.TryGetValue(part, out var existing)) {
          if (isLeaf && !existing.IsDirectory) { existing.Data = data; existing.StreamingSize = streamingSize; existing.StreamOpener = streamOpener; }
          cursor = existing;
          continue;
        }
        var node = new TreeNode {
          Name = part, IsDirectory = !isLeaf, Data = isLeaf ? data : [], Parent = cursor,
          StreamingSize = isLeaf ? streamingSize : null,
          StreamOpener = isLeaf ? streamOpener : null,
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
    var disk = BuildDisk();
    output.Write(disk);
  }

  /// <summary>
  /// Two-pass streaming Build: pass 1 derives image geometry from the declared
  /// sizes of <see cref="AddStreamingFile"/> entries and emits the full disk
  /// image (superblock, cylinder groups, inodes, directories) with the streaming
  /// files' data fragments left zero; pass 2 seeks to each file's first data
  /// fragment and streams its bytes from the factory in 64 KB chunks. The output
  /// is byte-identical to <see cref="WriteTo"/> for the same inputs — only WHERE
  /// the file-data bytes come from differs. The UFS <c>cs</c> records are
  /// cylinder-group free-space summaries, not content checksums, so streaming the
  /// data fragments in afterward is byte-safe.
  /// </summary>
  /// <param name="output">A writable, seekable target stream.</param>
  public void BuildToStreaming(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek || !output.CanWrite)
      throw new ArgumentException("BuildToStreaming requires a writable, seekable stream.", nameof(output));

    var sink = new List<(long ByteOffset, long Size, Func<Stream> Opener)>();
    this._streamingSink = sink;
    byte[] disk;
    try {
      disk = BuildDisk();
    } finally {
      this._streamingSink = null;
    }

    output.SetLength(disk.Length);
    output.Position = 0;
    output.Write(disk, 0, disk.Length);

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
    }
    output.Flush();
  }

  private byte[] BuildDisk() {
    var root = BuildTree();
    root.Inode = RootIno;

    // ── inode assignment (root=2, .snap=3, then directories and files) ──────
    // .snap is a synthetic newfs directory that always lives in the root and is
    // listed first (inode 3), exactly as newfs emits it.
    var snap = new TreeNode { Name = ".snap", IsDirectory = true, Parent = root, Inode = SnapIno };
    root.Children[snap.Name] = snap;
    root.Order.Insert(0, snap);

    var directories = new List<TreeNode> { root, snap };
    var regularFiles = new List<TreeNode>();
    var nextIno = FirstUserIno;
    var queue = new Queue<TreeNode>();
    queue.Enqueue(root);
    while (queue.Count > 0) {
      var dir = queue.Dequeue();
      foreach (var child in dir.Order) {
        if (child == snap) continue;             // already assigned inode 3 + listed
        child.Inode = nextIno++;
        if (child.IsDirectory) { directories.Add(child); queue.Enqueue(child); } else regularFiles.Add(child);
      }
    }

    // ── pick image size; grow if user data needs more than the 16 MB floor ──
    var imageBytes = (int)Math.Max(MinImageBytes, EstimateBytes(directories, regularFiles));
    imageBytes = RoundUp(imageBytes, BlockSize);
    var totalFrags = imageBytes / FragSize;
    var geom = ComputeGeometry(totalFrags);
    var disk = new byte[imageBytes];

    // The inode table and data area live inside cylinder group 0; all live
    // inodes (root, .snap, user tree) fit comfortably in cg0's first ipg slots.
    var inodeTableOffset = IblkNo * FragSize;

    // ── data-frag allocation inside cg0 ─────────────────────────────────────
    // newfs lays out: [meta 0..dblkno) | csum block | root dir | .snap dir | ...
    // The csum block occupies one block (block-aligned at dblkno). Each directory
    // and file starts on a fs-block boundary; a small object only fills the
    // fragments it needs but the next object still starts block-aligned.
    var nextFrag = geom.Dblkno + Frag;             // skip the csum block
    _placedCg0Frags.Clear();

    // Render every directory's on-disk byte image (DIRBLKSIZ-chunk packed).
    var dirImage = new Dictionary<TreeNode, byte[]>();
    foreach (var dir in directories) {
      var parentIno = dir.Parent?.Inode ?? RootIno;
      var img = BuildDirectoryImage(dir, parentIno);
      EnsureDirectoryAddressable(dir, HowMany(img.Length, BlockSize));
      dirImage[dir] = img;
    }
    const int MaxAddressableBlocks = MaxDirectBlocks + PointersPerBlock;
    foreach (var file in regularFiles) {
      var blocks = HowMany((int)file.EffectiveLength, BlockSize);
      if (blocks > MaxAddressableBlocks)
        throw new InvalidOperationException(
          $"UFS writer supports direct blocks plus one single-indirect block " +
          $"(max {(long)MaxAddressableBlocks * BlockSize} bytes per file); " +
          $"'{file.Name}' needs {file.EffectiveLength} bytes.");
    }

    // ── directories: lay out and emit inodes ────────────────────────────────
    foreach (var dir in directories) {
      var img = dirImage[dir];
      var first = nextFrag;
      var (directBlocks, indirectFrag, fragsUsed) = PlaceObject(disk, img, ref nextFrag);

      var childDirs = dir.Order.Count(c => c.IsDirectory);
      // di_dirdepth: distance from the root (root=0, its children=1, …). fsck
      // expects directory inodes to record their depth (UFS directory-depth
      // tracking); a missing depth provokes a Phase 5 "track directory depth" fix.
      var depth = 0;
      for (var n = dir.Parent; n != null; n = n.Parent) depth++;
      WriteUfs1Inode(disk, inodeTableOffset + dir.Inode * InodeSize,
        mode: dir == snap ? (uint)0x41FD : 0x41ED, nlink: (ushort)(2 + childDirs),
        size: (ulong)img.Length,
        blocksUsed512: (uint)((long)fragsUsed * FragSize / 512),
        directBlocks: directBlocks, indirectBlock: indirectFrag, dirDepth: (uint)depth);
      _ = first;
    }

    // ── file blocks + inodes ────────────────────────────────────────────────
    foreach (var file in regularFiles) {
      var firstFrag = nextFrag;
      var (directBlocks, indirectFrag, fragsUsed) = file.StreamOpener != null
        ? PlaceObjectGeometry(disk, (int)file.EffectiveLength, ref nextFrag)
        : PlaceObject(disk, file.Data, ref nextFrag);

      // Streaming entries leave their data fragments zero; BuildToStreaming
      // post-fills them from the source in 64 KB chunks. The data fragments are
      // contiguous from `firstFrag`, so the file's bytes occupy
      // [firstFrag*FragSize, +EffectiveLength).
      if (file.StreamOpener != null && this._streamingSink != null && file.EffectiveLength > 0)
        this._streamingSink.Add(((long)firstFrag * FragSize, file.EffectiveLength, file.StreamOpener));

      WriteUfs1Inode(disk, inodeTableOffset + file.Inode * InodeSize,
        mode: 0x81A4, nlink: 1, size: (ulong)file.EffectiveLength,
        blocksUsed512: (uint)((long)fragsUsed * FragSize / 512),
        directBlocks: directBlocks, indirectBlock: indirectFrag);
    }

    // The highest data frag consumed in cg0 (relative to image start).
    var cg0DataEndFrag = nextFrag;

    // ── per-cg headers + bitmaps and the cs summary block ───────────────────
    var dirCount = directories.Count;
    var liveInodes = nextIno;                      // inodes 0..(nextIno-1) used: 0,1 reserved + live tree
    WriteCylinderGroups(disk, geom, dirCount, liveInodes, cg0DataEndFrag);

    // ── superblock ──────────────────────────────────────────────────────────
    WriteSuperblock(disk, geom, dirCount, liveInodes, cg0DataEndFrag);

    return disk;
  }

  private static long EstimateBytes(List<TreeNode> directories, List<TreeNode> regularFiles) {
    long frags = 1024;                             // generous slack for metadata
    foreach (var d in directories) frags += Math.Max(1, d.Order.Count / 16 + 1) * Frag + Frag;
    foreach (var f in regularFiles) frags += (long)(HowMany((int)f.EffectiveLength, BlockSize) + 1) * Frag;
    return frags * FragSize;
  }

  // Writes an object's payload at the current allocation cursor (block-aligned),
  // recording the frags it touches, and returns its di_db[] direct pointers, the
  // single-indirect fragment (0 if none), and the fragment count for di_blocks.
  // Each fs block before the tail consumes Frag fragments; the tail consumes only
  // as many fragments as it needs (newfs fragment-tail optimisation).
  private (int[] DirectBlocks, int IndirectFrag, int FragsUsed) PlaceObject(byte[] disk, byte[] payload, ref int nextFrag) {
    var length = payload.Length;
    var blocks = Math.Max(1, HowMany(length, BlockSize));
    var fullBlocks = length / BlockSize;
    var tailBytes = length - fullBlocks * BlockSize;
    var tailFrags = tailBytes > 0 ? HowMany(tailBytes, FragSize) : (length == 0 ? 1 : 0);

    var first = nextFrag;
    if (length > 0) payload.CopyTo(disk, (long)first * FragSize);

    // Record the frags each block actually occupies (block-aligned addressing,
    // tail filling only tailFrags).
    var blockFrags = new int[blocks];
    var fragsUsed = 0;
    for (var b = 0; b < blocks; b++) {
      var blockBase = first + b * Frag;
      blockFrags[b] = blockBase;
      var fragsInBlock = b < fullBlocks ? Frag : tailFrags;
      for (var f = 0; f < fragsInBlock; f++) _placedCg0Frags.Add(blockBase + f);
      fragsUsed += fragsInBlock;
    }
    nextFrag += blocks * Frag;

    var directBlocks = new int[MaxDirectBlocks];
    for (var b = 0; b < blocks && b < MaxDirectBlocks; b++) directBlocks[b] = blockFrags[b];

    var indirectFrag = 0;
    if (blocks > MaxDirectBlocks) {
      indirectFrag = nextFrag;
      WriteIndirectBlock(disk, indirectFrag, blockFrags, MaxDirectBlocks);
      for (var f = 0; f < Frag; f++) _placedCg0Frags.Add(indirectFrag + f);
      nextFrag += Frag;
      fragsUsed += Frag;
    }
    return (directBlocks, indirectFrag, fragsUsed);
  }

  // Streaming-file geometry: replicates PlaceObject's fragment allocation and
  // direct/indirect block accounting for a file of `length` bytes WITHOUT copying
  // any payload (the data fragments stay zero — BuildToStreaming post-fills them).
  // Byte-for-byte identical placement to PlaceObject(disk, payloadOfLength, ...).
  private (int[] DirectBlocks, int IndirectFrag, int FragsUsed) PlaceObjectGeometry(byte[] disk, int length, ref int nextFrag) {
    var blocks = Math.Max(1, HowMany(length, BlockSize));
    var fullBlocks = length / BlockSize;
    var tailBytes = length - fullBlocks * BlockSize;
    var tailFrags = tailBytes > 0 ? HowMany(tailBytes, FragSize) : (length == 0 ? 1 : 0);

    var first = nextFrag;
    // No payload copy — fragments remain zero until BuildToStreaming streams them.

    var blockFrags = new int[blocks];
    var fragsUsed = 0;
    for (var b = 0; b < blocks; b++) {
      var blockBase = first + b * Frag;
      blockFrags[b] = blockBase;
      var fragsInBlock = b < fullBlocks ? Frag : tailFrags;
      for (var f = 0; f < fragsInBlock; f++) _placedCg0Frags.Add(blockBase + f);
      fragsUsed += fragsInBlock;
    }
    nextFrag += blocks * Frag;

    var directBlocks = new int[MaxDirectBlocks];
    for (var b = 0; b < blocks && b < MaxDirectBlocks; b++) directBlocks[b] = blockFrags[b];

    var indirectFrag = 0;
    if (blocks > MaxDirectBlocks) {
      indirectFrag = nextFrag;
      WriteIndirectBlock(disk, indirectFrag, blockFrags, MaxDirectBlocks);
      for (var f = 0; f < Frag; f++) _placedCg0Frags.Add(indirectFrag + f);
      nextFrag += Frag;
      fragsUsed += Frag;
    }
    return (directBlocks, indirectFrag, fragsUsed);
  }

  // Builds a directory's on-disk byte image: entries packed into DIRBLKSIZ
  // (512-byte) chunks; no entry crosses a chunk boundary; the last entry in each
  // chunk has its reclen extended to the chunk end. di_size = the image length.
  private static byte[] BuildDirectoryImage(TreeNode dir, int parentIno) {
    const int DirBlkSiz = 512;
    var chunks = new List<byte[]>();
    var chunk = new byte[DirBlkSiz];
    var pos = 0;
    var lastStart = 0;
    foreach (var e in EnumerateEntries(dir, parentIno)) {
      var reclen = DirEntryReclen(e.Name);
      if (pos + reclen > DirBlkSiz) {
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(lastStart + 4), (ushort)(DirBlkSiz - lastStart));
        chunks.Add(chunk);
        chunk = new byte[DirBlkSiz];
        pos = 0;
        lastStart = 0;
      }
      lastStart = pos;
      WriteDirEntry(chunk, ref pos, e.Inode, e.Name, e.Type);
    }
    BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(lastStart + 4), (ushort)(DirBlkSiz - lastStart));
    chunks.Add(chunk);

    var image = new byte[chunks.Count * DirBlkSiz];
    for (var i = 0; i < chunks.Count; i++) chunks[i].CopyTo(image, i * DirBlkSiz);
    return image;
  }

  // ── directory entries ─────────────────────────────────────────────────────
  private readonly record struct DirEntry(int Inode, string Name, byte Type);

  private static IEnumerable<DirEntry> EnumerateEntries(TreeNode dir, int parentIno) {
    yield return new DirEntry(dir.Inode, ".", 4);
    yield return new DirEntry(parentIno, "..", 4);
    foreach (var child in dir.Order)
      yield return new DirEntry(child.Inode, child.Name, child.IsDirectory ? (byte)4 : (byte)8);
  }

  private static void EnsureDirectoryAddressable(TreeNode dir, int blockCount) {
    var maxBlocks = MaxDirectBlocks + PointersPerBlock;
    if (blockCount > maxBlocks)
      throw new InvalidOperationException(
        $"UFS writer addresses a directory through {MaxDirectBlocks} direct blocks plus one " +
        $"single-indirect block (max {maxBlocks} blocks); directory " +
        $"'{(dir.Name.Length == 0 ? "/" : dir.Name)}' with {dir.Order.Count} entries needs {blockCount}.");
  }

  private static void WriteIndirectBlock(byte[] disk, int indirectFrag, int[] blockFrags, int firstIndirectBlock) {
    var tableOffset = (long)indirectFrag * FragSize;
    for (var b = firstIndirectBlock; b < blockFrags.Length; b++)
      BinaryPrimitives.WriteInt32LittleEndian(
        disk.AsSpan((int)(tableOffset + (long)(b - firstIndirectBlock) * 4)), blockFrags[b]);
  }

  private static int DirEntryReclen(string name) {
    var nameLen = Encoding.ASCII.GetByteCount(name);
    return (8 + nameLen + 3) & ~3;
  }

  // ── struct fs (superblock) ────────────────────────────────────────────────
  // Field offsets and values mirror the bytes a real `newfs -O1 -b8192 -f1024`
  // emits for this geometry (verified by byte-comparing a FreeBSD image).
  private void WriteSuperblock(byte[] disk, Geometry geom, int dirCount, int liveInodes, int cg0DataEndFrag) {
    var sb = disk.AsSpan(SuperblockOffset, SuperblockSize);
    sb.Clear();

    var totalFrags = geom.TotalFrags;
    var dsize = DataFrags(geom);
    var spc = geom.Fpg * (FragSize / 512);                // sectors per cylinder = fpg * nspf (nspf=2)
    var maxbpg = BlockSize / 4 / 2;                        // fs_maxbpg = 1024
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var freeBlocks = TotalFreeBlocks(geom, cg0DataEndFrag);
    var freeFrags = TotalFreeFrags(geom, cg0DataEndFrag);
    var freeInodes = geom.Ipg * geom.Ncg - liveInodes;

    BinaryPrimitives.WriteInt32LittleEndian(sb[8..], SblkNo);              // fs_sblkno
    BinaryPrimitives.WriteInt32LittleEndian(sb[12..], CblkNo);             // fs_cblkno
    BinaryPrimitives.WriteInt32LittleEndian(sb[16..], IblkNo);             // fs_iblkno
    BinaryPrimitives.WriteInt32LittleEndian(sb[20..], geom.Dblkno);        // fs_dblkno
    BinaryPrimitives.WriteInt32LittleEndian(sb[24..], 0);                  // fs_old_cgoffset
    BinaryPrimitives.WriteInt32LittleEndian(sb[28..], -1);                 // fs_old_cgmask
    BinaryPrimitives.WriteUInt32LittleEndian(sb[32..], now);               // fs_old_time
    BinaryPrimitives.WriteInt32LittleEndian(sb[36..], totalFrags);         // fs_old_size (frags)
    BinaryPrimitives.WriteInt32LittleEndian(sb[40..], dsize);              // fs_old_dsize (frags)
    BinaryPrimitives.WriteInt32LittleEndian(sb[44..], geom.Ncg);           // fs_ncg
    BinaryPrimitives.WriteInt32LittleEndian(sb[48..], BlockSize);          // fs_bsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[52..], FragSize);           // fs_fsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[56..], Frag);               // fs_frag
    BinaryPrimitives.WriteInt32LittleEndian(sb[60..], 8);                  // fs_minfree
    BinaryPrimitives.WriteInt32LittleEndian(sb[64..], 0);                  // fs_old_rotdelay
    BinaryPrimitives.WriteInt32LittleEndian(sb[68..], 60);                 // fs_old_rps
    BinaryPrimitives.WriteInt32LittleEndian(sb[72..], ~(BlockSize - 1));   // fs_bmask
    BinaryPrimitives.WriteInt32LittleEndian(sb[76..], ~(FragSize - 1));    // fs_fmask
    BinaryPrimitives.WriteInt32LittleEndian(sb[80..], 13);                 // fs_bshift
    BinaryPrimitives.WriteInt32LittleEndian(sb[84..], 10);                 // fs_fshift
    BinaryPrimitives.WriteInt32LittleEndian(sb[88..], MaxContig);          // fs_maxcontig
    BinaryPrimitives.WriteInt32LittleEndian(sb[92..], maxbpg);             // fs_maxbpg
    BinaryPrimitives.WriteInt32LittleEndian(sb[96..], 3);                  // fs_fragshift
    BinaryPrimitives.WriteInt32LittleEndian(sb[100..], 1);                 // fs_fsbtodb
    BinaryPrimitives.WriteInt32LittleEndian(sb[104..], SbWriteSize);       // fs_sbsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[116..], BlockSize / 4);     // fs_nindir
    BinaryPrimitives.WriteUInt32LittleEndian(sb[120..], InodesPerBlock);   // fs_inopb
    BinaryPrimitives.WriteInt32LittleEndian(sb[124..], 2);                 // fs_old_nspf
    BinaryPrimitives.WriteInt32LittleEndian(sb[128..], 0);                 // fs_optim (FS_OPTTIME)
    BinaryPrimitives.WriteInt32LittleEndian(sb[132..], spc);               // fs_old_cpc-area → spc value newfs stores
    BinaryPrimitives.WriteInt32LittleEndian(sb[136..], 1);                 // (newfs: 1)
    // fs_id[2] at 144 (filesystem id, time-derived). Keep deterministic-ish.
    BinaryPrimitives.WriteUInt32LittleEndian(sb[144..], now);              // fs_id[0]
    BinaryPrimitives.WriteUInt32LittleEndian(sb[148..], (uint)_volumeUuid[0] | ((uint)_volumeUuid[1] << 8) | ((uint)_volumeUuid[2] << 16) | ((uint)_volumeUuid[3] << 24)); // fs_id[1]
    BinaryPrimitives.WriteInt32LittleEndian(sb[152..], geom.CsAddr);       // fs_old_csaddr
    BinaryPrimitives.WriteInt32LittleEndian(sb[156..], geom.CsSizeBytes);  // fs_cssize
    BinaryPrimitives.WriteInt32LittleEndian(sb[160..], geom.CgSizeBytes);  // fs_cgsize
    BinaryPrimitives.WriteInt32LittleEndian(sb[168..], spc);               // fs_old_nsect
    BinaryPrimitives.WriteInt32LittleEndian(sb[172..], spc);               // fs_old_spc
    BinaryPrimitives.WriteInt32LittleEndian(sb[176..], geom.Ncg);          // fs_old_ncyl
    BinaryPrimitives.WriteInt32LittleEndian(sb[180..], 1);                 // fs_old_cpg
    BinaryPrimitives.WriteInt32LittleEndian(sb[184..], geom.Ipg);          // fs_ipg
    BinaryPrimitives.WriteInt32LittleEndian(sb[188..], geom.Fpg);          // fs_fpg
    // fs_old_cstotal at 192 (csum: ndir, nbfree, nifree, nffree)
    BinaryPrimitives.WriteInt32LittleEndian(sb[192..], dirCount);
    BinaryPrimitives.WriteInt32LittleEndian(sb[196..], freeBlocks);
    BinaryPrimitives.WriteInt32LittleEndian(sb[200..], freeInodes);
    BinaryPrimitives.WriteInt32LittleEndian(sb[204..], freeFrags);
    sb[208] = 0;                                                           // fs_fmod
    sb[209] = 1;                                                           // fs_clean
    sb[210] = 0;                                                           // fs_ronly
    sb[211] = 0x80;                                                        // fs_old_flags (FS_FLAGS_UPDATED)
    BinaryPrimitives.WriteInt32LittleEndian(sb[860..], BlockSize);         // fs_maxbsize
    BinaryPrimitives.WriteInt64LittleEndian(sb[872..], (long)totalFrags);  // fs_unrefs-area placeholder
    BinaryPrimitives.WriteInt32LittleEndian(sb[880..], 160);               // fs_metaspace
    _volumeUuid.CopyTo(sb[896..]);                                         // UUID in spare area
    BinaryPrimitives.WriteInt64LittleEndian(sb[992..], SuperblockOffset);  // newfs: 8192
    BinaryPrimitives.WriteInt64LittleEndian(sb[1000..], SuperblockOffset); // newfs: 8192
    // fs_cstotal (csum_total, 8 int64s) at 1008
    BinaryPrimitives.WriteInt64LittleEndian(sb[1008..], dirCount);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1016..], freeBlocks);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1024..], freeInodes);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1032..], freeFrags);
    BinaryPrimitives.WriteInt64LittleEndian(sb[1072..], now);              // fs_time
    BinaryPrimitives.WriteInt64LittleEndian(sb[1080..], totalFrags);       // fs_size (frags)
    BinaryPrimitives.WriteInt64LittleEndian(sb[1088..], dsize);            // fs_dsize (frags)
    BinaryPrimitives.WriteInt64LittleEndian(sb[1096..], geom.CsAddr);      // fs_csaddr
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1196..], 16384);           // fs_avgfilesize
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1200..], 64);              // fs_avgfpdir
    BinaryPrimitives.WriteInt32LittleEndian(sb[1316..], 16);               // fs_save_cgsize (newfs: 16)
    BinaryPrimitives.WriteInt32LittleEndian(sb[1320..], 60);               // fs_maxsymlinklen
    BinaryPrimitives.WriteInt32LittleEndian(sb[1324..], 2);                // fs_old_inodefmt (FS_44INODEFMT)
    BinaryPrimitives.WriteInt64LittleEndian(sb[1328..], 70403120791551L);  // fs_maxfilesize
    BinaryPrimitives.WriteInt64LittleEndian(sb[1336..], BlockSize - 1);    // fs_qbmask = ~fs_bmask
    BinaryPrimitives.WriteInt64LittleEndian(sb[1344..], FragSize - 1);     // fs_qfmask = ~fs_fmask
    BinaryPrimitives.WriteInt32LittleEndian(sb[1356..], 1);                // fs_old_postblformat (FS_DYNAMICPOSTBLFMT)
    BinaryPrimitives.WriteInt32LittleEndian(sb[1360..], 1);                // fs_old_nrpos
    // fs_magic — last int32 of struct fs at +1372.
    BinaryPrimitives.WriteInt32LittleEndian(sb[FsMagicOffset..], Ufs1Magic);
  }

  // fs_dsize: usable data frags. cg0 loses everything below dblkno; cg>0 keeps
  // the [0, sblkno) boot gap; the cs summary block costs one fragment.
  private static int DataFrags(Geometry geom) {
    var total = geom.TotalFrags - geom.Dblkno;                  // cg0 overhead
    for (var cg = 1; cg < geom.Ncg; cg++) total -= geom.Dblkno - SblkNo; // cg>0 overhead
    total -= geom.CsSizeBytes / FragSize;                       // cs summary fragment(s)
    return total;
  }

  private static int CgNdblk(Geometry geom, int cg) {
    var cgbase = cg * geom.Fpg;
    return Math.Min(geom.Fpg, geom.TotalFrags - cgbase);
  }

  // ── per-cg cluster/free accounting shared by the cg headers and the cs block ─
  private readonly record struct CgCounts(int Ndir, int Nbfree, int Nifree, int Nffree);

  private CgCounts ComputeCgCounts(Geometry geom, int cg, int dirCount, int liveInodes, int cg0DataEndFrag) {
    var ndblk = CgNdblk(geom, cg);
    var ndir = cg == 0 ? dirCount : 0;

    // Inodes: cg0 holds every live inode; other cgs are empty.
    int usedInodes;
    if (cg == 0) usedInodes = liveInodes;
    else usedInodes = 0;
    var nifree = geom.Ipg - usedInodes;

    // Build the per-cg used-frag map to count free whole blocks vs free frags.
    var used = new bool[ndblk];
    MarkCgMetadata(geom, cg, used);
    if (cg == 0) MarkCg0Data(geom, used, cg0DataEndFrag);

    var nbfree = 0;
    var nffree = 0;
    for (var blk = 0; blk * Frag < ndblk; blk++) {
      var baseFrag = blk * Frag;
      var freeInBlock = 0;
      var anyUsed = false;
      for (var f = 0; f < Frag && baseFrag + f < ndblk; f++) {
        if (used[baseFrag + f]) anyUsed = true; else freeInBlock++;
      }
      var blockComplete = baseFrag + Frag <= ndblk;
      if (!anyUsed && blockComplete) nbfree++;
      else nffree += freeInBlock;
    }
    return new CgCounts(ndir, nbfree, nifree, nffree);
  }

  // Marks the metadata frags (sb backup, cg header, inode table) of a cg as used.
  private static void MarkCgMetadata(Geometry geom, int cg, bool[] used) {
    // cg0: frags [0, dblkno) are all metadata (boot block + primary superblock +
    // backup + cg header + inode table). cg>0: the [0, sblkno) boot/label gap is
    // free data; only [sblkno, dblkno) is metadata. (Matches newfs/dumpfs.)
    var firstMeta = cg == 0 ? 0 : SblkNo;
    for (var f = firstMeta; f < geom.Dblkno && f < used.Length; f++) used[f] = true;
  }

  // Marks cg0's allocated data frags (csum summary fragments + directory/file data).
  private void MarkCg0Data(Geometry geom, bool[] used, int cg0DataEndFrag) {
    // The cs summary occupies howmany(cssize, fsize) FRAGMENTS at fs_csaddr; the
    // rest of that block stays free (newfs marks only the populated fragments).
    var csumFrags = HowMany(geom.CsSizeBytes, FragSize);
    for (var f = 0; f < csumFrags && geom.Dblkno + f < used.Length; f++) used[geom.Dblkno + f] = true;
    // Plus every fragment the layout actually placed for directories/files.
    foreach (var f in _placedCg0Frags) if (f < used.Length) used[f] = true;
    _ = cg0DataEndFrag;
  }

  // Fragments actually written into cg0's data region (besides metadata/csum).
  private readonly List<int> _placedCg0Frags = [];

  private void WriteCylinderGroups(byte[] disk, Geometry geom, int dirCount, int liveInodes, int cg0DataEndFrag) {
    // Write the cs summary block first (referenced by fs_csaddr): one cs record
    // (ndir, nbfree, nifree, nffree) per cylinder group, 16 bytes each.
    var csOffset = (long)geom.CsAddr * FragSize;
    for (var cg = 0; cg < geom.Ncg; cg++) {
      var c = ComputeCgCounts(geom, cg, dirCount, liveInodes, cg0DataEndFrag);
      // struct csum on-disk order: cs_ndir, cs_nbfree, cs_nifree, cs_nffree.
      var rec = (int)csOffset + cg * 16;
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(rec + 0), c.Ndir);
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(rec + 4), c.Nbfree);
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(rec + 8), c.Nifree);
      BinaryPrimitives.WriteInt32LittleEndian(disk.AsSpan(rec + 12), c.Nffree);
    }

    for (var cg = 0; cg < geom.Ncg; cg++) {
      var c = ComputeCgCounts(geom, cg, dirCount, liveInodes, cg0DataEndFrag);
      WriteOneCylinderGroup(disk, geom, cg, c, liveInodes, cg0DataEndFrag);
    }
  }

  private void WriteOneCylinderGroup(byte[] disk, Geometry geom, int cg, CgCounts c, int liveInodes, int cg0DataEndFrag) {
    var cgbase = cg * geom.Fpg;
    var cgOffset = (cgbase + CblkNo) * FragSize;
    var cg0 = disk.AsSpan(cgOffset, BlockSize);
    cg0.Clear();

    var ndblk = CgNdblk(geom, cg);
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var usedInodes = cg == 0 ? liveInodes : 0;

    BinaryPrimitives.WriteInt32LittleEndian(cg0[4..], CgMagic);            // cg_magic
    BinaryPrimitives.WriteUInt32LittleEndian(cg0[8..], now);               // cg_old_time
    BinaryPrimitives.WriteInt32LittleEndian(cg0[12..], cg);               // cg_cgx
    BinaryPrimitives.WriteInt16LittleEndian(cg0[16..], 1);                 // cg_old_ncyl
    BinaryPrimitives.WriteInt16LittleEndian(cg0[18..], (short)geom.Ipg);   // cg_old_niblk
    BinaryPrimitives.WriteInt32LittleEndian(cg0[20..], ndblk);             // cg_ndblk
    BinaryPrimitives.WriteInt32LittleEndian(cg0[24..], c.Ndir);            // cg_cs.cs_ndir
    BinaryPrimitives.WriteInt32LittleEndian(cg0[28..], c.Nbfree);          // cg_cs.cs_nbfree
    BinaryPrimitives.WriteInt32LittleEndian(cg0[32..], c.Nifree);          // cg_cs.cs_nifree
    BinaryPrimitives.WriteInt32LittleEndian(cg0[36..], c.Nffree);          // cg_cs.cs_nffree
    BinaryPrimitives.WriteInt32LittleEndian(cg0[40..], 0);                 // cg_rotor
    BinaryPrimitives.WriteInt32LittleEndian(cg0[44..], 0);                 // cg_frotor
    BinaryPrimitives.WriteInt32LittleEndian(cg0[48..], 0);                 // cg_irotor

    // cg layout offsets (newfs initcg, Oflag=1).
    var btotoff = 168;
    var boff = btotoff + 4;                          // old_cpg(1) * sizeof(int32)
    var iusedoff = boff + 2;                          // old_cpg(1)*old_nrpos(1)*sizeof(int16)
    var freeoff = iusedoff + HowMany(geom.Ipg, 8);
    var clustersumoff = RoundUp(freeoff + HowMany(geom.Fpg, 8) - 4, 4);
    var clusteroff = clustersumoff + (ContigSumSize + 1) * 4;
    var nextfreeoff = clusteroff + HowMany(geom.Fpg / Frag, 8);

    BinaryPrimitives.WriteInt32LittleEndian(cg0[84..], btotoff);           // cg_old_btotoff
    BinaryPrimitives.WriteInt32LittleEndian(cg0[88..], boff);              // cg_old_boff
    BinaryPrimitives.WriteInt32LittleEndian(cg0[92..], iusedoff);          // cg_iusedoff
    BinaryPrimitives.WriteInt32LittleEndian(cg0[96..], freeoff);           // cg_freeoff
    BinaryPrimitives.WriteInt32LittleEndian(cg0[100..], nextfreeoff);      // cg_nextfreeoff
    BinaryPrimitives.WriteInt32LittleEndian(cg0[104..], clustersumoff);    // cg_clustersumoff
    BinaryPrimitives.WriteInt32LittleEndian(cg0[108..], clusteroff);       // cg_clusteroff
    BinaryPrimitives.WriteInt32LittleEndian(cg0[112..], ndblk / Frag);     // cg_nclusterblks
    BinaryPrimitives.WriteInt32LittleEndian(cg0[116..], 0);                // cg_niblk (UFS2 only → 0)
    BinaryPrimitives.WriteInt32LittleEndian(cg0[120..], 0);                // cg_initediblk (UFS2 only → 0)
    BinaryPrimitives.WriteInt64LittleEndian(cg0[136..], now);              // cg_time

    // Build the per-cg used-frag map.
    var used = new bool[ndblk];
    MarkCgMetadata(geom, cg, used);
    if (cg == 0) MarkCg0Data(geom, used, cg0DataEndFrag);

    // inode-used bitmap (bit i = inode (cgbase-relative) i used).
    for (var ino = 0; ino < usedInodes && ino < geom.Ipg; ino++)
      cg0[iusedoff + ino / 8] |= (byte)(1 << (ino % 8));

    // free-frag bitmap (1 = free) for frags [0, ndblk).
    for (var f = 0; f < ndblk; f++)
      if (!used[f]) cg0[freeoff + f / 8] |= (byte)(1 << (f % 8));

    // cg_frsum[i] = count of free-fragment runs of length i within blocks that are
    // not wholly free (1 ≤ i < frag). Whole free blocks do not contribute.
    var frsum = new int[Frag];                       // index 0 unused, 1..frag-1
    var blocks = ndblk / Frag;
    for (var blk = 0; blk < blocks; blk++) {
      var baseFrag = blk * Frag;
      var whole = true;
      for (var f = 0; f < Frag; f++) if (used[baseFrag + f]) { whole = false; break; }
      if (whole) continue;                            // whole free block → not a fragment run
      var fragRun = 0;
      for (var f = 0; f <= Frag; f++) {
        var free = f < Frag && !used[baseFrag + f];
        if (free) fragRun++;
        else { if (fragRun > 0 && fragRun < Frag) frsum[fragRun]++; fragRun = 0; }
      }
    }
    for (var i = 1; i < Frag; i++)
      BinaryPrimitives.WriteInt32LittleEndian(cg0[(52 + i * 4)..], frsum[i]);

    // cluster-free bitmap (whole free blocks) + cluster summary array. Each
    // maximal run of whole free blocks of length L increments clustersum[min(L,
    // contigsumsize)] by one.
    var clusterSums = new int[ContigSumSize + 1];    // index 0 unused, 1..contigsumsize
    var run = 0;
    for (var blk = 0; blk < blocks; blk++) {
      var baseFrag = blk * Frag;
      var whole = true;
      for (var f = 0; f < Frag; f++) if (used[baseFrag + f]) { whole = false; break; }
      if (whole) {
        cg0[clusteroff + blk / 8] |= (byte)(1 << (blk % 8));
        run++;
      } else {
        AccumulateRun(clusterSums, run);
        run = 0;
      }
    }
    AccumulateRun(clusterSums, run);
    for (var i = 1; i <= ContigSumSize; i++)
      BinaryPrimitives.WriteInt32LittleEndian(cg0[(clustersumoff + i * 4)..], clusterSums[i]);

    // ── superblock backup at cgbase + fs_sblkno ─────────────────────────────
    var primarySb = disk.AsSpan(SuperblockOffset, SuperblockSize);
    var backupOffset = (cgbase + SblkNo) * FragSize;
    primarySb.CopyTo(disk.AsSpan(backupOffset, SuperblockSize));
  }

  // A maximal run of free whole blocks of length `run` contributes one to the
  // bucket min(run, contigsumsize) (the top bucket counts runs ≥ contigsumsize).
  private static void AccumulateRun(int[] clusterSums, int run) {
    if (run <= 0) return;
    clusterSums[Math.Min(run, ContigSumSize)]++;
  }

  private int TotalFreeBlocks(Geometry geom, int cg0DataEndFrag) {
    var total = 0;
    for (var cg = 0; cg < geom.Ncg; cg++) {
      var c = ComputeCgCounts(geom, cg, 0, 0, cg0DataEndFrag);
      total += c.Nbfree;
    }
    return total;
  }

  private int TotalFreeFrags(Geometry geom, int cg0DataEndFrag) {
    var total = 0;
    for (var cg = 0; cg < geom.Ncg; cg++) {
      var c = ComputeCgCounts(geom, cg, 0, 0, cg0DataEndFrag);
      total += c.Nffree;
    }
    return total;
  }

  // ── ufs1_dinode (128 bytes) ───────────────────────────────────────────────
  private static void WriteUfs1Inode(
    byte[] disk, long inodeByteOffset,
    uint mode, ushort nlink, ulong size, uint blocksUsed512,
    ReadOnlySpan<int> directBlocks, int indirectBlock = 0, uint dirDepth = 0
  ) {
    var di = disk.AsSpan((int)inodeByteOffset, InodeSize);
    di.Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(di[0..], (ushort)mode);       // di_mode
    BinaryPrimitives.WriteUInt16LittleEndian(di[2..], nlink);              // di_nlink
    BinaryPrimitives.WriteUInt32LittleEndian(di[4..], dirDepth);           // di_dirdepth (UFS dir depth from root)
    BinaryPrimitives.WriteUInt64LittleEndian(di[8..], size);               // di_size
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt32LittleEndian(di[16..], now);               // di_atime
    BinaryPrimitives.WriteUInt32LittleEndian(di[24..], now);               // di_mtime
    BinaryPrimitives.WriteUInt32LittleEndian(di[32..], now);               // di_ctime
    for (var i = 0; i < MaxDirectBlocks && i < directBlocks.Length; i++)
      BinaryPrimitives.WriteInt32LittleEndian(di[(40 + i * 4)..], directBlocks[i]);
    BinaryPrimitives.WriteInt32LittleEndian(di[(40 + MaxDirectBlocks * 4)..], indirectBlock); // di_ib[0]
    BinaryPrimitives.WriteUInt32LittleEndian(di[104..], blocksUsed512);    // di_blocks (512-sectors)
    BinaryPrimitives.WriteUInt32LittleEndian(di[108..], 1);               // di_gen
    BinaryPrimitives.WriteUInt32LittleEndian(di[112..], 0);               // di_uid
    BinaryPrimitives.WriteUInt32LittleEndian(di[116..], 0);               // di_gid
  }

  // ── directory entry ───────────────────────────────────────────────────────
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
}

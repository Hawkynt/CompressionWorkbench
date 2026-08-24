#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.SysV;

/// <summary>
/// Builds minimal AT&amp;T UNIX System V "s5fs" filesystem images (the classic
/// 1983 layout — distinguished from BSD UFS and from Linux's "Coherent" /
/// "Xenix" SysV variants by magic <c>0xFD187E20</c> and type code 2 = 1024-byte
/// blocks).
/// </summary>
/// <remarks>
/// <para>
/// Layout (every field offset cross-checked against linux/fs/sysv/super.c and
/// the AT&amp;T System V Interface Definition):
/// </para>
/// <code>
///   Block 0            bootstrap (zeroed)
///   Block 1            superblock (1024 bytes at file offset 0x400)
///                        u16 s_isize           [ +0]   ilist size in blocks
///                        u32 s_fsize           [ +2]   total blocks on device
///                        u16 s_nfree           [ +6]   free-block cache count
///                        u32 s_free[50]        [ +8]   free-block cache
///                        u16 s_ninode          [+216]  free-inode cache count
///                        u16 s_inode[100]      [+218]  free-inode cache
///                        u8  s_flock           [+418]  locks (zero on a clean fs)
///                        u8  s_ilock           [+419]
///                        u8  s_fmod            [+420]  superblock-modified flag (clean=0)
///                        u8  s_ronly           [+421]  read-only flag (0)
///                        u32 s_time            [+422]  last-update timestamp
///                        u16 s_dinfo[4]        [+426]  device info (zero)
///                        u32 s_tfree           [+434]  total free blocks
///                        u16 s_tinode          [+438]  total free inodes
///                        u8  s_fname[6]        [+440]
///                        u8  s_fpack[6]        [+446]
///                        ...                            (zeros)
///                        u32 s_magic           [+504]  0xFD187E20
///                        u32 s_type            [+508]  1=512B 2=1024B 3=2048B
///   Block 2..N         inode list ("ilist"), 64-byte inodes
///                        u16 di_mode           [ +0]
///                        u16 di_nlink          [ +2]
///                        u16 di_uid            [ +4]
///                        u16 di_gid            [ +6]
///                        u32 di_size           [ +8]
///                        u8  di_addr[40]       [+12]   13 x 3-byte zone ptrs
///                                                      (10 direct, 1 ind, 1 dind, 1 tind)
///                        u32 di_atime          [+52]
///                        u32 di_mtime          [+56]
///                        u32 di_ctime          [+60]
///   Block N+1..         data blocks
/// </code>
/// <para>
/// Free-block management uses the classic chained 50-pointer cache. The
/// in-superblock cache holds up to 50 entries; when full and another block is
/// freed (which doesn't happen at format time but is how the kernel later
/// extends the chain), the kernel writes <c>s_nfree</c> + <c>s_free[]</c> into
/// the about-to-be-freed block and resets the cache to count 1, leaving the
/// newly freed block at the cache head. At format time the entire free chain
/// is encoded by leaving the head pointer in the superblock and chaining out
/// through additional 1024-byte free-list blocks (each laid as
/// <c>u16 nfree; u8 pad[2]; u32 free[50]</c> — the 2-byte pad keeps the array
/// 4-byte aligned, matching how Linux's <c>fs/sysv/balloc.c</c> reads them).
/// </para>
/// <para>
/// The writer targets the classic System V variant only: 1024-byte blocks,
/// 16-byte directory entries (inum:u16 + name:char[14]), little-endian field
/// ordering. Other in-the-wild SysV-family variants (Coherent, Xenix, SCO,
/// AFS) carry different magics and/or different inode shapes — supporting
/// them is out of scope.
/// </para>
/// </remarks>
public sealed class SysVWriter : IDisposable {

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];

  // Optional volume name written into s_fname[6] at superblock offset 440.
  // Blank unless asked for, as mkfs leaves it; ASCII, space/NUL-padded, max 6 bytes.
  private string _volumeLabel = "";

  /// <summary>
  /// Sets the 6-byte volume name written into the superblock <c>s_fname[6]</c>
  /// field (offset 440). ASCII, truncated to 6 bytes, space-padded.
  /// </summary>
  public void SetVolumeLabel(string label) => this._volumeLabel = label ?? "";

  // Spec constants — every offset audited against linux/fs/sysv/{super,sysv,inode}.h
  // and against the AT&T System V Interface Definition appendix on s5fs.
  internal const int BlockSize = 1024;
  internal const int InodeSize = 64;             // di_addr is 13*3 = 39 bytes plus 25 bytes header/timestamps
  internal const int DirEntrySize = 16;          // u16 inode + char[14] name
  internal const int MaxNameLength = 14;
  internal const int EntriesPerBlock = BlockSize / DirEntrySize;     // 64
  internal const int DirectZones = 10;
  internal const int FreeCacheSize = 50;         // s_free[50]
  internal const int InodeCacheSize = 100;       // s_inode[100]
  internal const int SuperblockOffset = 512;     // block 0 + BLOCK_SIZE/2, where the Linux sysv driver reads it
  internal const int FirstInodeBlock = 2;        // Block 2 onward
  internal const uint MagicSysV = 0xFD187E20;
  internal const uint TypeCode1024 = 2;          // s_type = 2 → 1024-byte blocks
  internal const ushort ModeDirectory = 0x41ED;  // S_IFDIR | 0755
  internal const ushort ModeRegularFile = 0x81A4;// S_IFREG | 0644
  private const int RootInode = 2;               // Inode 1 reserved per AT&T convention

  public SysVWriter(Stream output, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(output);
    this._output = output;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Registers a file to be written into the image.</summary>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((path, data));
  }

  /// <summary>Convenience: builds the image to a byte array.</summary>
  public static byte[] Build(IEnumerable<(string Name, byte[] Data)> files) {
    ArgumentNullException.ThrowIfNull(files);
    using var ms = new MemoryStream();
    using (var w = new SysVWriter(ms, leaveOpen: true)) {
      foreach (var (n, d) in files) w.AddFile(n, d);
      w.Finish();
    }
    return ms.ToArray();
  }

  // ── Tree node ──────────────────────────────────────────────────────────

  /// <summary>
  /// Leave a block unallocated where the file holds nothing but zeros.
  /// </summary>
  /// <remarks>
  /// A block pointer of zero names no block at all: the driver hands back a
  /// block of zeros for it and reads on. So a run of zeros need not occupy
  /// anything — the file keeps its length and every one of its bytes, and the
  /// volume is sized for what was actually written. A pointer block whose whole
  /// range is hole is not allocated either, because that is what a volume looks
  /// like when the gap was seeked past rather than written.
  /// </remarks>
  public bool MakeSparse { get; set; }

  /// <summary>
  /// Store one copy of files whose bytes are identical and give the rest a
  /// second name for it.
  /// </summary>
  /// <remarks>
  /// One inode, one set of blocks, and a count in the inode of how many
  /// directory entries name it — that is all a hard link is, and s5fs has had it
  /// since the beginning.
  /// </remarks>
  public bool DeduplicateWithLinks { get; set; }

  /// <summary>An s5fs inode counts its names in sixteen bits.</summary>
  private const int MaxLinks = ushort.MaxValue;

  private sealed class TreeNode {
    public uint Inode;
    public bool IsDirectory;
    public byte[] FileData = [];

    /// <summary>How many names point at this inode.</summary>
    public int Links = 1;

    /// <summary>Which of this file's blocks hold nothing but zeros.</summary>
    public bool[] Holes = [];

    public readonly List<KeyValuePair<string, TreeNode>> Children = [];
    public readonly Dictionary<string, TreeNode> ChildIndex = [];
  }

  /// <summary>Writes the complete s5fs image to <see cref="_output"/>.</summary>
  public void Finish() {
    // 1. Assemble the directory tree from registered paths.
    var root = new TreeNode { IsDirectory = true };
    var allDirs = new List<TreeNode> { root };
    var allFiles = new List<TreeNode>();

    foreach (var (rawPath, data) in this._files) {
      var parts = SplitPath(rawPath);
      if (parts.Count == 0) continue;
      var cursor = root;
      for (var i = 0; i < parts.Count - 1; i++) {
        var component = parts[i];
        if (!cursor.ChildIndex.TryGetValue(component, out var next)) {
          next = new TreeNode { IsDirectory = true };
          AddChild(cursor, component, next);
          allDirs.Add(next);
        } else if (!next.IsDirectory) {
          throw new InvalidOperationException(
            $"SysV: path component '{component}' is used as both a file and a directory.");
        }
        cursor = next;
      }
      var leaf = parts[^1];
      if (cursor.ChildIndex.ContainsKey(leaf))
        throw new InvalidOperationException($"SysV: duplicate entry '{rawPath}'.");
      var fileNode = new TreeNode { IsDirectory = false, FileData = data };
      AddChild(cursor, leaf, fileNode);
      allFiles.Add(fileNode);
    }

    // 2. Assign inode numbers. Root is inode 2 (the AT&T convention; inode 1
    //    is reserved for the historic "bad-block" inode and stays unused).
    root.Inode = RootInode;
    var nextInode = (uint)RootInode + 1;
    foreach (var dir in allDirs) {
      if (dir == root) continue;
      dir.Inode = nextInode++;
    }
    // Files whose bytes are identical share one inode when asked, so the rest of
    // the build only ever sees the stored ones and the content is laid down once
    // however many names lead to it.
    var stored = new List<TreeNode>(allFiles.Count);
    var firstWithContent = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
    foreach (var file in allFiles) {
      var key = ContentKey(file.FileData);
      if (this.DeduplicateWithLinks && firstWithContent.TryGetValue(key, out var first)) {
        file.Inode = first.Inode;
        ++first.Links;
        // An inode that has run out of room to count its names starts a fresh
        // one for the next copy rather than wrapping round to none.
        if (first.Links >= MaxLinks) firstWithContent.Remove(key);
        continue;
      }

      file.Inode = nextInode++;
      file.Holes = this.HoleMap(file.FileData);
      if (this.DeduplicateWithLinks) firstWithContent[key] = file;
      stored.Add(file);
    }
    var totalInodes = (int)nextInode - 1;    // inodes 1..totalInodes are accounted for

    // 3. Plan the inode table size. ilist must hold every used inode plus
    //    inode 1 (reserved). We need at least ceil(totalInodes / 16) blocks
    //    (16 inodes per 1024-byte block). Round up so the inode-table
    //    boundary aligns to a block.
    var inodesPerBlock = BlockSize / InodeSize;          // 16
    var ilistBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;
    if (ilistBlocks < 1) ilistBlocks = 1;
    var firstDataBlock = FirstInodeBlock + ilistBlocks;

    // 4. Compute how many data blocks each directory and file consumes.
    //    Files: ceil(size / 1024) direct blocks (writer caps at 10 direct).
    //    Dirs:  ceil((2 + children) / 64) direct blocks (writer caps at 10).
    var dataBlocksNeeded = 0;
    foreach (var dir in allDirs)
      dataBlocksNeeded += DirectoryBlockCount(dir);
    // What a file costs is counted by laying it out with nowhere to write it, so
    // the number the volume is sized for and the layout it ends up with come from
    // one piece of code rather than two descriptions of it.
    foreach (var file in stored) {
      var counter = 0u;
      LayoutFile(null, ref counter, file.FileData, file.Holes);
      dataBlocksNeeded += (int)counter;
    }

    // 5. Plan the free-block pool. The superblock's in-line free-block cache
    //    can advertise up to 49 free blocks (slot 0 is reserved as the chain
    //    pointer; an empty chain pointer = 0 terminates the list). We cap the
    //    reserved free space at exactly 49 blocks to avoid the complexity of
    //    spilling into chained on-disk groups — a read-only mount never
    //    allocates, so the cache size is purely advisory for tools like df.
    const int reservedFreeBlocks = FreeCacheSize - 1;   // 49 KB of free space
    var totalBlocks = firstDataBlock + dataBlocksNeeded + reservedFreeBlocks;
    var disk = new byte[totalBlocks * BlockSize];

    // 6. Allocate per-directory and per-file data blocks, populating the
    //    disk array as we go.
    var nextDataBlock = (uint)firstDataBlock;

    foreach (var dir in allDirs) {
      var entries = new List<(uint Inode, string Name)>(dir.Children.Count + 2) {
        (dir.Inode, "."),
        (ParentInode(root, dir), ".."),
      };
      foreach (var (childName, child) in dir.Children)
        entries.Add((child.Inode, childName));

      var blockCount = (entries.Count + EntriesPerBlock - 1) / EntriesPerBlock;
      if (blockCount > DirectZones)
        throw new InvalidOperationException(
          $"SysV writer addresses directories through direct zones only " +
          $"(max {EntriesPerBlock * DirectZones - 2} entries per directory).");

      var dirBlocks = new uint[DirectZones];
      for (var b = 0; b < blockCount; b++) {
        var block = nextDataBlock++;
        dirBlocks[b] = block;
        var spanOff = (int)(block * BlockSize);
        var first = b * EntriesPerBlock;
        var last = Math.Min(first + EntriesPerBlock, entries.Count);
        for (var e = first; e < last; e++) {
          var (ino, name) = entries[e];
          WriteDirEntry(disk, spanOff + (e - first) * DirEntrySize, ino, name);
        }
      }

      var childDirCount = 0;
      foreach (var (_, child) in dir.Children)
        if (child.IsDirectory) childDirCount++;

      WriteInode(disk, ilistOff: FirstInodeBlock * BlockSize,
        inodeNumber: dir.Inode,
        mode: ModeDirectory,
        size: (uint)(blockCount * BlockSize),
        nlinks: (ushort)(2 + childDirCount),
        zones: dirBlocks);
    }

    foreach (var file in stored) {
      var fileBlocks = LayoutFile(disk, ref nextDataBlock, file.FileData, file.Holes);

      WriteInode(disk, ilistOff: FirstInodeBlock * BlockSize,
        inodeNumber: file.Inode,
        mode: ModeRegularFile,
        size: (uint)file.FileData.Length,
        nlinks: (ushort)file.Links,
        zones: fileBlocks);
    }

    // 7. Build the free-block chain. The chain starts at the lowest
    //    unallocated block (= nextDataBlock) and runs through the trailing
    //    reserved range. Linked through 50-pointer cache groups.
    var freeBlockStart = nextDataBlock;
    var freeBlockEnd = (uint)totalBlocks;                // exclusive
    var totalFreeBlocks = (int)(freeBlockEnd - freeBlockStart);
    var (sNFree, sFree) = BuildFreeChain(disk, freeBlockStart, freeBlockEnd);

    // 8. Build the free-inode cache. We populate it with inodes that are
    //    laid out in the table but unused: every slot from totalInodes+1 up
    //    to the table capacity. Inode 1 stays out of the cache (reserved).
    var ilistCapacity = ilistBlocks * inodesPerBlock;
    var freeInodes = new List<ushort>(InodeCacheSize);
    for (var ino = totalInodes + 1; ino <= ilistCapacity && freeInodes.Count < InodeCacheSize; ino++)
      freeInodes.Add((ushort)ino);
    var totalFreeInodes = ilistCapacity - totalInodes;

    // 9. Write the superblock. s_isize is the FIRST DATA ZONE (block number of
    // the first data block = FirstInodeBlock + ilist blocks), not the ilist
    // size — the Linux sysv driver reads it as s_firstdatazone and rejects the
    // mount if it is below s_firstinodezone (=2).
    WriteSuperblock(disk,
      isize: (ushort)firstDataBlock,
      fsize: (uint)totalBlocks,
      sNFree: sNFree,
      sFree: sFree,
      sNInode: (ushort)freeInodes.Count,
      sInode: freeInodes,
      sTime: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      sTFree: (uint)totalFreeBlocks,
      sTInode: (ushort)Math.Min(totalFreeInodes, ushort.MaxValue),
      volumeLabel: this._volumeLabel);

    // 10. Bootstrap block (block 0) stays all-zero, matching mkfs.s5.
    this._output.Write(disk);
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static List<string> SplitPath(string path) {
    var parts = new List<string>();
    foreach (var raw in path.Replace('\\', '/').Split('/')) {
      if (raw.Length == 0 || raw is "." or "..") continue;
      var component = raw.Length > MaxNameLength ? raw[..MaxNameLength] : raw;
      parts.Add(component);
    }
    return parts;
  }

  private static void AddChild(TreeNode parent, string name, TreeNode child) {
    parent.Children.Add(new KeyValuePair<string, TreeNode>(name, child));
    parent.ChildIndex[name] = child;
  }

  private static uint ParentInode(TreeNode root, TreeNode target) {
    if (target == root) return root.Inode;
    var found = FindParent(root, target);
    return found?.Inode ?? root.Inode;
  }

  private static TreeNode? FindParent(TreeNode node, TreeNode target) {
    foreach (var (_, child) in node.Children) {
      if (child == target) return node;
      if (child.IsDirectory) {
        var deeper = FindParent(child, target);
        if (deeper != null) return deeper;
      }
    }
    return null;
  }

  private static int DirectoryBlockCount(TreeNode dir) {
    var entries = 2 + dir.Children.Count;
    return (entries + EntriesPerBlock - 1) / EntriesPerBlock;
  }

  // 24-bit zone pointer (low-mid-high byte order; little-endian 24-bit).
  internal static void Write24(Span<byte> dest, uint value) {
    dest[0] = (byte)(value & 0xFF);
    dest[1] = (byte)((value >> 8) & 0xFF);
    dest[2] = (byte)((value >> 16) & 0xFF);
  }

  private static void WriteDirEntry(byte[] disk, int offset, uint inode, string name) {
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(offset, 2), (ushort)inode);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, MaxNameLength);
    Buffer.BlockCopy(nameBytes, 0, disk, offset + 2, copyLen);
    // Remaining name bytes already zero (NUL-pad).
  }

  /// <summary>Which of a payload's blocks hold nothing but zeros.</summary>
  /// <remarks>
  /// Empty when holes are not wanted, so every caller can ask without checking
  /// first and <see cref="LayoutFile" /> has one shape either way.
  /// </remarks>
  private bool[] HoleMap(byte[] data) {
    if (!this.MakeSparse || data.Length == 0) return [];

    var count = (data.Length + BlockSize - 1) / BlockSize;
    var holes = new bool[count];
    for (var b = 0; b < count; b++) {
      var at = b * BlockSize;
      var length = Math.Min(BlockSize, data.Length - at);
      holes[b] = !data.AsSpan(at, length).ContainsAnyExcept((byte)0);
    }
    return holes;
  }

  /// <summary>A file's contents, as a key two identical files share.</summary>
  private static string ContentKey(byte[] data) =>
    data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
    + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

  /// <summary>Pointers a 1024-byte indirect block holds, four bytes each.</summary>
  private const int PointersPerBlock = BlockSize / 4;

  /// <summary>
  /// Places one file's blocks and returns its thirteen inode slots.
  /// </summary>
  /// <remarks>
  /// <para>Ten direct, then a single-, double- and triple-indirect tree, which is
  /// what an s5fs inode has always addressed. This used to refuse any file past
  /// the ten direct blocks outright — 10 240 bytes — while the reader beside it
  /// followed all four levels, so a volume from a real System V machine could be
  /// read here and never written. It also meant the format could not build the
  /// probe volume the interoperability checks use, and so was passed over by
  /// every one of them.</para>
  ///
  /// <para>A null <paramref name="disk" /> hands out block numbers without
  /// writing anything, which is how the volume is sized.</para>
  /// </remarks>
  private static uint[] LayoutFile(byte[]? disk, ref uint nextBlock, byte[] data, bool[] holes) {
    var slots = new uint[13];
    if (data.Length == 0) return slots;

    var blockCount = (data.Length + BlockSize - 1) / BlockSize;
    var dataBlocks = new uint[blockCount];
    for (var b = 0; b < blockCount; b++) {
      // A hole keeps its place in the pointer list and takes no block: the
      // pointer stays zero, and a reader hands back a block of zeros for it.
      if (b < holes.Length && holes[b]) continue;

      var block = nextBlock++;
      dataBlocks[b] = block;
      if (disk == null) continue;

      var srcOff = b * BlockSize;
      Buffer.BlockCopy(data, srcOff, disk, (int)(block * BlockSize),
        Math.Min(BlockSize, data.Length - srcOff));
    }

    var idx = 0;
    for (; idx < blockCount && idx < DirectZones; idx++)
      slots[idx] = dataBlocks[idx];
    if (idx == blockCount) return slots;

    for (var level = 1; level <= 3 && idx < blockCount; ++level)
      slots[DirectZones + level - 1] =
        BuildIndirect(disk, ref nextBlock, dataBlocks, ref idx, blockCount, level);

    if (idx < blockCount)
      throw new InvalidOperationException(
        $"SysV: a file of {data.Length:N0} bytes is past what triple-indirect addressing reaches.");
    return slots;
  }

  /// <summary>
  /// Builds one indirect tree of the given level and returns the block it is
  /// rooted at. Pointers inside an indirect block are four-byte words, unlike the
  /// three-byte ones an inode carries.
  /// </summary>
  private static uint BuildIndirect(byte[]? disk, ref uint nextBlock,
      uint[] dataBlocks, ref int idx, int total, int level) {
    var reach = PointersPerBlock;
    for (var i = 1; i < level; ++i) reach *= PointersPerBlock;

    // A pointer block whose whole range is hole is not allocated at all: a slot
    // of zero is how a volume records a gap nobody wrote.
    var end = Math.Min(total, idx + reach);
    var allHole = true;
    for (var probe = idx; probe < end; probe++)
      if (dataBlocks[probe] != 0) { allHole = false; break; }
    if (allHole) {
      idx = end;
      return 0;
    }

    var root = nextBlock++;
    var baseByte = (long)root * BlockSize;

    if (level == 1) {
      var count = Math.Min(PointersPerBlock, total - idx);
      for (var i = 0; i < count; i++) {
        var block = dataBlocks[idx++];
        if (disk != null)
          BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan((int)(baseByte + i * 4)), block);
      }
      return root;
    }

    for (var i = 0; i < PointersPerBlock && idx < total; i++) {
      var child = BuildIndirect(disk, ref nextBlock, dataBlocks, ref idx, total, level - 1);
      if (disk != null)
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan((int)(baseByte + i * 4)), child);
    }
    return root;
  }

  private static void WriteInode(byte[] disk, int ilistOff, uint inodeNumber,
      ushort mode, uint size, ushort nlinks, uint[] zones) {
    var off = ilistOff + ((int)inodeNumber - 1) * InodeSize;
    var span = disk.AsSpan(off, InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(span,           mode);
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2),  nlinks);
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4),  0); // uid
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6),  0); // gid
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8),  size);
    // di_addr[40] = 13 3-byte pointers + 1 byte padding at the tail
    for (var i = 0; i < Math.Min(zones.Length, 13); i++)
      Write24(span.Slice(12 + i * 3, 3), zones[i]);
    // Padding at offset 12+39 = 51 already zero (so do atime/mtime/ctime).
    var now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(52), now);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(56), now);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(60), now);
  }

  /// <summary>
  /// Populates the superblock's in-line free-block cache with up to 49
  /// free-block numbers drawn from <paramref name="firstFree"/> ..
  /// <paramref name="endExclusive"/>, leaving slot 0 = 0 to terminate the
  /// chain (no on-disk chain groups; a freshly created image is small
  /// enough that 49 free blocks suffice for tooling that inspects df).
  /// </summary>
  /// <remarks>
  /// Per linux/fs/sysv/balloc.c <c>sysv_new_block</c>: the kernel pops
  /// <c>s_free[--s_nfree]</c> on each allocation. When <c>s_nfree</c>
  /// drops to 1 the kernel reads the block pointed to by <c>s_free[0]</c>
  /// to refill the cache. A zero in <c>s_free[0]</c> means "no chain
  /// follows" and the volume is full once the in-line cache is exhausted.
  /// We rely on the fact that a read-only mount never allocates, so the
  /// chain is purely advisory.
  /// </remarks>
  private static (ushort sNFree, uint[] sFree) BuildFreeChain(byte[] disk, uint firstFree, uint endExclusive) {
    var sFree = new uint[FreeCacheSize];
    if (firstFree >= endExclusive)
      return (0, sFree);                       // no free space at all; clean superblock

    // Enumerate free blocks in descending order so the kernel pops them
    // back in ascending order (sFree[49] popped first → smallest block).
    var freeBlocks = new List<uint>((int)(endExclusive - firstFree));
    for (var b = endExclusive; b > firstFree; b--)
      freeBlocks.Add(b - 1);

    // Slot 0 stays 0 (chain terminator). Slots 1..49 carry actual free
    // blocks. s_nfree counts ALL slots including the terminator, matching
    // the on-disk semantic ("how many entries the kernel walks before
    // following the chain pointer in slot 0").
    sFree[0] = 0;
    var slotsAvailable = FreeCacheSize - 1;    // 49
    var entriesToStore = Math.Min(slotsAvailable, freeBlocks.Count);
    for (var i = 0; i < entriesToStore; i++)
      sFree[1 + i] = freeBlocks[i];

    return ((ushort)(1 + entriesToStore), sFree);
  }

  private static void WriteSuperblock(byte[] disk,
      ushort isize, uint fsize,
      ushort sNFree, uint[] sFree,
      ushort sNInode, List<ushort> sInode,
      uint sTime, uint sTFree, ushort sTInode,
      string volumeLabel) {
    var sb = disk.AsSpan(SuperblockOffset, BlockSize);
    // s_isize           [ +0] u16
    BinaryPrimitives.WriteUInt16LittleEndian(sb,                    isize);
    // s_fsize           [ +2] u32
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(2),           fsize);
    // s_nfree           [ +6] u16
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(6),           sNFree);
    // s_free[50]        [ +8] u32 x 50
    for (var i = 0; i < FreeCacheSize; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(8 + i * 4), sFree[i]);
    // s_ninode          [+216] u16
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(216),         sNInode);
    // s_inode[100]      [+218] u16 x 100
    for (var i = 0; i < sInode.Count && i < InodeCacheSize; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(218 + i * 2), sInode[i]);
    // s_flock/s_ilock/s_fmod/s_ronly stay zero (clean fs).
    // s_time            [+422] u32
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(422),         sTime);
    // s_dinfo[4]        [+426] u16 x 4 — zero (no device geometry)
    // s_tfree           [+434] u32
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(434),         sTFree);
    // s_tinode          [+438] u16
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(438),         sTInode);
    // s_fname[6]        [+440] — volume label, ASCII 6 chars (space-padded).
    sb.Slice(440, 6).Fill((byte)' ');
    var nameBytes = Encoding.ASCII.GetBytes(volumeLabel);
    nameBytes.AsSpan(0, Math.Min(nameBytes.Length, 6)).CopyTo(sb.Slice(440, 6));
    // s_fpack[6]        [+446] — pack name; zero-pad
    var pack = "v1    "u8;
    pack.CopyTo(sb.Slice(446, 6));
    // gap [452..503] zero
    // s_magic           [+504] u32
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(504),         MagicSysV);
    // s_type            [+508] u32
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(508),         TypeCode1024);
    // remaining bytes [512..1023] zero
  }

  public void Dispose() {
    if (!this._leaveOpen) this._output.Dispose();
  }
}

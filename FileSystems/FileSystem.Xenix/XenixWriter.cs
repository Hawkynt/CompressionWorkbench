#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Xenix;

/// <summary>
/// Builds minimal Microsoft/SCO Xenix System V filesystem images. Targets the
/// "Xenix V" (s5fs-compatible) variant — the layout Linux's historical
/// <c>sysv</c> driver mounted as <c>-t sysv -o xenix</c>: 1024-byte blocks,
/// 64-byte inodes with 24-bit zone pointers, 16-byte directory entries
/// (u16 inode + 14-char name), and the <c>0xFD187E20</c> superblock magic at
/// block-relative offset 504.
/// </summary>
/// <remarks>
/// <para><b>Layout</b> — every emitted image has the shape
/// <c>boot | sb | inode-table | data</c>:</para>
/// <list type="number">
///   <item>Block 0 (offset 0): bootstrap, zero-filled.</item>
///   <item>Block 1 (offset 1024): superblock; magic at <c>sb+504</c>,
///         block-size code (2 = 1024B) at <c>sb+508</c>.</item>
///   <item>Block 2..: 64-byte inode array; inode 2 is root.</item>
///   <item>Following blocks: data zones (directory tables and file bodies).</item>
/// </list>
/// <para><b>Scope.</b> Files are written through the 10 direct zone slots only
/// (max <c>10 * blockSize</c> bytes per file with the default 1 KB blocks).
/// Directories use direct zones; a directory's own entry table is laid out as a
/// flat list of 16-byte records (".", "..", child×N) starting at offset 0 of
/// the directory's first zone. Names longer than 14 ASCII bytes are truncated
/// (Xenix's directory entry budget). Nested paths produce real intermediate
/// directory inodes.</para>
/// </remarks>
public sealed class XenixWriter : IDisposable {

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Path, byte[] Data)> _files = [];

  // Mirror of XenixReader constants.
  private const int BootBlockSize = 1024;
  private const int SuperblockOffset = 1024;
  private const int InodeSize = 64;
  // Genuine Xenix superblock magic and offsets, as written by mkfs.xenix and
  // checked verbatim by the Linux sysv driver's detect_xenix(): s_magic at
  // struct offset 0x3F8 holds 0x2B5544, s_type at 0x3FC selects the block size
  // (1=512B, 2=1024B, 3=2048B). The whole xenix_super_block is exactly one
  // 1024-byte block, so it fits in block 1.
  internal const uint MagicXenix = 0x002B5544;
  internal const int MagicOffset = 0x3F8;
  internal const int TypeOffset = 0x3FC;
  private const uint TypeCode1024 = 2; // matches reader BlockSize==1024
  private const int BlockSize = 1024;
  private const int RootInode = 2;

  // Directory layout: 16-byte entries (u16 inode + 14-char name).
  private const int DirEntrySize = 16;
  private const int MaxNameLength = 14;
  private const int EntriesPerZone = BlockSize / DirEntrySize; // 64

  // 13-zone-slot inode: 10 direct + 1/2/3 indirect (we only allocate direct).
  private const int DirectZones = 10;

  // S_IFDIR | 0755 / S_IFREG | 0644
  private const ushort ModeDirectory = 0x41ED;
  private const ushort ModeRegularFile = 0x81A4;

  public XenixWriter(Stream output, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(output);
    this._output = output;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>Registers a file to be embedded into the emitted image.</summary>
  public void AddFile(string path, byte[] data) {
    ArgumentNullException.ThrowIfNull(path);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((path, data));
  }

  // In-memory mirror of the directory tree we will emit. Built from the
  // registered file paths; one node per path component.
  /// <summary>
  /// Leave a zone unallocated where the file holds nothing but zeros.
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
  /// directory entries name it — that is all a hard link is.
  /// </remarks>
  public bool DeduplicateWithLinks { get; set; }

  /// <summary>A Xenix inode counts its names in sixteen bits.</summary>
  private const int MaxLinks = ushort.MaxValue;

  private sealed class TreeNode {
    public uint Inode;
    public bool IsDirectory;
    public byte[] FileData = [];

    /// <summary>How many names point at this inode.</summary>
    public int Links = 1;

    /// <summary>Which of this file's zones hold nothing but zeros.</summary>
    public bool[] Holes = [];
    // Children are kept in insertion order so the on-disk layout is
    // deterministic, plus an index for O(1) component-name lookups during
    // path resolution.
    public readonly List<KeyValuePair<string, TreeNode>> Children = [];
    public readonly Dictionary<string, TreeNode> ChildIndex = [];
    public TreeNode? Parent;
  }

  /// <summary>Builds the directory/inode/data layout and writes the image.</summary>
  public void Finish() {
    // ── 1. Build the directory tree from registered paths ──────────────────
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
          next = new TreeNode { IsDirectory = true, Parent = cursor };
          AddChild(cursor, component, next);
          allDirs.Add(next);
        } else if (!next.IsDirectory) {
          throw new InvalidOperationException(
            $"Xenix: path component '{component}' is used as both a file and a directory.");
        }
        cursor = next;
      }

      var leaf = parts[^1];
      if (cursor.ChildIndex.ContainsKey(leaf))
        throw new InvalidOperationException($"Xenix: duplicate entry '{rawPath}'.");
      var fileNode = new TreeNode { IsDirectory = false, FileData = data, Parent = cursor };
      AddChild(cursor, leaf, fileNode);
      allFiles.Add(fileNode);
    }

    // ── 2. Assign inode numbers ────────────────────────────────────────────
    // Xenix s5fs reserves inode 1 (boot/bad-block) and uses inode 2 for the
    // root directory.  We mirror that: root = 2, remaining dirs, then files.
    root.Inode = RootInode;
    var nextInode = RootInode + 1u;
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

    // Total inodes counting the reserved inode 1 slot so the table offsets
    // line up with the reader's (inum-1)*64 indexing.
    var totalInodes = (int)nextInode - 1; // inodes 1..nextInode-1

    // ── 3. Plan block layout ───────────────────────────────────────────────
    // Block 0:        boot   (zero-filled)
    // Block 1:        super  (at file offset 1024)
    // Block 2..:      inode table (totalInodes * 64 bytes, rounded up)
    // Following blks: data zones
    var inodesPerBlock = BlockSize / InodeSize;     // 16
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;
    var firstDataBlock = 2 + inodeTableBlocks;

    // Validate directory addressability and file sizes against the writer's
    // direct-zone-only budget so we fail with a clear message instead of
    // emitting a truncated image.
    EnsureDirectoriesAddressable(root, "");

    // ── 4. Allocate data zones for dirs then files ─────────────────────────
    var nextZone = (uint)firstDataBlock;

    // Per-directory zone(s) holding its entry table.
    var dirZones = new Dictionary<TreeNode, uint[]>(ReferenceEqualityComparer.Instance);
    foreach (var dir in allDirs) {
      var entryCount = 2 + dir.Children.Count; // ".", "..", children
      var zoneCount = (entryCount + EntriesPerZone - 1) / EntriesPerZone;
      var zones = new uint[zoneCount];
      for (var i = 0; i < zoneCount; i++)
        zones[i] = nextZone++;
      dirZones[dir] = zones;
    }

    // Per-file zone(s) holding the file body, and the pointer blocks that
    // address whatever will not fit in the ten the inode carries.
    var fileZones = new Dictionary<TreeNode, uint[]>(ReferenceEqualityComparer.Instance);
    var fileSlots = new Dictionary<TreeNode, uint[]>(ReferenceEqualityComparer.Instance);
    var pointerBlocks = new List<(uint Block, uint[] Pointers)>();
    foreach (var file in stored) {
      var dataLen = file.FileData.Length;
      var zoneCount = dataLen == 0 ? 0 : (dataLen + BlockSize - 1) / BlockSize;
      var zones = new uint[zoneCount];
      for (var i = 0; i < zoneCount; i++) {
        // A hole keeps its place in the pointer list and takes no zone: the
        // pointer stays zero, and a reader hands back a block of zeros for it.
        if (i < file.Holes.Length && file.Holes[i]) continue;
        zones[i] = nextZone++;
      }
      fileZones[file] = zones;
      fileSlots[file] = PlanSlots(zones, ref nextZone, pointerBlocks);
    }

    var totalBlocks = (int)nextZone; // index of the next free zone == total blocks
    var diskSize = totalBlocks * BlockSize;
    var disk = new byte[diskSize];

    // ── 5. Emit the inode table ────────────────────────────────────────────
    var inodeTableOffset = 2 * BlockSize;
    foreach (var dir in allDirs) {
      var zones = dirZones[dir];
      var size = (uint)(zones.Length * BlockSize);
      // A directory's link count is 2 (itself + its "." entry) plus one ".."
      // back-reference for each immediate subdirectory.
      var subdirs = dir.Children.Count(c => c.Value.IsDirectory);
      WriteInode(disk, inodeTableOffset, (int)dir.Inode, ModeDirectory, size, zones, (ushort)(2 + subdirs));
    }
    foreach (var file in stored) {
      var size = (uint)file.FileData.Length;
      WriteInode(disk, inodeTableOffset, (int)file.Inode, ModeRegularFile, size,
        fileSlots[file], (ushort)file.Links);
    }

    // ── 6. Emit directory zones (.,..,child×N) ─────────────────────────────
    foreach (var dir in allDirs) {
      var entries = new List<(uint Inode, string Name)>(dir.Children.Count + 2) {
        (dir.Inode, "."),
        ((dir.Parent ?? root).Inode, ".."),
      };
      foreach (var (childName, child) in dir.Children)
        entries.Add((child.Inode, childName));

      var zones = dirZones[dir];
      for (var z = 0; z < zones.Length; z++) {
        var blockOff = (int)zones[z] * BlockSize;
        var first = z * EntriesPerZone;
        var last = Math.Min(first + EntriesPerZone, entries.Count);
        for (var e = first; e < last; e++) {
          var (ino, name) = entries[e];
          WriteDirEntry(disk, blockOff + (e - first) * DirEntrySize, ino, name);
        }
      }
    }

    // ── 7. Emit file data ──────────────────────────────────────────────────
    foreach (var file in stored) {
      var data = file.FileData;
      var zones = fileZones[file];
      for (var z = 0; z < zones.Length; z++) {
        if (zones[z] == 0) continue;   // a hole owns no zone to write into
        var at = z * BlockSize;
        Array.Copy(data, at, disk, (int)zones[z] * BlockSize,
          Math.Min(BlockSize, data.Length - at));
      }
    }

    // Pointer blocks hold plain four-byte words, unlike the three-byte ones an
    // inode packs its thirteen block numbers into.
    foreach (var (block, pointers) in pointerBlocks) {
      var baseByte = (int)block * BlockSize;
      for (var i = 0; i < pointers.Length; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(baseByte + i * 4), pointers[i]);
    }

    // ── 8. Superblock ──────────────────────────────────────────────────────
    // Genuine xenix_super_block fields the Linux sysv driver consults to mount
    // a Xenix volume read-only:
    //   s_isize (fs16 @ 0x000) — first data zone = first block past the inode
    //                            table; the kernel derives the inode count as
    //                            (s_isize - 2) * inodes_per_block from it.
    //   s_fsize (fs32 @ 0x002) — total number of zones (blocks) in the volume.
    //   s_magic (@ 0x3F8)      — 0x2B5544.
    //   s_type  (@ 0x3FC)      — 2 (1024-byte blocks).
    // The free-block/free-inode caches are left empty (we emit read-only WORM
    // images); the kernel does not need them to enumerate and read files.
    var sb = disk.AsSpan(SuperblockOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0x000), (ushort)firstDataBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x002), (uint)totalBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(MagicOffset), MagicXenix);
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(TypeOffset), TypeCode1024);

    this._output.Write(disk);
  }

  // ── Helpers ──────────────────────────────────────────────────────────────

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

  // A directory's entry table must fit in its 10 direct zone slots.
  // With 1 KB blocks that's 10 zones * 64 entries = 640 entries including
  // "." and ".." (i.e. up to 638 children).
  private static void EnsureDirectoriesAddressable(TreeNode dir, string path) {
    var entryCount = 2 + dir.Children.Count;
    var zonesNeeded = (entryCount + EntriesPerZone - 1) / EntriesPerZone;
    if (zonesNeeded > DirectZones)
      throw new InvalidOperationException(
        $"Xenix writer addresses each directory through {DirectZones} direct " +
        $"zones (max {DirectZones * EntriesPerZone - 2} children); directory " +
        $"'{(path.Length == 0 ? "/" : path)}' has {dir.Children.Count} children.");
    foreach (var (name, child) in dir.Children)
      if (child.IsDirectory)
        EnsureDirectoriesAddressable(child, path.Length == 0 ? name : $"{path}/{name}");
  }

  // 16-byte Xenix dir entry: u16 inode (LE) + 14-byte ASCII name padded with
  // zeros. Names are already truncated to MaxNameLength by SplitPath.
  private static void WriteDirEntry(byte[] disk, int offset, uint inode, string name) {
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(offset), (ushort)inode);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, MaxNameLength);
    Array.Copy(nameBytes, 0, disk, offset + 2, copyLen);
    // Remaining name bytes stay zero from the array allocation.
  }

  // 64-byte Xenix inode:
  //   u16 mode      (0)
  //   u16 nlink     (2)    we leave 0 — Xenix `fsck` doesn't require it for read
  //   u16 uid       (4)
  //   u16 gid       (6)
  //   u32 size      (8)
  //   3-byte * 13 zone addresses (12)   10 direct + 3 indirect (we only fill direct)
  //   u32 atime     (51 in Xenix-3; we leave zeroed)
  //   ...
  // The reader only consults mode/size/zones[0..12] — matching how the kernel
  // sysv driver enumerates a Xenix volume in read-only mode.
  private static void WriteInode(byte[] disk, int tableOff, int inodeNumber,
      ushort mode, uint size, uint[] zones, ushort nlink) {
    // Inodes are 1-based on disk: inode N lives at tableOff + (N-1)*InodeSize.
    var off = tableOff + (inodeNumber - 1) * InodeSize;
    var span = disk.AsSpan(off, InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(span, mode);
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), nlink);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), size);
    // Thirteen block numbers in thirty-nine bytes: ten direct, then the single-,
    // double- and triple-indirect roots.
    for (var i = 0; i < Math.Min(zones.Length, InodeZoneSlots); i++)
      Write24(span.Slice(12 + i * 3), zones[i]);
  }

  /// <summary>Which of a payload's zones hold nothing but zeros.</summary>
  /// <remarks>
  /// Empty when holes are not wanted, so every caller can ask without checking
  /// first and the layout has one shape either way.
  /// </remarks>
  private bool[] HoleMap(byte[] data) {
    if (!this.MakeSparse || data.Length == 0) return [];

    var count = (data.Length + BlockSize - 1) / BlockSize;
    var holes = new bool[count];
    for (var z = 0; z < count; z++) {
      var at = z * BlockSize;
      var length = Math.Min(BlockSize, data.Length - at);
      holes[z] = !data.AsSpan(at, length).ContainsAnyExcept((byte)0);
    }
    return holes;
  }

  /// <summary>A file's contents, as a key two identical files share.</summary>
  private static string ContentKey(byte[] data) =>
    data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
    + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

  /// <summary>Block numbers an inode carries: ten direct plus three indirect roots.</summary>
  private const int InodeZoneSlots = 13;

  /// <summary>Pointers a pointer block holds, four bytes each.</summary>
  private const int PointersPerBlock = BlockSize / 4;

  /// <summary>
  /// Turns a file's data zones into the thirteen block numbers its inode
  /// carries, claiming pointer blocks for whatever will not fit in the ten
  /// direct ones.
  /// </summary>
  /// <remarks>
  /// This writer used to refuse any file past those ten — 10 240 bytes — while
  /// the reader beside it followed all four addressing levels, so a volume from
  /// a real Xenix system could be read here and never written. It also meant the
  /// format could not build the probe volume the interoperability checks use,
  /// and was passed over by every one of them.
  /// </remarks>
  private static uint[] PlanSlots(uint[] dataZones, ref uint nextZone,
      List<(uint Block, uint[] Pointers)> pointerBlocks) {
    var slots = new uint[InodeZoneSlots];
    var idx = 0;
    for (; idx < dataZones.Length && idx < DirectZones; idx++)
      slots[idx] = dataZones[idx];
    if (idx == dataZones.Length) return slots;

    for (var level = 1; level <= 3 && idx < dataZones.Length; ++level)
      slots[DirectZones + level - 1] = PlanIndirect(dataZones, ref idx, ref nextZone, level, pointerBlocks);

    if (idx < dataZones.Length)
      throw new InvalidOperationException(
        $"Xenix: a file of {(long)dataZones.Length * BlockSize:N0} bytes is past what " +
        "triple-indirect addressing reaches.");
    return slots;
  }

  /// <summary>Claims one pointer tree of the given level and returns its root block.</summary>
  private static uint PlanIndirect(uint[] dataZones, ref int idx, ref uint nextZone, int level,
      List<(uint Block, uint[] Pointers)> pointerBlocks) {
    var reach = PointersPerBlock;
    for (var i = 1; i < level; ++i) reach *= PointersPerBlock;

    // A pointer block whose whole range is hole is not claimed at all: a slot of
    // zero is how a volume records a gap nobody wrote.
    var end = Math.Min(dataZones.Length, idx + reach);
    var allHole = true;
    for (var probe = idx; probe < end; probe++)
      if (dataZones[probe] != 0) { allHole = false; break; }
    if (allHole) {
      idx = end;
      return 0;
    }

    var root = nextZone++;
    var pointers = new List<uint>(PointersPerBlock);

    if (level == 1) {
      var count = Math.Min(PointersPerBlock, dataZones.Length - idx);
      for (var i = 0; i < count; i++) pointers.Add(dataZones[idx++]);
    } else {
      for (var i = 0; i < PointersPerBlock && idx < dataZones.Length; i++)
        pointers.Add(PlanIndirect(dataZones, ref idx, ref nextZone, level - 1, pointerBlocks));
    }

    pointerBlocks.Add((root, [.. pointers]));
    return root;
  }

  // 24-bit little-endian block-address store, mirroring the reader's Read24.
  private static void Write24(Span<byte> dest, uint val) {
    dest[0] = (byte)(val & 0xFF);
    dest[1] = (byte)((val >> 8) & 0xFF);
    dest[2] = (byte)((val >> 16) & 0xFF);
  }

  public void Dispose() {
    if (!this._leaveOpen) this._output.Dispose();
  }
}

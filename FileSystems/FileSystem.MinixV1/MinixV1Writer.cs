#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixV1;

/// <summary>
/// Builds minimal but spec-correct original Minix v1 filesystem images (1987,
/// Tanenbaum). The v1 on-disk format uses 1024-byte blocks, 16-bit zone
/// numbers, 16-bit inode counts and 32-byte inodes addressing data through
/// 7 direct zones, 1 single-indirect and 1 double-indirect zone. Directory
/// names are 14 bytes (magic <c>0x137F</c>) or 30 bytes (magic <c>0x138F</c>).
/// Every path component becomes a real directory inode carrying its own
/// <c>"."</c>/<c>".."</c> entries, so a file added as <c>"a/b/c.txt"</c> is
/// stored under nested directories <c>a</c> and <c>a/b</c>. Files larger than
/// the 7 direct zones (7168 bytes) spill into the single-indirect zone, which
/// addresses a further 512 zones (524 288 bytes); beyond that the
/// double-indirect zone extends the reach further still.
/// </summary>
public sealed class MinixV1Writer : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly int _nameLength;
  private readonly ushort _magic;
  private readonly List<(string Path, byte[] Data)> _files = [];

  private const ushort MagicV1_14 = 0x137F;
  private const ushort MagicV1_30 = 0x138F;
  private const int BlockSize = 1024;
  private const int SuperblockOff = 1024; // block 1
  private const int InodeSize = 32;
  private const int ZonePointersPerBlock = BlockSize / 2; // 512 (16-bit pointers)

  // V1 inode zone slots: 7 direct, 1 single-indirect, 1 double-indirect.
  private const int DirectZones = 7;
  private const int IndirectSlot = 7;
  private const int DoubleIndirectSlot = 8;

  // Classic Minix v1 maximum file size constant, as emitted by mkfs.minix.
  private const uint MaxSizeV1 = 0x10081C00;

  private const ushort ModeDirectory   = 0x41ED; // S_IFDIR | 0755
  private const ushort ModeRegularFile = 0x81A4; // S_IFREG | 0644

  /// <summary>
  /// Creates a writer for the given output stream. <paramref name="longNames"/>
  /// selects the 30-byte-name variant (magic 0x138F); the default is the
  /// classic 14-byte-name layout (magic 0x137F).
  /// </summary>
  public MinixV1Writer(Stream output, bool leaveOpen = false, bool longNames = false) {
    _output = output;
    _leaveOpen = leaveOpen;
    _nameLength = longNames ? 30 : 14;
    _magic = longNames ? MagicV1_30 : MagicV1_14;
  }

  private int DirEntrySize => 2 + _nameLength;
  private int EntriesPerZone => BlockSize / this.DirEntrySize;
  private int MaxNameLength => _nameLength - 1; // reserve a trailing NUL

  /// <summary>Registers a file to be written into the image.</summary>
  public void AddFile(string path, byte[] data) => _files.Add((path, data));

  // A node in the in-memory directory tree built from the registered paths.
  /// <summary>
  /// Leave a zone unallocated where the file holds nothing but zeros.
  /// </summary>
  /// <remarks>
  /// <para>Minix says a file's zones one pointer at a time, and a pointer of
  /// zero names no zone at all: the driver hands back a block of zeros for it
  /// and reads on. So a run of zeros need not occupy anything — the file keeps
  /// its length and every one of its bytes, and the volume is sized for what was
  /// actually written.</para>
  ///
  /// <para>The pointer blocks go the same way. One whose whole range is hole is
  /// not allocated either, and its slot stays zero, because that is what a
  /// volume looks like when the file was written by seeking past the gap: the
  /// kernel never asks for a block it is not filling.</para>
  /// </remarks>
  public bool MakeSparse { get; set; }

  /// <summary>
  /// Store one copy of files whose bytes are identical and give the rest a
  /// second name for it.
  /// </summary>
  /// <remarks>
  /// One inode, one set of zones, and a count in the inode of how many directory
  /// entries name it — that is all a hard link is, and minix has had it since the
  /// beginning. <c>fsck.minix</c> counts the entries pointing at each inode and
  /// compares them against that field, so linking is a matter of naming the same
  /// inode twice and saying so.
  /// </remarks>
  public bool DeduplicateWithLinks { get; set; }

  /// <summary>A v1 inode counts its names in a single byte.</summary>
  private const int MaxLinks = byte.MaxValue;

  private sealed class TreeNode {
    public uint Inode;
    public bool IsDirectory;
    public byte[] FileData = [];
    public TreeNode? Parent;

    /// <summary>How many names point at this inode.</summary>
    public int Links = 1;

    public readonly List<KeyValuePair<string, TreeNode>> Children = [];
    public readonly Dictionary<string, TreeNode> ChildIndex = [];
  }

  /// <summary>Builds and writes the Minix v1 filesystem image.</summary>
  public void Finish() => _output.Write(this.Build());

  /// <summary>Builds the complete image in memory and returns its bytes.</summary>
  /// <summary>Zones a 16-bit zone number can address.</summary>
  private const long MaxZones = (1L << 16) - 1;

  /// <summary>
  /// Performs the build operation.
  /// </summary>
public byte[] Build() {
    var root = new TreeNode { IsDirectory = true };
    var allDirs = new List<TreeNode> { root };
    var allFiles = new List<TreeNode>();
    BuildTree(root, allDirs, allFiles);

    // Assign 1-based inode numbers: root = 1, then directories, then files.
    root.Inode = 1;
    var nextInode = 2u;
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
      if (this.DeduplicateWithLinks
          && firstWithContent.TryGetValue(ContentKey(file.FileData), out var first)) {
        file.Inode = first.Inode;
        ++first.Links;
        // An inode that has run out of room to count its names starts a fresh
        // one for the next copy rather than wrapping round to none.
        if (first.Links >= MaxLinks) firstWithContent.Remove(ContentKey(file.FileData));
        continue;
      }

      file.Inode = nextInode++;
      if (this.DeduplicateWithLinks) firstWithContent[ContentKey(file.FileData)] = file;
      stored.Add(file);
    }
    var totalInodes = (int)nextInode - 1;

    // --- Layout: block0 boot, block1 superblock, imap, zmap, inode table, data.
    var inodesPerBlock = BlockSize / InodeSize; // 32
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;
    // Advertise every inode slot the (whole-block) inode table physically holds,
    // not just the ones in use. The surplus slots are left free in the imap so a
    // genuine in-place writer can allocate new inodes without re-laying-out the
    // image — exactly how a real mkfs.minix volume carries spare inodes.
    var advertisedInodes = inodeTableBlocks * inodesPerBlock;
    const int imapBlocks = 1;
    const int zmapBlocks = 1;
    var firstDataZone = 2 + imapBlocks + zmapBlocks + inodeTableBlocks;

    // A directory's block is the same in both passes below, so it is built once.
    var directoryBytes = new Dictionary<TreeNode, byte[]>(allDirs.Count);
    foreach (var dir in allDirs) {
      var entries = new List<(uint Inode, string Name)>(dir.Children.Count + 2) {
        (dir.Inode, "."),
        ((dir.Parent ?? root).Inode, ".."),
      };
      foreach (var (childName, child) in dir.Children)
        entries.Add((child.Inode, childName));

      var dirBytes = new byte[entries.Count * this.DirEntrySize];
      for (var e = 0; e < entries.Count; e++)
        WriteDirEntry(dirBytes, e * this.DirEntrySize, entries[e].Inode, entries[e].Name);
      directoryBytes[dir] = dirBytes;
    }

    // Which of each stored file's zones hold nothing but zeros. Read once: the
    // count the volume is sized for and the layout it is written with both
    // depend on it, and they have to agree exactly.
    var holes = new Dictionary<TreeNode, bool[]>(stored.Count);
    foreach (var file in stored)
      holes[file] = this.HoleMap(file.FileData);

    // Pass one hands out zone numbers with no disk to write them onto, so the
    // size comes from the same code that does the laying out rather than from a
    // second description of it that could disagree with it.
    var planner = new ZoneAllocator(firstDataZone);
    foreach (var dir in allDirs)
      WriteData(null, 0, firstDataZone, planner, directoryBytes[dir], []);
    foreach (var file in stored)
      WriteData(null, 0, firstDataZone, planner, file.FileData, holes[file]);

    var totalZones = planner.Next;
    // A zone number is 16 bits wide on this variant, and the image is built in
    // memory. Sizing past either limit used to surface as an arithmetic
    // overflow instead of saying the volume cannot hold the payload.
    var diskBytes = (long)totalZones * BlockSize;
    if (totalZones > MaxZones || diskBytes > System.Array.MaxLength)
      throw new InvalidOperationException(
        $"Minix: the payload needs {totalZones:N0} zones ({diskBytes:N0} bytes), past the " +
        $"{MaxZones:N0} zones this volume can address.");
    var disk = new byte[diskBytes];

    var imapOff = 2 * BlockSize;
    var zmapOff = 3 * BlockSize;
    var inodeTableOff = 4 * BlockSize;

    // Inode bitmap: bit 0 reserved, inode N occupies bit N.
    SetBit(disk, imapOff, 0);
    for (var ino = 1; ino <= totalInodes; ino++)
      SetBit(disk, imapOff, ino);

    // Zone bitmap: bit 0 reserved; the first data zone occupies bit 1, i.e.
    // absolute zone Z occupies bit (Z - firstDataZone + 1).
    SetBit(disk, zmapOff, 0);

    var allocator = new ZoneAllocator(firstDataZone);

    // --- Directories ---
    foreach (var dir in allDirs) {
      var dirBytes = directoryBytes[dir];

      var childDirCount = 0;
      foreach (var (_, child) in dir.Children)
        if (child.IsDirectory) childDirCount++;

      // A directory is never written sparse. Its zeros are empty entry slots
      // rather than absent data, and a driver reading a hole where a directory
      // block should be gets a block of entries naming inode 0.
      var zones = WriteData(disk, zmapOff, firstDataZone, allocator, dirBytes, []);
      WriteInode(disk, inodeTableOff, (int)dir.Inode, ModeDirectory,
        (uint)dirBytes.Length, (byte)(2 + childDirCount), zones);
    }

    // --- Files ---
    foreach (var file in stored) {
      var zones = WriteData(disk, zmapOff, firstDataZone, allocator, file.FileData, holes[file]);
      WriteInode(disk, inodeTableOff, (int)file.Inode, ModeRegularFile,
        (uint)file.FileData.Length, (byte)file.Links, zones);
    }

    // --- Superblock at offset 1024 ---
    var sb = disk.AsSpan(SuperblockOff);
    BinaryPrimitives.WriteUInt16LittleEndian(sb,           (ushort)advertisedInodes); // s_ninodes
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(2),  (ushort)totalZones);  // s_nzones
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(4),  imapBlocks);          // s_imap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(6),  zmapBlocks);          // s_zmap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(8),  (ushort)firstDataZone); // s_firstdatazone
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(10), 0);                   // s_log_zone_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(12), MaxSizeV1);           // s_max_size
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(16), _magic);              // s_magic

    return disk;
  }

  // Builds the directory tree from the registered paths.
  private void BuildTree(TreeNode root, List<TreeNode> allDirs, List<TreeNode> allFiles) {
    foreach (var (rawPath, data) in _files) {
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
            $"MinixV1: path component '{component}' is used as both a file and a directory.");
        }
        cursor = next;
      }

      var leaf = parts[^1];
      if (cursor.ChildIndex.ContainsKey(leaf))
        throw new InvalidOperationException($"MinixV1: duplicate entry '{rawPath}'.");
      var fileNode = new TreeNode { IsDirectory = false, FileData = data, Parent = cursor };
      AddChild(cursor, leaf, fileNode);
      allFiles.Add(fileNode);
    }
  }

  // Hands out consecutive absolute zone numbers starting at the first data zone.
  private sealed class ZoneAllocator(int firstDataZone) {
    private int _next = firstDataZone;

    /// <summary>The zone number the next allocation would take.</summary>
    public int Next => _next;

    public uint Allocate() => (uint)_next++;
  }

  // Writes file/directory bytes across direct, single- and double-indirect
  // zones, marks each allocated zone in the bitmap, and returns the 9 inode
  // zone slots.
  // A null disk allocates without writing, which is how the volume is sized.
  private uint[] WriteData(byte[]? disk, int zmapOff, int firstDataZone,
      ZoneAllocator allocator, byte[] data, bool[] holes) {
    var slots = new uint[9];
    if (data.Length == 0) return slots;

    var dataZoneCount = (data.Length + BlockSize - 1) / BlockSize;

    // The flat list of data zones, allocated and filled in order. A hole keeps
    // its place in the list and takes no zone: the pointer stays zero, and a
    // reader hands back a block of zeros for it.
    var dataZones = new uint[dataZoneCount];
    for (var z = 0; z < dataZoneCount; z++) {
      if (z < holes.Length && holes[z]) continue;

      var zone = allocator.Allocate();
      dataZones[z] = zone;
      if (disk == null) continue;

      MarkZone(disk, zmapOff, firstDataZone, zone);
      var at = z * BlockSize;
      Array.Copy(data, at, disk, (long)zone * BlockSize, Math.Min(BlockSize, data.Length - at));
    }

    // Direct slots.
    var idx = 0;
    for (; idx < dataZoneCount && idx < DirectZones; idx++)
      slots[idx] = dataZones[idx];
    if (idx == dataZoneCount) return slots;

    // Single-indirect: one pointer block addressing up to 512 data zones. One
    // whose whole range is hole is not allocated at all, because a slot of zero
    // is how a volume records a gap nobody wrote.
    var singleCount = Math.Min(ZonePointersPerBlock, dataZoneCount - idx);
    var singleBase = idx;
    if (AnyAllocated(dataZones, singleBase, singleCount)) {
      var single = allocator.Allocate();
      slots[IndirectSlot] = single;
      if (disk != null) {
        MarkZone(disk, zmapOff, firstDataZone, single);
        WritePointerBlock(disk, single, dataZones, singleBase, singleCount);
      }
    }
    idx += singleCount;
    if (idx == dataZoneCount) return slots;

    // Double-indirect: a pointer block of single-indirect blocks.
    var singleBlocks = (dataZoneCount - idx + ZonePointersPerBlock - 1) / ZonePointersPerBlock;
    if (singleBlocks > ZonePointersPerBlock)
      throw new InvalidOperationException(
        "MinixV1 writer: file exceeds double-indirect addressing capacity.");
    if (!AnyAllocated(dataZones, idx, dataZoneCount - idx)) return slots;

    var dbl = allocator.Allocate();
    slots[DoubleIndirectSlot] = dbl;
    if (disk != null) MarkZone(disk, zmapOff, firstDataZone, dbl);

    var dblPtrs = new uint[singleBlocks];
    for (var s = 0; s < singleBlocks; s++) {
      var count = Math.Min(ZonePointersPerBlock, dataZoneCount - idx);
      if (AnyAllocated(dataZones, idx, count)) {
        var single2 = allocator.Allocate();
        dblPtrs[s] = single2;
        if (disk != null) {
          MarkZone(disk, zmapOff, firstDataZone, single2);
          WritePointerBlock(disk, single2, dataZones, idx, count);
        }
      }
      idx += count;
    }
    if (disk != null) WritePointerBlock(disk, dbl, dblPtrs, 0, singleBlocks);
    return slots;
  }

  /// <summary>Whether any zone in the range holds data rather than being hole.</summary>
  private static bool AnyAllocated(uint[] zones, int start, int count) {
    for (var i = start; i < start + count && i < zones.Length; ++i)
      if (zones[i] != 0) return true;
    return false;
  }

  /// <summary>Which of a payload's zones hold nothing but zeros.</summary>
  /// <remarks>
  /// Empty when holes are not wanted, so every caller can ask without checking
  /// first and <see cref="WriteData" /> has one shape either way.
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
  /// <remarks>
  /// The length goes in as well as the digest: the digest is what says two files
  /// are the same, and the length is what makes saying so cheap to be sure of.
  /// </remarks>
  private static string ContentKey(byte[] data) =>
    data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
    + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

  private static void WritePointerBlock(byte[] disk, uint zone, uint[] ptrs, int offset, int count) {
    var baseByte = (long)zone * BlockSize;
    for (var i = 0; i < count; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(
        disk.AsSpan((int)(baseByte + i * 2)), (ushort)ptrs[offset + i]);
  }


  private List<string> SplitPath(string path) {
    var parts = new List<string>();
    foreach (var raw in path.Replace('\\', '/').Split('/')) {
      if (raw.Length == 0 || raw is "." or "..") continue;
      var component = raw.Length > this.MaxNameLength ? raw[..this.MaxNameLength] : raw;
      parts.Add(component);
    }
    return parts;
  }

  private static void AddChild(TreeNode parent, string name, TreeNode child) {
    parent.Children.Add(new KeyValuePair<string, TreeNode>(name, child));
    parent.ChildIndex[name] = child;
  }

  private void WriteDirEntry(byte[] dirData, int offset, uint inode, string name) {
    BinaryPrimitives.WriteUInt16LittleEndian(dirData.AsSpan(offset), (ushort)inode);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, this.MaxNameLength);
    nameBytes.AsSpan(0, copyLen).CopyTo(dirData.AsSpan(offset + 2));
  }

  // V1 inode (32 bytes): mode(0,u16) uid(2,u16) size(4,u32) time(8,u32)
  //   gid(12,u8) nlinks(13,u8) zones[9](14..31, u16 each).
  private static void WriteInode(byte[] disk, int tableOff, int inodeNumber,
      ushort mode, uint size, byte nlinks, uint[] zones) {
    var off = tableOff + (inodeNumber - 1) * InodeSize;
    var span = disk.AsSpan(off, InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(span, mode);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), size);
    span[13] = nlinks;
    for (var i = 0; i < 9; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(14 + i * 2), (ushort)zones[i]);
  }

  private static void MarkZone(byte[] disk, int zmapOff, int firstDataZone, uint zone) =>
    SetBit(disk, zmapOff, (int)zone - firstDataZone + 1);

  private static void SetBit(byte[] data, int bitmapOffset, int bitIndex) {
    data[bitmapOffset + bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (!_leaveOpen) _output.Dispose();
  }
}

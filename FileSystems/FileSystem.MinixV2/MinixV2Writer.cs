#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixV2;

/// <summary>
/// Builds minimal but spec-correct Minix v2 filesystem images (1991). v2 keeps
/// the v1 superblock and 1024-byte blocks but widens inodes to 64 bytes, zone
/// numbers to 32 bits, and adds a triple-indirect zone for large files.
/// Directory names are 14 bytes (magic <c>0x2468</c>) or 30 bytes (magic
/// <c>0x2478</c>). Every path component becomes a real directory inode with its
/// own <c>"."</c>/<c>".."</c> entries, so a file added as <c>"a/b/c.txt"</c> is
/// stored under nested directories <c>a</c> and <c>a/b</c>. Files larger than
/// the 7 direct zones (7168 bytes) spill into the single-indirect zone
/// (256 further zones), then the double-indirect zone, then the
/// triple-indirect zone.
/// </summary>
public sealed class MinixV2Writer : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly int _nameLength;
  private readonly ushort _magic;
  private readonly List<(string Path, byte[] Data)> _files = [];

  private const ushort MagicV2_14 = 0x2468;
  private const ushort MagicV2_30 = 0x2478;
  private const int BlockSize = 1024;
  private const int SuperblockOff = 1024; // block 1
  private const int InodeSize = 64;
  private const int ZonePointersPerBlock = BlockSize / 4; // 256 (32-bit pointers)

  // V2 inode zone slots: 7 direct, single-, double-, triple-indirect.
  private const int DirectZones = 7;
  private const int IndirectSlot = 7;
  private const int DoubleIndirectSlot = 8;
  private const int TripleIndirectSlot = 9;

  // mkfs.minix emits this constant for v2 s_max_size.
  private const uint MaxSizeV2 = 0x7FFFFFFF;

  private const ushort ModeDirectory   = 0x41ED; // S_IFDIR | 0755
  private const ushort ModeRegularFile = 0x81A4; // S_IFREG | 0644

  /// <summary>
  /// Creates a writer for the given output stream. <paramref name="longNames"/>
  /// selects the 30-byte-name variant (magic 0x2478); the default is the
  /// 14-byte-name layout (magic 0x2468).
  /// </summary>
  public MinixV2Writer(Stream output, bool leaveOpen = false, bool longNames = false) {
    _output = output;
    _leaveOpen = leaveOpen;
    _nameLength = longNames ? 30 : 14;
    _magic = longNames ? MagicV2_30 : MagicV2_14;
  }

  private int DirEntrySize => 2 + _nameLength;
  private int MaxNameLength => _nameLength - 1;

  /// <summary>Registers a file to be written into the image.</summary>
  public void AddFile(string path, byte[] data) => _files.Add((path, data));

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
  /// <para>The indirect blocks go the same way. One whose whole range is hole is
  /// not allocated either, and its slot in the inode stays zero, because that is
  /// what a volume looks like when the file was written by seeking past the gap:
  /// the kernel never asks for a block it is not filling. Allocating a block of
  /// nothing but zero pointers would read back identically and still be a
  /// volume no minix system would have produced.</para>
  /// </remarks>
  public bool MakeSparse { get; set; }

  /// <summary>
  /// Store one copy of files whose bytes are identical and give the rest a
  /// second name for it.
  /// </summary>
  /// <remarks>
  /// One inode, one set of zones, and a count in the inode of how many directory
  /// entries name it — that is all a hard link is, and minix has had it since
  /// the beginning. <c>fsck.minix</c> counts the entries pointing at each inode
  /// and compares them against that field, so linking is a matter of naming the
  /// same inode twice and saying so.
  /// </remarks>
  public bool DeduplicateWithLinks { get; set; }

  /// <summary>A v2 inode counts its names in sixteen bits.</summary>
  private const int MaxLinks = ushort.MaxValue;

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

  /// <summary>Builds and writes the Minix v2 filesystem image.</summary>
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

    root.Inode = 1;
    var nextInode = 2u;
    foreach (var dir in allDirs) {
      if (dir == root) continue;
      dir.Inode = nextInode++;
    }

    // Files whose bytes are identical share one inode when asked; the rest of
    // the build then only ever sees the stored ones, so the content is laid down
    // once however many names lead to it.
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
      if (this.DeduplicateWithLinks) firstWithContent[key] = file;
      stored.Add(file);
    }
    var totalInodes = (int)nextInode - 1;

    var inodesPerBlock = BlockSize / InodeSize; // 16
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;
    // Advertise every physical inode slot in the (whole-block) inode table so a
    // genuine in-place writer can claim spare inodes without re-laying-out the
    // image — the surplus slots stay free in the imap, as on a real mkfs volume.
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

    SetBit(disk, imapOff, 0);
    for (var ino = 1; ino <= totalInodes; ino++)
      SetBit(disk, imapOff, ino);

    SetBit(disk, zmapOff, 0);

    var allocator = new ZoneAllocator(firstDataZone);

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
        (uint)dirBytes.Length, (ushort)(2 + childDirCount), zones);
    }

    foreach (var file in stored) {
      var zones = WriteData(disk, zmapOff, firstDataZone, allocator, file.FileData, holes[file]);
      WriteInode(disk, inodeTableOff, (int)file.Inode, ModeRegularFile,
        (uint)file.FileData.Length, (ushort)file.Links, zones);
    }

    // --- Superblock at offset 1024 ---
    // v2 keeps the v1 16-bit s_nzones (0 here) and carries the real zone count
    // in the 32-bit s_zones field at +20.
    var sb = disk.AsSpan(SuperblockOff);
    BinaryPrimitives.WriteUInt16LittleEndian(sb,           (ushort)advertisedInodes); // s_ninodes
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(2),  0);                   // s_nzones (v1 field, unused)
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(4),  imapBlocks);          // s_imap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(6),  zmapBlocks);          // s_zmap_blocks
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(8),  (ushort)firstDataZone); // s_firstdatazone
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(10), 0);                   // s_log_zone_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(12), MaxSizeV2);           // s_max_size
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(16), _magic);              // s_magic
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(20), (uint)totalZones);    // s_zones (v2 32-bit)

    return disk;
  }

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
            $"MinixV2: path component '{component}' is used as both a file and a directory.");
        }
        cursor = next;
      }

      var leaf = parts[^1];
      if (cursor.ChildIndex.ContainsKey(leaf))
        throw new InvalidOperationException($"MinixV2: duplicate entry '{rawPath}'.");
      var fileNode = new TreeNode { IsDirectory = false, FileData = data, Parent = cursor };
      AddChild(cursor, leaf, fileNode);
      allFiles.Add(fileNode);
    }
  }

  private sealed class ZoneAllocator(int firstDataZone) {
    private int _next = firstDataZone;

    /// <summary>The zone number the next allocation would take.</summary>
    public int Next => _next;

    public uint Allocate() => (uint)_next++;
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

  // Writes a payload across direct, single-, double- and triple-indirect zones.
  // A null disk allocates without writing, which is how the volume is sized.
  private uint[] WriteData(byte[]? disk, int zmapOff, int firstDataZone,
      ZoneAllocator allocator, byte[] data, bool[] holes) {
    var slots = new uint[10];
    if (data.Length == 0) return slots;

    var dataZoneCount = (data.Length + BlockSize - 1) / BlockSize;
    var dataZones = new uint[dataZoneCount];
    for (var z = 0; z < dataZoneCount; z++) {
      // A hole keeps its place in the pointer list and takes no zone: the slot
      // stays zero, and a reader hands back a block of zeros for it.
      if (z < holes.Length && holes[z]) continue;

      var zone = allocator.Allocate();
      dataZones[z] = zone;
      if (disk == null) continue;

      MarkZone(disk, zmapOff, firstDataZone, zone);
      var at = z * BlockSize;
      Array.Copy(data, at, disk, (long)zone * BlockSize, Math.Min(BlockSize, data.Length - at));
    }

    var idx = 0;
    for (; idx < dataZoneCount && idx < DirectZones; idx++)
      slots[idx] = dataZones[idx];
    if (idx == dataZoneCount) return slots;

    // Single-indirect.
    slots[IndirectSlot] = BuildIndirect(disk, zmapOff, firstDataZone, allocator,
      dataZones, ref idx, dataZoneCount, level: 1);
    if (idx == dataZoneCount) return slots;

    // Double-indirect.
    slots[DoubleIndirectSlot] = BuildIndirect(disk, zmapOff, firstDataZone, allocator,
      dataZones, ref idx, dataZoneCount, level: 2);
    if (idx == dataZoneCount) return slots;

    // Triple-indirect.
    slots[TripleIndirectSlot] = BuildIndirect(disk, zmapOff, firstDataZone, allocator,
      dataZones, ref idx, dataZoneCount, level: 3);
    if (idx != dataZoneCount)
      throw new InvalidOperationException(
        "MinixV2 writer: file exceeds triple-indirect addressing capacity.");
    return slots;
  }

  // Builds one indirect tree of the given level rooted at a freshly allocated
  // zone, consuming as many data zones from <paramref name="dataZones"/> as the
  // level can address (up to 256^level), and returns the root zone number.
  // Returns zero, having taken no zone, when everything it would address is
  // hole — an inode slot of zero is how a volume records a gap nobody wrote.
  private uint BuildIndirect(byte[]? disk, int zmapOff, int firstDataZone,
      ZoneAllocator allocator, uint[] dataZones, ref int idx, int total, int level) {
    var end = Math.Min(total, idx + Capacity(level));
    var allHole = true;
    for (var probe = idx; probe < end; probe++)
      if (dataZones[probe] != 0) { allHole = false; break; }

    if (allHole) {
      idx = end;
      return 0;
    }

    var root = allocator.Allocate();
    var baseByte = (long)root * BlockSize;
    if (disk != null) MarkZone(disk, zmapOff, firstDataZone, root);

    if (level == 1) {
      var count = Math.Min(ZonePointersPerBlock, total - idx);
      for (var i = 0; i < count; i++) {
        var zone = dataZones[idx++];
        if (disk != null)
          BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan((int)(baseByte + i * 4)), zone);
      }
      return root;
    }

    for (var i = 0; i < ZonePointersPerBlock && idx < total; i++) {
      var child = BuildIndirect(disk, zmapOff, firstDataZone, allocator,
        dataZones, ref idx, total, level - 1);
      if (disk != null)
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan((int)(baseByte + i * 4)), child);
    }
    return root;
  }

  /// <summary>How many data zones an indirect tree of this level addresses.</summary>
  private static int Capacity(int level) {
    var capacity = ZonePointersPerBlock;
    for (var i = 1; i < level; i++) capacity *= ZonePointersPerBlock;
    return capacity;
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

  // V2 inode (64 bytes): mode(0) nlinks(2) uid(4) gid(6) size(8,u32)
  //   atime(12) mtime(16) ctime(20) zones[10](24..63, u32 each).
  private static void WriteInode(byte[] disk, int tableOff, int inodeNumber,
      ushort mode, uint size, ushort nlinks, uint[] zones) {
    var off = tableOff + (inodeNumber - 1) * InodeSize;
    var span = disk.AsSpan(off, InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(span, mode);
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), nlinks);
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), size);
    for (var i = 0; i < 10; i++)
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24 + i * 4), zones[i]);
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

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
  private sealed class TreeNode {
    public uint Inode;
    public bool IsDirectory;
    public byte[] FileData = [];
    public TreeNode? Parent;
    public readonly List<KeyValuePair<string, TreeNode>> Children = [];
    public readonly Dictionary<string, TreeNode> ChildIndex = [];
  }

  /// <summary>Builds and writes the Minix v1 filesystem image.</summary>
  public void Finish() => _output.Write(this.Build());

  /// <summary>Builds the complete image in memory and returns its bytes.</summary>
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
    foreach (var file in allFiles)
      file.Inode = nextInode++;
    var totalInodes = (int)nextInode - 1;

    // --- Layout: block0 boot, block1 superblock, imap, zmap, inode table, data.
    var inodesPerBlock = BlockSize / InodeSize; // 32
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;
    const int imapBlocks = 1;
    const int zmapBlocks = 1;
    var firstDataZone = 2 + imapBlocks + zmapBlocks + inodeTableBlocks;

    // Estimate the data zones (incl. indirect-pointer blocks) needed so we can
    // size the image, then allocate them precisely during the write pass.
    var dataZonesNeeded = 0;
    foreach (var dir in allDirs)
      dataZonesNeeded += ZonesForByteLength(DirectoryByteLength(dir));
    foreach (var file in allFiles)
      dataZonesNeeded += ZonesForByteLength(file.FileData.Length);

    var totalZones = firstDataZone + dataZonesNeeded;
    var disk = new byte[(long)totalZones * BlockSize];

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
      var entries = new List<(uint Inode, string Name)>(dir.Children.Count + 2) {
        (dir.Inode, "."),
        ((dir.Parent ?? root).Inode, ".."),
      };
      foreach (var (childName, child) in dir.Children)
        entries.Add((child.Inode, childName));

      var dirBytes = new byte[entries.Count * this.DirEntrySize];
      for (var e = 0; e < entries.Count; e++)
        WriteDirEntry(dirBytes, e * this.DirEntrySize, entries[e].Inode, entries[e].Name);

      var childDirCount = 0;
      foreach (var (_, child) in dir.Children)
        if (child.IsDirectory) childDirCount++;

      var zones = WriteData(disk, zmapOff, firstDataZone, allocator, dirBytes);
      WriteInode(disk, inodeTableOff, (int)dir.Inode, ModeDirectory,
        (uint)dirBytes.Length, (byte)(2 + childDirCount), zones);
    }

    // --- Files ---
    foreach (var file in allFiles) {
      var zones = WriteData(disk, zmapOff, firstDataZone, allocator, file.FileData);
      WriteInode(disk, inodeTableOff, (int)file.Inode, ModeRegularFile,
        (uint)file.FileData.Length, nlinks: 1, zones);
    }

    // --- Superblock at offset 1024 ---
    var sb = disk.AsSpan(SuperblockOff);
    BinaryPrimitives.WriteUInt16LittleEndian(sb,           (ushort)totalInodes); // s_ninodes
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
    public uint Allocate() => (uint)_next++;
  }

  // Writes file/directory bytes across direct, single- and double-indirect
  // zones, marks each allocated zone in the bitmap, and returns the 9 inode
  // zone slots.
  private uint[] WriteData(byte[] disk, int zmapOff, int firstDataZone,
      ZoneAllocator allocator, byte[] data) {
    var slots = new uint[9];
    if (data.Length == 0) return slots;

    var dataZoneCount = (data.Length + BlockSize - 1) / BlockSize;
    var written = 0;

    // The flat list of data zones, allocated and filled in order.
    var dataZones = new uint[dataZoneCount];
    for (var z = 0; z < dataZoneCount; z++) {
      var zone = allocator.Allocate();
      MarkZone(disk, zmapOff, firstDataZone, zone);
      dataZones[z] = zone;
      var toWrite = Math.Min(BlockSize, data.Length - written);
      Array.Copy(data, written, disk, (long)zone * BlockSize, toWrite);
      written += toWrite;
    }

    // Direct slots.
    var idx = 0;
    for (; idx < dataZoneCount && idx < DirectZones; idx++)
      slots[idx] = dataZones[idx];
    if (idx == dataZoneCount) return slots;

    // Single-indirect: one pointer block addressing up to 512 data zones.
    var single = allocator.Allocate();
    MarkZone(disk, zmapOff, firstDataZone, single);
    slots[IndirectSlot] = single;
    var singleCount = Math.Min(ZonePointersPerBlock, dataZoneCount - idx);
    var singleBase = idx;
    WritePointerBlock(disk, single, dataZones, singleBase, singleCount);
    idx += singleCount;
    if (idx == dataZoneCount) return slots;

    // Double-indirect: a pointer block of single-indirect blocks.
    var dbl = allocator.Allocate();
    MarkZone(disk, zmapOff, firstDataZone, dbl);
    slots[DoubleIndirectSlot] = dbl;
    var singleBlocks = (dataZoneCount - idx + ZonePointersPerBlock - 1) / ZonePointersPerBlock;
    if (singleBlocks > ZonePointersPerBlock)
      throw new InvalidOperationException(
        "MinixV1 writer: file exceeds double-indirect addressing capacity.");
    var dblPtrs = new uint[singleBlocks];
    for (var s = 0; s < singleBlocks; s++) {
      var single2 = allocator.Allocate();
      MarkZone(disk, zmapOff, firstDataZone, single2);
      dblPtrs[s] = single2;
      var count = Math.Min(ZonePointersPerBlock, dataZoneCount - idx);
      WritePointerBlock(disk, single2, dataZones, idx, count);
      idx += count;
    }
    WritePointerBlock(disk, dbl, dblPtrs, 0, singleBlocks);
    return slots;
  }

  private static void WritePointerBlock(byte[] disk, uint zone, uint[] ptrs, int offset, int count) {
    var baseByte = (long)zone * BlockSize;
    for (var i = 0; i < count; i++)
      BinaryPrimitives.WriteUInt16LittleEndian(
        disk.AsSpan((int)(baseByte + i * 2)), (ushort)ptrs[offset + i]);
  }

  // Number of zones (data + indirect-pointer blocks) a byte payload occupies.
  private static int ZonesForByteLength(int length) {
    if (length == 0) return 0;
    var dataZones = (length + BlockSize - 1) / BlockSize;
    var total = dataZones;
    var remaining = dataZones - DirectZones;
    if (remaining > 0) {
      total += 1; // single-indirect block
      remaining -= ZonePointersPerBlock;
    }
    if (remaining > 0) {
      total += 1; // double-indirect block
      total += (remaining + ZonePointersPerBlock - 1) / ZonePointersPerBlock; // its single blocks
    }
    return total;
  }

  private int DirectoryByteLength(TreeNode dir) =>
    (2 + dir.Children.Count) * this.DirEntrySize;

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

  public void Dispose() {
    if (!_leaveOpen) _output.Dispose();
  }
}

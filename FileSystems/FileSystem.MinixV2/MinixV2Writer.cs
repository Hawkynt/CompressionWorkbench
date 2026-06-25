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

  private sealed class TreeNode {
    public uint Inode;
    public bool IsDirectory;
    public byte[] FileData = [];
    public TreeNode? Parent;
    public readonly List<KeyValuePair<string, TreeNode>> Children = [];
    public readonly Dictionary<string, TreeNode> ChildIndex = [];
  }

  /// <summary>Builds and writes the Minix v2 filesystem image.</summary>
  public void Finish() => _output.Write(this.Build());

  /// <summary>Builds the complete image in memory and returns its bytes.</summary>
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
    foreach (var file in allFiles)
      file.Inode = nextInode++;
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

    SetBit(disk, imapOff, 0);
    for (var ino = 1; ino <= totalInodes; ino++)
      SetBit(disk, imapOff, ino);

    SetBit(disk, zmapOff, 0);

    var allocator = new ZoneAllocator(firstDataZone);

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
        (uint)dirBytes.Length, (ushort)(2 + childDirCount), zones);
    }

    foreach (var file in allFiles) {
      var zones = WriteData(disk, zmapOff, firstDataZone, allocator, file.FileData);
      WriteInode(disk, inodeTableOff, (int)file.Inode, ModeRegularFile,
        (uint)file.FileData.Length, nlinks: 1, zones);
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
    public uint Allocate() => (uint)_next++;
  }

  // Writes a payload across direct, single-, double- and triple-indirect zones.
  private uint[] WriteData(byte[] disk, int zmapOff, int firstDataZone,
      ZoneAllocator allocator, byte[] data) {
    var slots = new uint[10];
    if (data.Length == 0) return slots;

    var dataZoneCount = (data.Length + BlockSize - 1) / BlockSize;
    var written = 0;
    var dataZones = new uint[dataZoneCount];
    for (var z = 0; z < dataZoneCount; z++) {
      var zone = allocator.Allocate();
      MarkZone(disk, zmapOff, firstDataZone, zone);
      dataZones[z] = zone;
      var toWrite = Math.Min(BlockSize, data.Length - written);
      Array.Copy(data, written, disk, (long)zone * BlockSize, toWrite);
      written += toWrite;
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
  private uint BuildIndirect(byte[] disk, int zmapOff, int firstDataZone,
      ZoneAllocator allocator, uint[] dataZones, ref int idx, int total, int level) {
    var root = allocator.Allocate();
    MarkZone(disk, zmapOff, firstDataZone, root);
    var baseByte = (long)root * BlockSize;

    if (level == 1) {
      var count = Math.Min(ZonePointersPerBlock, total - idx);
      for (var i = 0; i < count; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(
          disk.AsSpan((int)(baseByte + i * 4)), dataZones[idx++]);
      return root;
    }

    for (var i = 0; i < ZonePointersPerBlock && idx < total; i++) {
      var child = BuildIndirect(disk, zmapOff, firstDataZone, allocator,
        dataZones, ref idx, total, level - 1);
      BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan((int)(baseByte + i * 4)), child);
    }
    return root;
  }

  // Number of zones (data + indirect-pointer blocks) a payload occupies.
  private static int ZonesForByteLength(int length) {
    if (length == 0) return 0;
    var dataZones = (length + BlockSize - 1) / BlockSize;
    var total = dataZones;
    var remaining = dataZones - DirectZones;
    if (remaining <= 0) return total;

    var perSingle = ZonePointersPerBlock;
    // Single-indirect.
    var take = Math.Min(remaining, perSingle);
    total += 1;
    remaining -= take;
    if (remaining <= 0) return total;

    // Double-indirect.
    var perDouble = perSingle * ZonePointersPerBlock;
    take = Math.Min(remaining, perDouble);
    total += 1 + (take + perSingle - 1) / perSingle;
    remaining -= take;
    if (remaining <= 0) return total;

    // Triple-indirect.
    total += 1; // top block
    var singles = (remaining + perSingle - 1) / perSingle;
    total += singles;
    var doubles = (singles + ZonePointersPerBlock - 1) / ZonePointersPerBlock;
    total += doubles;
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

  public void Dispose() {
    if (!_leaveOpen) _output.Dispose();
  }
}

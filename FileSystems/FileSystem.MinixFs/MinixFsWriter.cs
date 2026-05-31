#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.MinixFs;

/// <summary>
/// Builds minimal Minix v3 filesystem images. Uses 1024-byte blocks and creates
/// real directory inodes for every path component, so a file added as
/// <c>"a/b/c.txt"</c> is stored under nested directories <c>a</c> and <c>a/b</c>.
/// Files are stored using direct zone pointers (up to 7 direct zones per file =
/// up to 7168 bytes with 1K blocks). Each directory occupies a single zone, so a
/// directory may hold at most 16 entries (including "." and "..").
/// </summary>
public sealed class MinixFsWriter : IDisposable {
  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, byte[] Data)> _files = [];

  private const ushort MagicV3 = 0x4D5A;
  private const int BlockSize = 1024;
  private const int BootBlockSize = 1024; // block 0: boot block (unused)
  // Superblock is always at byte offset 1024
  private const int SuperblockOff = 1024;
  private const int V3InodeSize = 64;

  public MinixFsWriter(Stream output, bool leaveOpen = false) {
    _output = output;
    _leaveOpen = leaveOpen;
  }

  /// <summary>Registers a file to be written into the image.</summary>
  public void AddFile(string path, byte[] data) => _files.Add((path, data));

  // V3 dir entry: uint32 inode (4 bytes) + char[60] name (60 bytes) = 64 bytes per entry.
  private const int DirEntrySize = 64;
  private const int MaxNameLength = 59; // 60-byte name field minus a trailing NUL.
  // A directory occupies exactly one zone, so it can hold at most this many entries.
  private const int MaxEntriesPerDir = BlockSize / DirEntrySize; // 16

  private const ushort ModeDirectory   = 0x41ED; // S_IFDIR | 0755
  private const ushort ModeRegularFile = 0x81A4; // S_IFREG | 0644

  // A node in the in-memory directory tree built from the registered paths.
  private sealed class TreeNode {
    public uint Inode;
    public bool IsDirectory;
    public byte[] FileData = [];
    // Child directories/files keyed by their (single-component) name, in
    // insertion order so the on-disk layout is deterministic.
    public readonly List<KeyValuePair<string, TreeNode>> Children = [];
    public readonly Dictionary<string, TreeNode> ChildIndex = [];
  }

  /// <summary>
  /// Builds and writes the Minix v3 filesystem image to the output stream.
  /// </summary>
  public void Finish() {
    // --- Build the directory tree from the registered paths ---
    // Every path component becomes a real directory inode; the final
    // component becomes a file inode. The root is inode 1.
    var root = new TreeNode { IsDirectory = true };
    var allDirs = new List<TreeNode> { root };
    var allFiles = new List<TreeNode>();

    foreach (var (rawPath, data) in _files) {
      var parts = SplitPath(rawPath);
      if (parts.Count == 0) continue;

      var cursor = root;
      // Walk/create every intermediate directory component.
      for (var i = 0; i < parts.Count - 1; i++) {
        var component = parts[i];
        if (!cursor.ChildIndex.TryGetValue(component, out var next)) {
          next = new TreeNode { IsDirectory = true };
          AddChild(cursor, component, next);
          allDirs.Add(next);
        } else if (!next.IsDirectory) {
          throw new InvalidOperationException(
            $"MinixFs: path component '{component}' is used as both a file and a directory.");
        }
        cursor = next;
      }

      // Final component: the file itself.
      var leaf = parts[^1];
      if (cursor.ChildIndex.ContainsKey(leaf))
        throw new InvalidOperationException($"MinixFs: duplicate entry '{rawPath}'.");
      var fileNode = new TreeNode { IsDirectory = false, FileData = data };
      AddChild(cursor, leaf, fileNode);
      allFiles.Add(fileNode);
    }

    // Enforce the single-zone-per-directory limit early with a clear message.
    EnsureDirectoriesFitOneZone(root, "");

    // --- Assign 1-based inode numbers: root = 1, then dirs, then files ---
    // Root keeps inode 1 so the reader (which starts at inode 1) finds it.
    root.Inode = 1;
    var nextInode = 2u;
    foreach (var dir in allDirs) {
      if (dir == root) continue;
      dir.Inode = nextInode++;
    }
    foreach (var file in allFiles)
      file.Inode = nextInode++;
    var totalInodes = (int)nextInode - 1;

    // --- Layout calculation ---
    // Block 0:  boot block (1024 bytes, unused)
    // Block 1:  superblock (1024 bytes at offset 1024)
    // Block 2 onwards: inode bitmap (1 block), zone bitmap (1 block),
    //                  inode table, then data zones.
    var inodesPerBlock = BlockSize / V3InodeSize;
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;

    // We keep 1 block each for inode bitmap and zone bitmap.
    const int imapBlocks = 1;
    const int zmapBlocks = 1;

    // firstdatazone = block index of first data zone
    // Layout: block0 (boot) + block1 (superblock) + imapBlocks + zmapBlocks + inodeTableBlocks
    var firstdatazone = 2 + imapBlocks + zmapBlocks + inodeTableBlocks;

    // Zones needed: one per directory (single zone each) + ceil(size/blocksize) per file.
    var dataZonesNeeded = allDirs.Count;
    foreach (var file in allFiles)
      dataZonesNeeded += file.FileData.Length == 0 ? 0 : (file.FileData.Length + BlockSize - 1) / BlockSize;

    var totalZones = firstdatazone + dataZonesNeeded;
    var totalBlocks = totalZones; // zones == blocks for log_zone_size=0
    var diskSize = totalBlocks * BlockSize;
    var disk = new byte[diskSize];

    // --- Bitmap / inode-table offsets ---
    var imapOff = 2 * BlockSize;        // inode bitmap: block 2
    var zmapOff = 3 * BlockSize;        // zone bitmap:  block 3
    var inodeTableOff = 4 * BlockSize;  // inode table:  block 4

    // Mark every used inode (1..totalInodes) in the inode bitmap.
    // Minix inodes are 1-based; bit 0 of byte 0 = inode 1.
    for (var ino = 1; ino <= totalInodes; ino++)
      SetBit(disk, imapOff, ino - 1);

    // Mark all metadata zones (0..firstdatazone-1) as used in the zone bitmap.
    for (var z = 0; z < firstdatazone; z++)
      SetBit(disk, zmapOff, z);

    var nextZone = firstdatazone;

    // --- Allocate and write each directory's zone, then its inode ---
    // Parent link counts: a directory's i_nlinks = 2 (self "." + parent's entry)
    // plus one extra per child directory (the child's ".." points back).
    foreach (var dir in allDirs) {
      var dirZone = nextZone++;
      SetBit(disk, zmapOff, dirZone);

      var dirData = new byte[BlockSize];
      var pos = 0;
      // "." -> self
      WriteDirEntry(dirData, pos, dir.Inode, ".");
      pos += DirEntrySize;
      // ".." -> parent. The tree is walked parent-first, so we resolve the
      // parent inode by carrying it in the recursion below; here we patch it
      // after the fact via ParentOf lookup.
      WriteDirEntry(dirData, pos, ParentInode(root, dir), "..");
      pos += DirEntrySize;

      foreach (var (childName, child) in dir.Children) {
        WriteDirEntry(dirData, pos, child.Inode, childName);
        pos += DirEntrySize;
      }

      dirData.CopyTo(disk, dirZone * BlockSize);

      var childDirCount = 0;
      foreach (var (_, child) in dir.Children)
        if (child.IsDirectory) childDirCount++;

      WriteV3Inode(disk, inodeTableOff, inodeIndex: (int)(dir.Inode - 1),
        mode: ModeDirectory,
        size: (uint)BlockSize,
        nlinks: (ushort)(2 + childDirCount),
        zones: [(uint)dirZone, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    }

    // --- Allocate and write each file's zones, then its inode ---
    foreach (var file in allFiles) {
      var data = file.FileData;
      var zonesNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
      if (zonesNeeded > 7)
        throw new InvalidOperationException(
          $"MinixFs writer only supports direct zones (max {7 * BlockSize} bytes per file).");

      var fileZones = new uint[10]; // 10 zone slots in a V3 inode
      for (var z = 0; z < zonesNeeded; z++) {
        fileZones[z] = (uint)nextZone;
        SetBit(disk, zmapOff, nextZone);
        nextZone++;
      }

      WriteV3Inode(disk, inodeTableOff, inodeIndex: (int)(file.Inode - 1),
        mode: ModeRegularFile,
        size: (uint)data.Length,
        nlinks: 1,
        zones: fileZones);

      var written = 0;
      for (var z = 0; z < 7 && written < data.Length; z++) {
        if (fileZones[z] == 0) break;
        var toWrite = Math.Min(BlockSize, data.Length - written);
        Array.Copy(data, written, disk, (int)fileZones[z] * BlockSize, toWrite);
        written += toWrite;
      }
    }

    // --- Superblock at offset 1024 ---
    // V3 superblock layout (little-endian):
    //  uint32 s_ninodes        [0]
    //  uint16 s_pad0           [4]
    //  uint16 s_imap_blocks    [6]
    //  uint16 s_zmap_blocks    [8]
    //  uint16 s_firstdatazone  [10]
    //  uint16 s_log_zone_size  [12]
    //  uint16 s_pad1           [14]
    //  uint32 s_max_size       [16]
    //  uint32 s_zones          [20]
    //  uint16 s_magic          [24]
    //  uint16 s_pad2           [26]
    //  uint16 s_blocksize      [28]
    //  uint8  s_disk_version   [30]
    var sb = disk.AsSpan(SuperblockOff);
    BinaryPrimitives.WriteUInt32LittleEndian(sb,              (uint)totalInodes);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(6),     imapBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(8),     zmapBlocks);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(10),    (ushort)firstdatazone);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(12),    0); // log_zone_size = 0 (zone==block)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(16),    (uint)diskSize); // s_max_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(20),    (uint)totalZones);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(24),    MagicV3);
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(28),    BlockSize);

    _output.Write(disk);
  }

  // Splits a registered path into its directory/file components, discarding
  // empty segments produced by leading/trailing/duplicate separators.
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

  // Resolves the parent inode of a directory node (root's parent is itself).
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

  // A directory's entries (".", "..", plus children) must fit in a single zone.
  private static void EnsureDirectoriesFitOneZone(TreeNode dir, string path) {
    var entryCount = 2 + dir.Children.Count; // "." and ".."
    if (entryCount > MaxEntriesPerDir)
      throw new InvalidOperationException(
        $"MinixFs writer stores each directory in a single {BlockSize}-byte zone " +
        $"(max {MaxEntriesPerDir - 2} entries); directory " +
        $"'{(path.Length == 0 ? "/" : path)}' has {dir.Children.Count} entries.");
    foreach (var (name, child) in dir.Children)
      if (child.IsDirectory)
        EnsureDirectoriesFitOneZone(child, path.Length == 0 ? name : $"{path}/{name}");
  }

  private static void WriteDirEntry(byte[] dirData, int offset, uint inode, string name) {
    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(offset), inode);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, MaxNameLength);
    nameBytes.AsSpan(0, copyLen).CopyTo(dirData.AsSpan(offset + 4));
    // null terminator already present (array zero-initialized)
  }

  private static void WriteV3Inode(byte[] disk, int tableOff, int inodeIndex,
      ushort mode, uint size, ushort nlinks, uint[] zones) {
    // Real Minix3 inode layout (little-endian, 64 bytes total):
    //   uint16 i_mode   [0]
    //   uint16 i_nlinks [2]
    //   uint16 i_uid    [4]
    //   uint16 i_gid    [6]
    //   uint32 i_size   [8]
    //   uint32 i_atime  [12]
    //   uint32 i_mtime  [16]
    //   uint32 i_ctime  [20]
    //   uint32 i_zone[10] [24..63]
    var off = tableOff + inodeIndex * V3InodeSize;
    var span = disk.AsSpan(off, V3InodeSize);
    BinaryPrimitives.WriteUInt16LittleEndian(span,          mode);
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), nlinks); // i_nlinks
    BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), size); // i_size
    // i_zone[10] at offset 24
    for (var i = 0; i < Math.Min(zones.Length, 10); i++)
      BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24 + i * 4), zones[i]);
  }

  private static void SetBit(byte[] data, int bitmapOffset, int bitIndex) {
    data[bitmapOffset + bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
  }

  public void Dispose() {
    if (!_leaveOpen) _output.Dispose();
  }
}

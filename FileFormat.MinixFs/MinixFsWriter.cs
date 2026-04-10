#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.MinixFs;

/// <summary>
/// Builds minimal Minix v3 filesystem images. Uses 1024-byte blocks with a flat
/// root directory (no subdirectory creation). Files are stored using direct zone
/// pointers (up to 7 direct zones per file = up to 7168 bytes with 1K blocks).
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

  /// <summary>
  /// Builds and writes the Minix v3 filesystem image to the output stream.
  /// </summary>
  public void Finish() {
    // --- Layout calculation ---
    // Block 0:  boot block (1024 bytes, unused)
    // Block 1:  superblock (1024 bytes at offset 1024)
    // Block 2 onwards: inode bitmap (1 block), zone bitmap (1 block),
    //                  inode table, then data zones.

    // Inodes needed: 1 (root dir) + 1 per file
    var totalInodes = 1 + _files.Count;
    // Round up to fill a whole block
    var inodesPerBlock = BlockSize / V3InodeSize;
    var inodeTableBlocks = (totalInodes + inodesPerBlock - 1) / inodesPerBlock;

    // We keep 1 block each for inode bitmap and zone bitmap.
    const int imapBlocks = 1;
    const int zmapBlocks = 1;

    // firstdatazone = block index of first data zone
    // Layout: block0 (boot) + block1 (superblock) + imapBlocks + zmapBlocks + inodeTableBlocks
    var firstdatazone = 2 + imapBlocks + zmapBlocks + inodeTableBlocks;

    // Zones needed: 1 (root dir) + ceil(size/blocksize) per file (min 1 if data is non-empty)
    var dataZonesNeeded = 1; // root dir
    foreach (var (_, data) in _files)
      dataZonesNeeded += data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;

    var totalZones = firstdatazone + dataZonesNeeded;

    var totalBlocks = totalZones; // zones == blocks for log_zone_size=0
    var diskSize = totalBlocks * BlockSize;
    var disk = new byte[diskSize];

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

    // --- Bitmap offsets ---
    // Inode bitmap: block 2
    var imapOff = 2 * BlockSize;
    // Zone bitmap: block 3
    var zmapOff = 3 * BlockSize;
    // Inode table: block 4
    var inodeTableOff = 4 * BlockSize;

    // Mark inode 1 (root) as used in inode bitmap
    // Minix inodes are 1-based; bit 0 of byte 0 = inode 1
    SetBit(disk, imapOff, 0); // inode 1

    // Mark all metadata zones (0..firstdatazone-1) as used in zone bitmap
    for (var z = 0; z < firstdatazone; z++)
      SetBit(disk, zmapOff, z);

    // Current zone allocator
    var nextZone = firstdatazone;

    // Allocate root directory zone
    var rootDirZone = nextZone++;
    SetBit(disk, zmapOff, rootDirZone);

    // --- Build root directory entries ---
    // V3 dir entry: uint32 inode (4 bytes) + char[60] name (60 bytes) = 64 bytes per entry
    const int DirEntrySize = 64;
    // . and .. plus one entry per file
    var rootDirData = new byte[BlockSize];
    var dirPos = 0;

    // "." entry: inode 1
    WriteDirEntry(rootDirData, dirPos, 1, ".");
    dirPos += DirEntrySize;
    // ".." entry: inode 1 (root's parent = itself)
    WriteDirEntry(rootDirData, dirPos, 1, "..");
    dirPos += DirEntrySize;

    // Allocate inodes and zones for each file
    var fileInode = 2u; // start after root (inode 1)
    var fileAllocations = new List<(uint Inode, uint[] Zones, byte[] Data)>();

    foreach (var (name, data) in _files) {
      SetBit(disk, imapOff, (int)(fileInode - 1));

      // Allocate data zones (max 7 direct for now)
      var zonesNeeded = data.Length == 0 ? 0 : (data.Length + BlockSize - 1) / BlockSize;
      var fileZones = new uint[10]; // 10 zone slots in V3 inode
      for (var z = 0; z < zonesNeeded && z < 7; z++) {
        fileZones[z] = (uint)nextZone;
        SetBit(disk, zmapOff, nextZone);
        nextZone++;
      }

      fileAllocations.Add((fileInode, fileZones, data));

      // Write directory entry for this file (strip any path prefix — store just filename)
      var baseName = Path.GetFileName(name);
      if (baseName.Length > 59) baseName = baseName[..59];
      if (dirPos + DirEntrySize <= rootDirData.Length) {
        WriteDirEntry(rootDirData, dirPos, fileInode, baseName);
        dirPos += DirEntrySize;
      }

      fileInode++;
    }

    // Write root dir data into its zone
    rootDirData.CopyTo(disk, rootDirZone * BlockSize);

    // --- Write root directory inode (inode 1 = index 0 in table) ---
    WriteV3Inode(disk, inodeTableOff, inodeIndex: 0,
      mode: 0x41ED, // directory 0755
      size: (uint)BlockSize,
      zones: [(uint)rootDirZone, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

    // --- Write file inodes and data ---
    foreach (var (ino, zones, data) in fileAllocations) {
      var idx = (int)(ino - 1); // 0-based index in inode table
      WriteV3Inode(disk, inodeTableOff, inodeIndex: idx,
        mode: 0x81A4, // regular file 0644
        size: (uint)data.Length,
        zones: zones);

      // Copy file data into allocated zones
      var written = 0;
      for (var z = 0; z < 7 && written < data.Length; z++) {
        if (zones[z] == 0) break;
        var toWrite = Math.Min(BlockSize, data.Length - written);
        Array.Copy(data, written, disk, (int)zones[z] * BlockSize, toWrite);
        written += toWrite;
      }
    }

    _output.Write(disk);
  }

  private static void WriteDirEntry(byte[] dirData, int offset, uint inode, string name) {
    BinaryPrimitives.WriteUInt32LittleEndian(dirData.AsSpan(offset), inode);
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var copyLen = Math.Min(nameBytes.Length, 59);
    nameBytes.AsSpan(0, copyLen).CopyTo(dirData.AsSpan(offset + 4));
    // null terminator already present (array zero-initialized)
  }

  private static void WriteV3Inode(byte[] disk, int tableOff, int inodeIndex,
      ushort mode, uint size, uint[] zones) {
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
    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2), 1);    // i_nlinks
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

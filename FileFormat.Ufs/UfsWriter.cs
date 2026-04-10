using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ufs;

/// <summary>Writes a minimal UFS1 filesystem image.</summary>
public sealed class UfsWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>Builds the UFS1 image and returns the raw bytes.</summary>
  public byte[] Build() {
    // Minimal UFS1 image layout:
    // Block 0-7: boot area (8192 bytes)
    // Block 8-15: Superblock (8192 bytes at offset 8192)
    // Block 16+: CG0: cg header, inode table, data blocks
    const int fragSize = 1024;
    const int blockSize = 8192;
    const int inodesPerGroup = 64;
    const int inodeSize = 128;

    var iblkno = 4; // inode table starts at fragment 4 within CG
    var dblkno = iblkno + (inodesPerGroup * inodeSize + fragSize - 1) / fragSize;
    var fpg = 1024; // fragments per group

    // Calculate needed data blocks
    var dataFragsNeeded = 0;
    foreach (var (_, data) in _files) {
      var frags = (data.Length + fragSize - 1) / fragSize;
      dataFragsNeeded += Math.Max(frags, 1); // at least 1 for dir entry data
    }
    // Root directory needs at least 1 block
    dataFragsNeeded += blockSize / fragSize;

    var totalFrags = fpg; // one cylinder group
    var imageSize = Math.Max(totalFrags * fragSize, (16 + dblkno + dataFragsNeeded + 8) * fragSize);
    imageSize = Math.Max(imageSize, 128 * 1024); // minimum 128KB
    var disk = new byte[imageSize];

    // Superblock at offset 8192
    var sb = disk.AsSpan(8192);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[84..], (uint)fragSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[88..], (uint)blockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1268..], (uint)inodesPerGroup);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[16..], (uint)iblkno);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[20..], (uint)dblkno);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[172..], 1); // ncg = 1
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1380..], (uint)fpg);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[1372..], 0x00011954); // magic

    // CG0 starts at fragment 0
    // Inode table at fragment iblkno (offset = iblkno * fragSize)
    var inodeTableOff = iblkno * fragSize;

    // Root inode (inode 2) — directory
    var rootInodeOff = inodeTableOff + 2 * inodeSize;
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(rootInodeOff), 0x41ED); // S_IFDIR | 0755
    BinaryPrimitives.WriteInt16LittleEndian(disk.AsSpan(rootInodeOff + 2), 2); // nlink

    // Data blocks start after inode table
    var currentDataFrag = dblkno;

    // Root directory data block
    var rootDirFrag = currentDataFrag;
    currentDataFrag += blockSize / fragSize;

    // Build root directory entries
    using var dirStream = new MemoryStream();
    // "." entry
    WriteDirEntry(dirStream, 2, ".");
    // ".." entry
    WriteDirEntry(dirStream, 2, "..");

    var nextInode = 3;
    var fileInodes = new List<int>();

    foreach (var (name, data) in _files) {
      var ino = nextInode++;
      fileInodes.Add(ino);
      WriteDirEntry(dirStream, ino, name);

      // Write file inode
      var fileInodeOff = inodeTableOff + ino * inodeSize;
      if (fileInodeOff + inodeSize <= disk.Length) {
        BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(fileInodeOff), 0x81A4); // S_IFREG | 0644
        BinaryPrimitives.WriteInt16LittleEndian(disk.AsSpan(fileInodeOff + 2), 1); // nlink
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(fileInodeOff + 8), (ulong)data.Length);

        // Direct block pointers
        var dataFrag = currentDataFrag;
        var frags = (data.Length + fragSize - 1) / fragSize;
        if (frags == 0) frags = 0;

        for (int i = 0; i < frags && i < 12; i++) {
          BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(fileInodeOff + 40 + i * 4), (uint)(dataFrag + i));
        }

        // Write file data
        var dataOff = dataFrag * fragSize;
        if (data.Length > 0 && dataOff + data.Length <= disk.Length)
          data.CopyTo(disk, dataOff);

        currentDataFrag += Math.Max(frags, 0);
      }
    }

    // Write root directory data
    var dirData = dirStream.ToArray();
    var rootDirOff = rootDirFrag * fragSize;
    if (dirData.Length > 0 && rootDirOff + dirData.Length <= disk.Length)
      dirData.CopyTo(disk, rootDirOff);

    // Root inode: set size and direct block
    BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(rootInodeOff + 8), (ulong)dirData.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(rootInodeOff + 40), (uint)rootDirFrag);

    return disk;
  }

  private static void WriteDirEntry(MemoryStream ms, int ino, string name) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var reclen = 8 + nameBytes.Length;
    reclen = (reclen + 3) & ~3; // align to 4

    var entry = new byte[reclen];
    BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)ino);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), (ushort)reclen);
    entry[6] = 0; // type (0 = unknown, works for UFS1)
    entry[7] = (byte)nameBytes.Length;
    nameBytes.CopyTo(entry, 8);
    ms.Write(entry);
  }
}

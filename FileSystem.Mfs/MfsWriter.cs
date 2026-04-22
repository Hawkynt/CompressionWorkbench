using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Mfs;

/// <summary>Builds a minimal MFS disk image.</summary>
public sealed class MfsWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>Builds the MFS disk image.</summary>
  public byte[] Build() {
    const int sectorSize = 512;
    const uint blockSize = 1024;
    const int totalSectors = 800; // 400KB floppy
    var imageSize = totalSectors * sectorSize;
    var disk = new byte[imageSize];

    // MDB at offset 1024 (sector 2)
    var mdb = disk.AsSpan(1024);
    BinaryPrimitives.WriteUInt16BigEndian(mdb, 0xD2D7); // magic

    var numAllocBlocks = (ushort)((totalSectors - 12) * sectorSize / blockSize);
    BinaryPrimitives.WriteUInt16BigEndian(mdb[18..], numAllocBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(mdb[20..], blockSize);

    // File directory at sectors 3-5 (offset 1536 to 3072)
    // First allocation block at sector 12
    var firstAllocBlockSector = 12;
    BinaryPrimitives.WriteUInt16BigEndian(mdb[28..], (ushort)firstAllocBlockSector);

    // Volume name
    mdb[36] = 8;
    "Untitled"u8.CopyTo(mdb[37..]);

    // Write file directory entries starting at offset 1024+128
    var dirPos = 1024 + 128;
    var currentBlock = 0;

    for (int i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      var nameBytes = Encoding.ASCII.GetBytes(name);
      var blocks = (int)((data.Length + blockSize - 1) / blockSize);
      if (blocks == 0 && data.Length == 0) blocks = 0;

      // Write file data
      var dataOffset = firstAllocBlockSector * sectorSize + (int)(currentBlock * blockSize);
      if (data.Length > 0 && dataOffset + data.Length <= imageSize)
        data.CopyTo(disk, dataOffset);

      // Write directory entry
      disk[dirPos] = 0x80; // flags: in use
      disk[dirPos + 1] = 0; // version
      // fileType (4 bytes) at +2, fileCreator (4 bytes) at +6 — leave zeros
      BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(dirPos + 26), (ushort)currentBlock);
      BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(dirPos + 28), (uint)data.Length);
      // resource fork = 0
      disk[dirPos + 38] = (byte)nameBytes.Length;
      nameBytes.CopyTo(disk, dirPos + 39);

      var entryLen = 39 + nameBytes.Length;
      if ((entryLen & 1) != 0) entryLen++;
      dirPos += entryLen;

      currentBlock += blocks;
    }

    return disk;
  }
}

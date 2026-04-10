using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Hfs;

/// <summary>Builds a minimal Classic HFS disk image.</summary>
public sealed class HfsWriter {
  private const int MdbOffset = 1024;
  private const int NodeSize = 512;
  private const uint BlockSize = 512;
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>Builds the HFS disk image.</summary>
  public byte[] Build() {
    // Layout: 800KB floppy image = 1600 sectors = 819200 bytes
    // Sector 0-1: boot blocks
    // Sector 2: MDB
    // Sectors 3-4: allocation bitmap (enough for 800KB)
    // Sector 5-6: catalog B-tree (header node + leaf node)
    // Sector 7+: file data
    // Last 2 sectors: alternate MDB + reserved

    const int totalSectors = 1600;
    const int imageSize = totalSectors * 512;
    var disk = new byte[imageSize];

    var firstAllocBlock = 6; // in 512-byte sectors (after MDB + bitmap)
    var numAllocBlocks = (ushort)((totalSectors - firstAllocBlock - 2) * 512 / BlockSize);

    // Catalog at allocation block 0-1
    var catalogStartBlock = 0;
    var catalogBlockCount = 2;
    var dataStartBlock = catalogBlockCount;

    // Place files
    var currentBlock = dataStartBlock;
    var fileBlocks = new List<(int startBlock, int blockCount)>();
    foreach (var (_, data) in _files) {
      var blocks = (int)((data.Length + BlockSize - 1) / BlockSize);
      if (blocks == 0) blocks = 0;
      fileBlocks.Add((currentBlock, blocks));
      // Write data
      var offset = firstAllocBlock * 512 + (int)(currentBlock * BlockSize);
      if (data.Length > 0 && offset + data.Length <= imageSize)
        data.CopyTo(disk, offset);
      currentBlock += blocks;
    }

    // Write MDB
    var mdb = disk.AsSpan(MdbOffset);
    BinaryPrimitives.WriteUInt16BigEndian(mdb, 0x4244); // signature
    BinaryPrimitives.WriteUInt16BigEndian(mdb[18..], numAllocBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(mdb[20..], BlockSize);
    BinaryPrimitives.WriteUInt16BigEndian(mdb[28..], (ushort)firstAllocBlock);
    BinaryPrimitives.WriteUInt32BigEndian(mdb[30..], 100); // next CNID

    // Volume name at offset 36 (Pascal string: length byte + name)
    var volName = "Untitled"u8;
    mdb[36] = (byte)volName.Length;
    volName.CopyTo(mdb[37..]);

    // Catalog extents at MDB+78: startBlock(2) + numBlocks(2) x 3
    BinaryPrimitives.WriteUInt16BigEndian(mdb[78..], (ushort)catalogStartBlock);
    BinaryPrimitives.WriteUInt16BigEndian(mdb[80..], (ushort)catalogBlockCount);

    // Write catalog B-tree
    var catBase = firstAllocBlock * 512 + (int)(catalogStartBlock * BlockSize);

    // Header node (node 0)
    disk[catBase + 8] = 2; // kind = header
    BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(catBase + 10), 3); // numRecords

    // Header record at offset 14:
    var hdr = disk.AsSpan(catBase + 14);
    BinaryPrimitives.WriteUInt16BigEndian(hdr, 1); // treeDepth
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], 1); // rootNode = 1
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], 0); // leafRecords
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], 0); // firstLeaf placeholder
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], 1); // firstLeaf = 1
    BinaryPrimitives.WriteUInt32BigEndian(hdr[18..], 1); // lastLeaf = 1
    BinaryPrimitives.WriteUInt16BigEndian(hdr[22..], NodeSize); // nodeSize
    BinaryPrimitives.WriteUInt32BigEndian(hdr[26..], 2); // totalNodes
    BinaryPrimitives.WriteUInt32BigEndian(hdr[30..], 0); // freeNodes

    // Record offsets for header node
    BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(catBase + NodeSize - 2), 14);
    BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(catBase + NodeSize - 4), 120);
    BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(catBase + NodeSize - 6), 248);
    BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(catBase + NodeSize - 8), 400);

    // Leaf node (node 1)
    var leafBase = catBase + NodeSize;
    disk[leafBase + 8] = 0xFF; // kind = leaf (-1 as signed byte)
    disk[leafBase + 9] = 1;    // height = 1

    // Write file records
    var records = new List<byte[]>();
    for (int i = 0; i < _files.Count; i++) {
      var (name, data) = _files[i];
      var (startBlk, blkCount) = fileBlocks[i];
      records.Add(BuildFileRecord(2, name, startBlk, blkCount, data.Length));
    }

    BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(leafBase + 10), (ushort)records.Count);

    var writePos = 14;
    for (int i = 0; i < records.Count; i++) {
      var rec = records[i];
      rec.CopyTo(disk, leafBase + writePos);
      BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(leafBase + NodeSize - 2 * (i + 1)), (ushort)writePos);
      writePos += rec.Length;
      if ((writePos & 1) != 0) writePos++;
    }
    var freeSlot = leafBase + NodeSize - 2 * (records.Count + 1);
    if (freeSlot > leafBase)
      BinaryPrimitives.WriteUInt16BigEndian(disk.AsSpan(freeSlot), (ushort)writePos);

    // Update header leaf records count
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], (uint)records.Count);

    return disk;
  }

  private static byte[] BuildFileRecord(uint parentDirId, string name, int startBlock, int blockCount, int fileSize) {
    // Key: keyLen(1) + reserved(1) + parentDirId(4) + nameLen(1) + name
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var keyLen = (byte)(1 + 4 + 1 + nameBytes.Length); // reserved + parentDirId + nameLen + name
    var keyTotal = 1 + keyLen;
    if ((keyTotal & 1) != 0) keyTotal++; // pad to even

    // Record data: type(1) + reserved(1) + ... + first extent at +16
    var dataSize = 26;
    var rec = new byte[keyTotal + dataSize];

    rec[0] = keyLen;
    rec[1] = 0; // reserved
    BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(2), parentDirId);
    rec[6] = (byte)nameBytes.Length;
    nameBytes.CopyTo(rec, 7);

    var dataPos = keyTotal;
    rec[dataPos] = 2; // type = file
    rec[dataPos + 1] = 0; // reserved

    // Data fork first extent at +16
    BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(dataPos + 16), (ushort)startBlock);
    BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(dataPos + 18), (ushort)blockCount);
    // Logical EOF at +22
    BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(dataPos + 22), (uint)fileSize);

    return rec;
  }
}

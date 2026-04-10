using System.Buffers.Binary;
using System.Text;

namespace FileFormat.HfsPlus;

/// <summary>
/// Creates minimal HFS+ filesystem images.
/// Produces a 4 MB image with 4 KB block size by default.
/// Files are stored uncompressed in the data fork using single-extent allocation.
/// </summary>
public sealed class HfsPlusWriter {
  private const uint DefaultBlockSize = 4096;
  private const int DefaultImageBlocks = 1024; // 4 MB = 1024 * 4096
  private const int VolumeHeaderOffset = 1024;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsPlusVersion = 4;
  private const uint RootFolderCnid = 2;
  private const uint FirstUserCnid = 16;

  // HFS+ epoch: 1904-01-01T00:00:00Z.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Adds a file to be included in the volume image.
  /// </summary>
  /// <param name="name">The filename (stored in the root directory).</param>
  /// <param name="data">The file content.</param>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds and returns the complete HFS+ volume image.
  /// </summary>
  /// <returns>A byte array containing the HFS+ filesystem image.</returns>
  public byte[] Build() {
    var blockSize = DefaultBlockSize;

    // Calculate required size: we need blocks for
    // - Block 0: allocation bitmap
    // - Block 1: catalog B-tree header node
    // - Block 2: catalog B-tree leaf node
    // - Blocks 3+: file data
    var dataBlocksNeeded = 0;
    foreach (var (_, data) in _files)
      dataBlocksNeeded += (int)((data.Length + blockSize - 1) / blockSize);

    var totalBlocks = Math.Max(DefaultImageBlocks, (uint)(3 + dataBlocksNeeded + 1));
    var imageSize = (int)(totalBlocks * blockSize);

    var disk = new byte[imageSize];

    // ── Volume Header at offset 1024 ──────────────────────────────────────
    var vh = disk.AsSpan(VolumeHeaderOffset);
    BinaryPrimitives.WriteUInt16BigEndian(vh, HfsPlusSignature);        // offset 0: signature
    BinaryPrimitives.WriteUInt16BigEndian(vh[2..], HfsPlusVersion);     // offset 2: version
    // offset 4: attributes (uint32 BE) — 0 for simplicity
    // offset 40: blockSize
    BinaryPrimitives.WriteUInt32BigEndian(vh[40..], blockSize);
    // offset 44: totalBlocks
    BinaryPrimitives.WriteUInt32BigEndian(vh[44..], totalBlocks);

    // Catalog file: use blocks 1 and 2 (header node + leaf node).
    var catalogStartBlock = 1u;
    var catalogBlockCount = 2u;

    // offset 260: catalog file total blocks
    BinaryPrimitives.WriteUInt32BigEndian(vh[260..], catalogBlockCount);
    // offset 268: catalog clump size
    BinaryPrimitives.WriteUInt32BigEndian(vh[268..], catalogBlockCount * blockSize);
    // offset 272: catalog extent[0].startBlock
    BinaryPrimitives.WriteUInt32BigEndian(vh[272..], catalogStartBlock);
    // offset 276: catalog extent[0].blockCount
    BinaryPrimitives.WriteUInt32BigEndian(vh[276..], catalogBlockCount);

    // Allocation file at block 0.
    // offset 196: allocation file total blocks = 1
    BinaryPrimitives.WriteUInt32BigEndian(vh[196..], 1);
    // offset 204: alloc clump size
    BinaryPrimitives.WriteUInt32BigEndian(vh[204..], blockSize);
    // offset 208: alloc extent[0].startBlock = 0
    BinaryPrimitives.WriteUInt32BigEndian(vh[208..], 0);
    // offset 212: alloc extent[0].blockCount = 1
    BinaryPrimitives.WriteUInt32BigEndian(vh[212..], 1);

    // ── Build catalog B-tree ──────────────────────────────────────────────
    var nodeSize = (ushort)4096;
    var catalogBase = (int)(catalogStartBlock * blockSize);

    // -- Node 0: Header node --
    var headerNode = disk.AsSpan(catalogBase, nodeSize);
    // Node descriptor: fLink=0, bLink=0, kind=1 (header), height=0, numRecords=3
    headerNode[8] = 1; // kind = header node
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[10..], 3); // numRecords

    // Header record at offset 14.
    var hdr = headerNode[14..];
    BinaryPrimitives.WriteUInt16BigEndian(hdr, 1);              // treeDepth = 1
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], 1);         // rootNode = 1 (node index 1)
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], 0);         // leafRecords (will update)
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], 0);        // firstLeafNode (placeholder)
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], 0);        // lastLeafNode (placeholder)
    BinaryPrimitives.WriteUInt16BigEndian(hdr[26..], nodeSize); // nodeSize
    BinaryPrimitives.WriteUInt16BigEndian(hdr[28..], 520);      // maxKeyLength
    BinaryPrimitives.WriteUInt32BigEndian(hdr[30..], 2);        // totalNodes = 2
    BinaryPrimitives.WriteUInt32BigEndian(hdr[34..], 0);        // freeNodes = 0

    // Header node record offsets (stored at end of node, uint16 BE each, reverse order).
    // Record 0 offset at nodeSize-2, Record 1 at nodeSize-4, Record 2 at nodeSize-6.
    // Free space offset at nodeSize-8.
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 2)..], 14);    // record 0 = header record
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 4)..], 142);   // record 1 = user data record
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 6)..], 270);   // record 2 = map record
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 8)..], 398);   // free space

    // -- Node 1: Leaf node --
    var leafBase = catalogBase + nodeSize;
    var leafNode = disk.AsSpan(leafBase, nodeSize);
    // Node descriptor: fLink=0, bLink=0, kind=-1 (leaf), height=1
    leafNode[8] = 0xFF; // kind = -1 (leaf), signed byte
    leafNode[9] = 1;    // height = 1

    // Build catalog records.
    var records = new List<byte[]>();

    // Root folder thread record (type 3): maps CNID 2 → parent CNID 1, name "root".
    records.Add(BuildFolderThreadRecord(1, "root"));

    // Root folder record (type 1).
    records.Add(BuildFolderRecord(RootFolderCnid, (uint)_files.Count));

    // File records.
    var nextBlock = 3u; // Blocks 0=alloc, 1=catalog hdr, 2=catalog leaf, 3+=file data.
    var nextCnid = FirstUserCnid;

    foreach (var (name, data) in _files) {
      var dataBlockCount2 = (uint)((data.Length + blockSize - 1) / blockSize);
      if (dataBlockCount2 == 0 && data.Length == 0) dataBlockCount2 = 0;

      var startBlock = nextBlock;
      var fileCnid = nextCnid++;

      // Write file data to blocks.
      if (data.Length > 0) {
        var destOffset = (int)(startBlock * blockSize);
        if (destOffset + data.Length <= disk.Length)
          data.CopyTo(disk, destOffset);
      }

      nextBlock += dataBlockCount2;

      // File thread record (type 4).
      records.Add(BuildFileThreadRecord(fileCnid, RootFolderCnid, name));

      // File record (type 2).
      records.Add(BuildFileRecord(fileCnid, RootFolderCnid, name, (long)data.Length, startBlock, dataBlockCount2));
    }

    // Write records into the leaf node.
    var recCount = (ushort)records.Count;
    BinaryPrimitives.WriteUInt16BigEndian(leafNode[10..], recCount);

    var writePos = 14; // After node descriptor.
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      rec.CopyTo(disk, leafBase + writePos);
      // Write record offset at end of node.
      var offsetSlot = nodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leafNode[offsetSlot..], (ushort)writePos);
      writePos += rec.Length;
      // Align to 2-byte boundary.
      if ((writePos & 1) != 0) writePos++;
    }
    // Free space offset.
    var freeSlot = nodeSize - 2 * (records.Count + 1);
    if (freeSlot >= 0)
      BinaryPrimitives.WriteUInt16BigEndian(leafNode[freeSlot..], (ushort)writePos);

    // Update header record counts.
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], (uint)records.Count);  // leafRecords
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], 0);                    // firstLeafNode
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], 0);                    // lastLeafNode
    // rootNode and firstLeaf/lastLeaf = node 1.
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], 1);   // rootNode = 1
    BinaryPrimitives.WriteUInt32BigEndian(hdr[18..], 1);  // firstLeafNode = 1
    BinaryPrimitives.WriteUInt32BigEndian(hdr[22..], 1);  // lastLeafNode = 1

    // ── Allocation bitmap at block 0 ─────────────────────────────────────
    // Mark used blocks: 0 (alloc), 1-2 (catalog), 3..nextBlock-1 (file data).
    var bitmapBase = 0; // Block 0 starts at byte 0.
    for (var b = 0u; b < nextBlock && b < totalBlocks; b++) {
      var byteIndex = (int)(b / 8);
      var bitIndex = (int)(7 - (b % 8)); // MSB-first within each byte.
      if (bitmapBase + byteIndex < disk.Length)
        disk[bitmapBase + byteIndex] |= (byte)(1 << bitIndex);
    }

    return disk;
  }

  // ── Record builders ─────────────────────────────────────────────────────

  private static byte[] BuildCatalogKey(uint parentCnid, string name) {
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    // Key: keyLength (uint16 BE) + parentCNID (uint32 BE) + nameLength (uint16 BE) + name (UTF-16BE).
    var keyLen = (ushort)(4 + 2 + nameBytes.Length); // parentCNID + nameLength + name
    var key = new byte[2 + keyLen];
    BinaryPrimitives.WriteUInt16BigEndian(key, keyLen);
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(6), nameLen);
    nameBytes.CopyTo(key, 8);
    return key;
  }

  private static byte[] BuildFolderRecord(uint cnid, uint valence) {
    // Key: parent CNID = 1 (root parent), name = "" (folder's own thread-like entry
    // pointed to via parent=1 to signal root directory in standard HFS+ layout).
    var key = BuildCatalogKey(1, "");
    // Folder record: type(2) + flags(2) + valence(4) + cnid(4) + createDate(4) + modDate(4) + ... = ~88 bytes min
    var recData = new byte[88];
    BinaryPrimitives.WriteInt16BigEndian(recData, 1); // recordType = folder
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), valence);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(8), cnid);
    var now = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(12), now);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(16), now);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  private static byte[] BuildFolderThreadRecord(uint parentCnid, string name) {
    var key = BuildCatalogKey(RootFolderCnid, "");
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    // Thread record: type(2) + reserved(2) + parentCNID(4) + nameLength(2) + name.
    var recData = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt16BigEndian(recData, 3); // recordType = folder thread
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(8), nameLen);
    nameBytes.CopyTo(recData, 10);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  private static byte[] BuildFileThreadRecord(uint fileCnid, uint parentCnid, string name) {
    var key = BuildCatalogKey(fileCnid, "");
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    // Thread record: type(2) + reserved(2) + parentCNID(4) + nameLength(2) + name.
    var recData = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt16BigEndian(recData, 4); // recordType = file thread
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(8), nameLen);
    nameBytes.CopyTo(recData, 10);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  private static byte[] BuildFileRecord(uint fileCnid, uint parentCnid, string name,
      long logicalSize, uint startBlock, uint blockCount) {
    var key = BuildCatalogKey(parentCnid, name);
    // File record: must be at least 86 bytes for the reader to parse:
    // type(2) + flags(2) + reserved(4) + cnid(4) + createDate(4) + modDate(4) +
    // ... padding to offset 70 for data fork logical size (8) + startBlock(4) + blockCount(4)
    var recData = new byte[86];
    BinaryPrimitives.WriteInt16BigEndian(recData, 2); // recordType = file
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(8), fileCnid);
    var now = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(12), now);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(16), now);
    BinaryPrimitives.WriteUInt64BigEndian(recData.AsSpan(70), (ulong)logicalSize);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(78), startBlock);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(82), blockCount);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  private static uint HfsTimestamp(DateTime dt) {
    if (dt < HfsEpoch) return 0;
    var seconds = (dt - HfsEpoch).TotalSeconds;
    return seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }
}

using System.Buffers.Binary;
using System.Text;

namespace FileSystem.HfsPlus;

/// <summary>
/// Creates minimal HFS+ filesystem images per Apple TN1150 ("HFS Plus Volume Format").
/// <para>
/// Produces a 4&#160;MB image with 4&#160;KB block size by default. Files are stored
/// uncompressed in the data fork using single-extent allocation. The catalog file
/// record is the full 248-byte <c>HFSPlusCatalogFile</c> layout with the data fork
/// <c>HFSPlusForkData</c> struct at offset 88 and the resource fork <c>HFSPlusForkData</c>
/// at offset 168, matching TN1150.
/// </para>
/// </summary>
public sealed class HfsPlusWriter {
  private const uint DefaultBlockSize = 4096;
  private const int DefaultImageBlocks = 1024; // 4 MB = 1024 * 4096
  private const int VolumeHeaderOffset = 1024;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsPlusVersion = 4;
  private const uint RootFolderCnid = 2;
  private const uint FirstUserCnid = 16;

  // TN1150 HFSPlusCatalogFile layout.
  internal const int CatalogFileRecordSize = 248;
  internal const int CatalogForkDataSize = 80;
  internal const int DataForkOffset = 88;
  internal const int ResourceForkOffset = 168;

  // HFS+ epoch: 1904-01-01T00:00:00Z.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Adds a file to be included in the volume image.
  /// </summary>
  /// <param name="name">The filename (stored in the root directory).</param>
  /// <param name="data">The file content.</param>
  public void AddFile(string name, byte[] data) => this._files.Add((name, data));

  /// <summary>
  /// Builds and returns the complete HFS+ volume image.
  /// </summary>
  /// <returns>A byte array containing the HFS+ filesystem image.</returns>
  public byte[] Build() {
    var blockSize = DefaultBlockSize;

    var dataBlocksNeeded = 0;
    foreach (var (_, data) in this._files)
      dataBlocksNeeded += (int)((data.Length + blockSize - 1) / blockSize);

    var totalBlocks = Math.Max(DefaultImageBlocks, (uint)(3 + dataBlocksNeeded + 1));
    var imageSize = (int)(totalBlocks * blockSize);

    var disk = new byte[imageSize];

    // ── Volume Header at offset 1024 ──────────────────────────────────────
    var vh = disk.AsSpan(VolumeHeaderOffset);
    BinaryPrimitives.WriteUInt16BigEndian(vh, HfsPlusSignature);
    BinaryPrimitives.WriteUInt16BigEndian(vh[2..], HfsPlusVersion);
    BinaryPrimitives.WriteUInt32BigEndian(vh[4..], 0x00000100u); // kHFSVolumeUnmountedBit
    var nowTs = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(vh[16..], nowTs);      // createDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[20..], nowTs);      // modifyDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[28..], nowTs);      // checkedDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[32..], (uint)this._files.Count); // fileCount
    BinaryPrimitives.WriteUInt32BigEndian(vh[36..], 0);          // folderCount
    BinaryPrimitives.WriteUInt32BigEndian(vh[40..], blockSize);
    BinaryPrimitives.WriteUInt32BigEndian(vh[44..], totalBlocks);

    var catalogStartBlock = 1u;
    var catalogBlockCount = 2u;

    // offset 260: catalog file total blocks
    BinaryPrimitives.WriteUInt32BigEndian(vh[260..], catalogBlockCount);
    BinaryPrimitives.WriteUInt32BigEndian(vh[268..], catalogBlockCount * blockSize);
    BinaryPrimitives.WriteUInt32BigEndian(vh[272..], catalogStartBlock);
    BinaryPrimitives.WriteUInt32BigEndian(vh[276..], catalogBlockCount);

    // Allocation file at block 0.
    BinaryPrimitives.WriteUInt32BigEndian(vh[196..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(vh[204..], blockSize);
    BinaryPrimitives.WriteUInt32BigEndian(vh[208..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(vh[212..], 1);

    // ── Build catalog B-tree ──────────────────────────────────────────────
    const ushort nodeSize = 4096;
    var catalogBase = (int)(catalogStartBlock * blockSize);

    // -- Node 0: Header node --
    var headerNode = disk.AsSpan(catalogBase, nodeSize);
    headerNode[8] = 1; // kind = header node
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[10..], 3); // numRecords

    var hdr = headerNode[14..];
    BinaryPrimitives.WriteUInt16BigEndian(hdr, 1);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], 0);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[26..], nodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(hdr[28..], 520);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[30..], 2);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[34..], 0);

    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 2)..], 14);
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 4)..], 142);
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 6)..], 270);
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 8)..], 398);

    // -- Node 1: Leaf node --
    var leafBase = catalogBase + nodeSize;
    var leafNode = disk.AsSpan(leafBase, nodeSize);
    leafNode[8] = 0xFF; // kind = -1 (leaf)
    leafNode[9] = 1;    // height = 1

    // Build catalog records.
    var records = new List<byte[]>();

    records.Add(BuildFolderThreadRecord(1, "root"));
    records.Add(BuildFolderRecord(RootFolderCnid, (uint)this._files.Count));

    var nextBlock = 3u;
    var nextCnid = FirstUserCnid;

    foreach (var (name, data) in this._files) {
      var dataBlockCount2 = (uint)((data.Length + blockSize - 1) / blockSize);

      var startBlock = nextBlock;
      var fileCnid = nextCnid++;

      if (data.Length > 0) {
        var destOffset = (int)(startBlock * blockSize);
        if (destOffset + data.Length <= disk.Length)
          data.CopyTo(disk, destOffset);
      }

      nextBlock += dataBlockCount2;

      records.Add(BuildFileThreadRecord(fileCnid, RootFolderCnid, name));
      records.Add(BuildFileRecord(fileCnid, RootFolderCnid, name, (long)data.Length, startBlock, dataBlockCount2));
    }

    // Write records into the leaf node.
    var recCount = (ushort)records.Count;
    BinaryPrimitives.WriteUInt16BigEndian(leafNode[10..], recCount);

    var writePos = 14;
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      rec.CopyTo(disk, leafBase + writePos);
      var offsetSlot = nodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leafNode[offsetSlot..], (ushort)writePos);
      writePos += rec.Length;
      if ((writePos & 1) != 0) writePos++;
    }
    var freeSlot = nodeSize - 2 * (records.Count + 1);
    if (freeSlot >= 0)
      BinaryPrimitives.WriteUInt16BigEndian(leafNode[freeSlot..], (ushort)writePos);

    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], (uint)records.Count);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[18..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[22..], 1);

    // ── Allocation bitmap at block 0 ─────────────────────────────────────
    for (var b = 0u; b < nextBlock && b < totalBlocks; b++) {
      var byteIndex = (int)(b / 8);
      var bitIndex = (int)(7 - (b % 8));
      if (byteIndex < disk.Length)
        disk[byteIndex] |= (byte)(1 << bitIndex);
    }

    BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(VolumeHeaderOffset + 48), totalBlocks - nextBlock);
    BinaryPrimitives.WriteUInt32BigEndian(disk.AsSpan(VolumeHeaderOffset + 64), nextCnid);

    return disk;
  }

  // ── Record builders ─────────────────────────────────────────────────────

  private static byte[] BuildCatalogKey(uint parentCnid, string name) {
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    var keyLen = (ushort)(4 + 2 + nameBytes.Length);
    var key = new byte[2 + keyLen];
    BinaryPrimitives.WriteUInt16BigEndian(key, keyLen);
    BinaryPrimitives.WriteUInt32BigEndian(key.AsSpan(2), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(6), nameLen);
    nameBytes.CopyTo(key, 8);
    return key;
  }

  private static byte[] BuildFolderRecord(uint cnid, uint valence) {
    var key = BuildCatalogKey(1, "");
    // TN1150 HFSPlusCatalogFolder = 88 bytes min (recordType + flags + valence +
    // folderID + dates + perms + userInfo + finderInfo + textEncoding + reserved).
    var recData = new byte[88];
    BinaryPrimitives.WriteInt16BigEndian(recData, 1);                 // recordType = kHFSPlusFolderRecord
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), valence);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(8), cnid);   // folderID
    var now = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(12), now);   // createDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(16), now);   // contentModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(20), now);   // attributeModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(24), now);   // accessDate

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  private static byte[] BuildFolderThreadRecord(uint parentCnid, string name) {
    var key = BuildCatalogKey(RootFolderCnid, "");
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);
    var nameLen = (ushort)(nameBytes.Length / 2);
    var recData = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt16BigEndian(recData, 3); // kHFSPlusFolderThreadRecord
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
    var recData = new byte[10 + nameBytes.Length];
    BinaryPrimitives.WriteInt16BigEndian(recData, 4); // kHFSPlusFileThreadRecord
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), parentCnid);
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(8), nameLen);
    nameBytes.CopyTo(recData, 10);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  /// <summary>
  /// Emits a full 248-byte TN1150 <c>HFSPlusCatalogFile</c> record with the data
  /// fork <c>HFSPlusForkData</c> at offset 88 (relative to the record body, i.e.
  /// after the catalog key) and the resource fork at offset 168.
  /// </summary>
  private static byte[] BuildFileRecord(uint fileCnid, uint parentCnid, string name,
      long logicalSize, uint startBlock, uint blockCount) {
    var key = BuildCatalogKey(parentCnid, name);
    var recData = new byte[CatalogFileRecordSize];

    // Header fields.
    BinaryPrimitives.WriteInt16BigEndian(recData, 2);                  // recordType = kHFSPlusFileRecord
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(2), 0);       // flags
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(4), 0);       // reserved1
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(8), fileCnid);// fileID
    var now = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(12), now);    // createDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(16), now);    // contentModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(20), now);    // attributeModDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(24), now);    // accessDate
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(28), 0);      // backupDate
    // permissions[16] at offset 32 — zeros (owner=0, group=0, mode=0 → unspecified).
    // userInfo[16] at offset 48 (FileInfo) — zeros.
    // finderInfo[16] at offset 64 (ExtendedFileInfo) — zeros.
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(80), 0);      // textEncoding
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(84), 0);      // reserved2

    // HFSPlusForkData dataFork at offset 88 (80 bytes).
    WriteForkData(recData.AsSpan(DataForkOffset, CatalogForkDataSize), logicalSize, DefaultBlockSize, startBlock, blockCount);

    // HFSPlusForkData resourceFork at offset 168 (80 bytes). Empty for our writer.
    WriteForkData(recData.AsSpan(ResourceForkOffset, CatalogForkDataSize), 0, DefaultBlockSize, 0, 0);

    var result = new byte[key.Length + recData.Length];
    key.CopyTo(result, 0);
    recData.CopyTo(result, key.Length);
    return result;
  }

  /// <summary>
  /// Writes an <c>HFSPlusForkData</c> struct (80 bytes):
  /// logicalSize (u64) + clumpSize (u32) + totalBlocks (u32) + 8 extents (u32 startBlock + u32 blockCount).
  /// </summary>
  private static void WriteForkData(Span<byte> dst, long logicalSize, uint clumpSize, uint startBlock, uint blockCount) {
    BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)logicalSize); // offset 0
    BinaryPrimitives.WriteUInt32BigEndian(dst[8..], clumpSize);     // offset 8
    BinaryPrimitives.WriteUInt32BigEndian(dst[12..], blockCount);   // offset 12 — totalBlocks
    // extents[0] at offset 16.
    BinaryPrimitives.WriteUInt32BigEndian(dst[16..], startBlock);
    BinaryPrimitives.WriteUInt32BigEndian(dst[20..], blockCount);
    // Remaining 7 extent descriptors are zero (no fragmentation in our writer).
  }

  private static uint HfsTimestamp(DateTime dt) {
    if (dt < HfsEpoch) return 0;
    var seconds = (dt - HfsEpoch).TotalSeconds;
    return seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }
}

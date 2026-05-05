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

    // Layout per TN1150:
    //   block 0:        boot blocks (sectors 0,1) + primary VHB (sector 2)
    //   block 1:        allocation bitmap (1 block fits up to 32768 alloc blocks)
    //   block 2:        extents-overflow B-tree (1 block, header only — empty)
    //   blocks 3..4:    catalog B-tree (header + leaf node)
    //   blocks 5..N:    user file data
    //   block totalBlocks-1: alternate VHB at sector (totalSectors-2)
    const uint AllocBlock = 1;
    const uint ExtentsBlock = 2;
    const uint CatalogStartBlock = 3;
    const uint CatalogBlockCount = 2;

    var minBlocks = 5u + (uint)dataBlocksNeeded + 1u; // +1 for last (alt VHB)
    var totalBlocks = Math.Max(DefaultImageBlocks, minBlocks);
    var imageSize = (int)(totalBlocks * blockSize);

    var disk = new byte[imageSize];

    // HFS+ requires the volume header at sector 2 (offset 1024) AND a byte-
    // identical alternate volume header at sector (totalSectors-2). For an
    // image with 512-byte sectors that's at byte offset (imageSize - 1024).
    // Both copies must carry the H+/HX signature and matching contents — fsck
    // refuses the volume otherwise.
    var alternateVhOffset = imageSize - 1024;

    // ── Volume Header at offset 1024 ──────────────────────────────────────
    var vh = disk.AsSpan(VolumeHeaderOffset);
    BinaryPrimitives.WriteUInt16BigEndian(vh, HfsPlusSignature);
    BinaryPrimitives.WriteUInt16BigEndian(vh[2..], HfsPlusVersion);
    // attributes: kHFSVolumeUnmountedBit (0x100) | kHFSVolumeUsedBit? (no, just unmount)
    BinaryPrimitives.WriteUInt32BigEndian(vh[4..], 0x00000100u);
    // lastMountedVersion: ASCII "10.0" (the value mkfs.hfsplus writes).
    "10.0"u8.ToArray().CopyTo(vh[8..12]);
    var nowTs = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(vh[16..], nowTs);      // createDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[20..], nowTs);      // modifyDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[28..], nowTs);      // checkedDate
    BinaryPrimitives.WriteUInt32BigEndian(vh[32..], (uint)this._files.Count); // fileCount (root excluded)
    BinaryPrimitives.WriteUInt32BigEndian(vh[36..], 0);          // folderCount (root excluded per TN1150)
    BinaryPrimitives.WriteUInt32BigEndian(vh[40..], blockSize);
    BinaryPrimitives.WriteUInt32BigEndian(vh[44..], totalBlocks);
    // rsrcClumpSize, dataClumpSize @ 56, 60: TN1150 recommends 64 KB.
    BinaryPrimitives.WriteUInt32BigEndian(vh[56..], 0x10000);    // rsrcClumpSize = 64K
    BinaryPrimitives.WriteUInt32BigEndian(vh[60..], 0x10000);    // dataClumpSize = 64K
    // encodingsBitmap @ 72: bit 0 = MacRoman (the mandatory legacy encoding).
    BinaryPrimitives.WriteUInt64BigEndian(vh[72..], 1UL);

    var catalogStartBlock = CatalogStartBlock;
    var catalogBlockCount = CatalogBlockCount;

    // ── Special-file ForkData descriptors per TN1150 §3.2 ─────────────────
    // VolumeHeader special-file ForkData offsets:
    //   allocationFile @ 112, extentsFile @ 192, catalogFile @ 272,
    //   attributesFile @ 352, startupFile @ 432.
    // Each is a 80-byte HFSPlusForkData:
    //   +0  logicalSize (u64 BE)
    //   +8  clumpSize   (u32 BE)
    //   +12 totalBlocks (u32 BE)
    //   +16 extents[8] (u32 startBlock + u32 blockCount each).
    //
    // Allocation bitmap: 1 block at AllocBlock covers the volume.
    WriteForkData(vh.Slice(112, CatalogForkDataSize),
        (long)blockSize, blockSize, AllocBlock, 1u);

    // Extents-overflow B-tree: 1 block at ExtentsBlock. We don't have any
    // overflow records but the B-tree must exist (header node only). fsck
    // requires this — an "all-zero" extents fork descriptor fails verification.
    WriteForkData(vh.Slice(192, CatalogForkDataSize),
        (long)blockSize, blockSize, ExtentsBlock, 1u);

    // Catalog file: 2 blocks at CatalogStartBlock.
    WriteForkData(vh.Slice(272, CatalogForkDataSize),
        (long)catalogBlockCount * blockSize, blockSize, catalogStartBlock, catalogBlockCount);

    // Attributes B-tree file: not allocated (HFS+ allows empty attributes file).
    WriteForkData(vh.Slice(352, CatalogForkDataSize), 0L, 0u, 0u, 0u);

    // Startup file: empty (only used for special boot scenarios).
    WriteForkData(vh.Slice(432, CatalogForkDataSize), 0L, 0u, 0u, 0u);

    // ── Build extents-overflow B-tree (empty) ─────────────────────────────
    // Required even when empty: fsck.hfsplus refuses a volume whose
    // extentsFile fork descriptor in the volume header has totalBlocks=0.
    // We allocate 1 block (=1 node) at ExtentsBlock containing only a
    // header node with no leaf records.
    const ushort nodeSize = 4096;
    {
      var extBase = (int)(ExtentsBlock * blockSize);
      var extHeader = disk.AsSpan(extBase, nodeSize);
      extHeader[8] = 1; // kind = kBTHeaderNode
      extHeader[9] = 0; // height
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[10..], 3); // numRecords = 3

      var ehdr = extHeader[14..];
      BinaryPrimitives.WriteUInt16BigEndian(ehdr, 0);            // treeDepth = 0 (no leaf nodes)
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[2..], 0);       // rootNode = 0 (empty)
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[6..], 0);       // leafRecords = 0
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[10..], 0);      // firstLeafNode
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[14..], 0);      // lastLeafNode
      BinaryPrimitives.WriteUInt16BigEndian(ehdr[18..], nodeSize);
      BinaryPrimitives.WriteUInt16BigEndian(ehdr[20..], 10);     // maxKeyLength = HFSPlusExtentKey size = 10
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[22..], 1);      // totalNodes = 1
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[26..], 0);      // freeNodes = 0
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[32..], blockSize); // clumpSize
      ehdr[36] = 0; ehdr[37] = 0;                                 // btreeType, keyCompareType (binary)
      BinaryPrimitives.WriteUInt32BigEndian(ehdr[38..], 2);       // attributes = kBTBigKeysMask only

      // Offset table.
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 2)..], 14);  // BTHeaderRec
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 4)..], 120); // UserDataRec
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 6)..], 248); // BTMapRec
      BinaryPrimitives.WriteUInt16BigEndian(extHeader[(nodeSize - 8)..], (ushort)(nodeSize - 8));

      // Map: only this header node (node 0) used.
      extHeader[248] = 0x80;
    }

    // ── Build catalog B-tree ──────────────────────────────────────────────
    var catalogBase = (int)(catalogStartBlock * blockSize);

    // -- Node 0: Header node (TN1150 §2.5) --
    // Header node has exactly 3 records:
    //   #0 BTHeaderRec     @ offset 14, size 106 → ends at 120
    //   #1 UserDataRec     @ offset 120, size 128 → ends at 248
    //   #2 BTMapRec        @ offset 248, fills to (nodeSize - offset table - 8) bytes
    // Record offset table (uint16 BE each, written in reverse at end of node):
    //   slot[0] @ nodeSize-2 = 14   (BTHeaderRec start)
    //   slot[1] @ nodeSize-4 = 120  (UserDataRec start)
    //   slot[2] @ nodeSize-6 = 248  (BTMapRec start)
    //   slot[3] @ nodeSize-8 = freeOffset (one past BTMapRec)
    var headerNode = disk.AsSpan(catalogBase, nodeSize);
    headerNode[8] = 1; // kind = kBTHeaderNode (1)
    headerNode[9] = 0; // height = 0 for header node
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[10..], 3); // numRecords = 3

    // BTHeaderRec at offset 14. Field offsets per TN1150 §2.5.1:
    //   +0  treeDepth      (u16)
    //   +2  rootNode       (u32)
    //   +6  leafRecords    (u32)
    //   +10 firstLeafNode  (u32)
    //   +14 lastLeafNode   (u32)
    //   +18 nodeSize       (u16)
    //   +20 maxKeyLength   (u16)
    //   +22 totalNodes     (u32)
    //   +26 freeNodes      (u32)
    //   +30 reserved1      (u16)
    //   +32 clumpSize      (u32)
    //   +36 btreeType      (u8)
    //   +37 keyCompareType (u8)
    //   +38 attributes     (u32)
    //   +42 reserved3[16]  (u32×16)
    var hdr = headerNode[14..];
    BinaryPrimitives.WriteUInt16BigEndian(hdr, 1);                  // treeDepth = 1 (only leaves)
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], 1);             // rootNode = 1
    // leafRecords filled in after records computed, below.
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], 1);            // firstLeafNode = 1
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], 1);            // lastLeafNode = 1
    BinaryPrimitives.WriteUInt16BigEndian(hdr[18..], nodeSize);     // nodeSize
    BinaryPrimitives.WriteUInt16BigEndian(hdr[20..], 516);          // maxKeyLength (HFS+ catalog max)
    BinaryPrimitives.WriteUInt32BigEndian(hdr[22..], catalogBlockCount * (blockSize / nodeSize)); // totalNodes
    // freeNodes: totalNodes − (header + leaf used). 1 leaf + 1 header = 2 used.
    BinaryPrimitives.WriteUInt32BigEndian(hdr[26..],
        Math.Max(0u, (catalogBlockCount * (blockSize / nodeSize)) - 2u));
    BinaryPrimitives.WriteUInt32BigEndian(hdr[32..], blockSize);    // clumpSize
    hdr[36] = 0;                                                    // btreeType = kHFSBTreeType (0)
    hdr[37] = 0xCF;                                                 // keyCompareType = kHFSBinaryCompare (0xCF)
    // attributes: kBTBigKeysMask (2) | kBTVariableIndexKeysMask (4) = 6.
    // The HFS+ catalog uses big keys (u16 keyLength) and variable-length index keys.
    BinaryPrimitives.WriteUInt32BigEndian(hdr[38..], 6);

    // Record offsets (in reverse from end of node).
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 2)..], 14);  // BTHeaderRec
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 4)..], 120); // UserDataRec
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 6)..], 248); // BTMapRec
    // Free-space record marker = where BTMapRec ends. BTMapRec fills the space
    // between offset 248 and the offset table (at nodeSize-8). So freeOffset
    // = nodeSize - 8.
    BinaryPrimitives.WriteUInt16BigEndian(headerNode[(nodeSize - 8)..], (ushort)(nodeSize - 8));

    // Mark node 0 (header) and node 1 (leaf) as used in the BT map record.
    // BTMapRec is a bitmap of node usage starting at offset 248. Bit 0 of byte 0
    // covers node 0 (header), bit 1 covers node 1 (leaf), etc. MSB = lowest node.
    headerNode[248] = 0xC0; // bits 7,6 set → nodes 0 and 1 used

    // -- Node 1: Leaf node --
    var leafBase = catalogBase + nodeSize;
    var leafNode = disk.AsSpan(leafBase, nodeSize);
    leafNode[8] = 0xFF; // kind = -1 (leaf)
    leafNode[9] = 1;    // height = 1

    // Build catalog records. HFS+ B-tree leaf records MUST be sorted by their
    // catalog key (parentCNID ascending, then name binary-ascending). We collect
    // (sortParentCnid, sortName, recordBytes) tuples then sort.
    var keyed = new List<(uint Parent, string Name, byte[] Bytes)>();

    // Root folder record: key = (parentOfRoot=1, volumeName).
    // Per TN1150 §6.3, the root directory's catalog key uses the VOLUME NAME
    // as the directory name (not an empty string). The folder thread record's
    // body then names the root with the same volume name. fsck.hfsplus rejects
    // "" with "Invalid catalog record type (4, 1)".
    const string VolumeName = "untitled";
    keyed.Add((1u, VolumeName, BuildFolderRecord(RootFolderCnid, (uint)this._files.Count, VolumeName)));
    // Root folder thread: key = (rootCNID=2, "").
    keyed.Add((RootFolderCnid, "", BuildFolderThreadRecord(1, VolumeName)));

    // First free block for user data: catalog starts at block 3, takes 2 blocks → start at 5.
    var nextBlock = CatalogStartBlock + CatalogBlockCount;
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

      // File record: key = (parentCNID, name).
      keyed.Add((RootFolderCnid, name,
          BuildFileRecord(fileCnid, RootFolderCnid, name, (long)data.Length, startBlock, dataBlockCount2)));
      // File thread record: key = (fileCNID, ""). fsck.hfsplus checks
      // "fileCount == fileThread" (thread record count matches file count).
      keyed.Add((fileCnid, "",
          BuildFileThreadRecord(fileCnid, RootFolderCnid, name)));
    }

    // Sort records by (parent, name) — HFS+ binary compare uses UTF-16BE.
    keyed.Sort((a, b) => {
      if (a.Parent != b.Parent) return a.Parent.CompareTo(b.Parent);
      var an = Encoding.BigEndianUnicode.GetBytes(a.Name);
      var bn = Encoding.BigEndianUnicode.GetBytes(b.Name);
      var min = Math.Min(an.Length, bn.Length);
      for (var i = 0; i < min; i++) {
        if (an[i] != bn[i]) return an[i].CompareTo(bn[i]);
      }
      return an.Length.CompareTo(bn.Length);
    });

    var records = new List<byte[]>(keyed.Count);
    foreach (var kr in keyed) records.Add(kr.Bytes);

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

    // Now we know the leaf record count, fill in BTHeaderRec.leafRecords.
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], (uint)records.Count);

    // ── Allocation bitmap at AllocBlock ──────────────────────────────────
    // Mark used blocks: 0 (boot+VHB), 1 (alloc), 2 (extents), 3..3+catBlocks-1
    // (catalog), CatalogStartBlock+catalogBlockCount..nextBlock-1 (user data),
    // and totalBlocks-1 (alternate VHB sector resides in last block).
    var allocBase = (int)(AllocBlock * blockSize);
    void MarkUsed(uint blk) {
      if (blk >= totalBlocks) return;
      var byteIndex = allocBase + (int)(blk / 8);
      var bitIndex = (int)(7 - (blk % 8));
      if (byteIndex < disk.Length)
        disk[byteIndex] |= (byte)(1 << bitIndex);
    }
    // System-reserved blocks.
    MarkUsed(0);
    MarkUsed(AllocBlock);
    MarkUsed(ExtentsBlock);
    for (var b = catalogStartBlock; b < catalogStartBlock + catalogBlockCount; b++) MarkUsed(b);
    // User data blocks.
    for (var b = catalogStartBlock + catalogBlockCount; b < nextBlock; b++) MarkUsed(b);
    // Alt VHB lives in the last allocation block.
    MarkUsed(totalBlocks - 1);

    var usedBlocks = 5u + (uint)dataBlocksNeeded + 1u; // boot+alloc+ext+catalog(2)+data+altVH
    BinaryPrimitives.WriteUInt32BigEndian(vh[48..], totalBlocks - usedBlocks); // freeBlocks
    BinaryPrimitives.WriteUInt32BigEndian(vh[52..], nextBlock); // nextAllocation
    BinaryPrimitives.WriteUInt32BigEndian(vh[64..], nextCnid);  // nextCatalogID

    // ── Alternate Volume Header — byte-identical mirror of primary ───────
    // 512-byte block at (imageSize - 1024). Must match the primary so fsck's
    // cross-check passes. We copy the entire 512-byte sector starting at the
    // primary VHB offset (1024..1535).
    disk.AsSpan(VolumeHeaderOffset, 512).CopyTo(disk.AsSpan(alternateVhOffset, 512));

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

  private static byte[] BuildFolderRecord(uint cnid, uint valence, string name) {
    // For the root folder, key = (parentID=1, volumeName). For other folders,
    // key = (parentID, folderName).
    var key = BuildCatalogKey(1, name);
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

  /// <summary>
  /// Builds a folder thread record (recordType=3). The key is (myCNID, "")
  /// and the body contains parentCnid + my name.
  /// </summary>
  private static byte[] BuildFolderThreadRecord(uint parentCnid, string name) {
    // Thread record key uses the FOLDER's own CNID (always RootFolderCnid here
    // since this minimal writer only creates a single root folder).
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
    // flags = kHFSThreadExistsMask (0x0002) — required because we always
    // emit a paired file thread record. Without this, fsck reports
    // "Incorrect number of thread records" or "Invalid catalog record type".
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(2), 0x0002);
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

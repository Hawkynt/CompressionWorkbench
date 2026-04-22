using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hfs;

/// <summary>
/// Builds a spec-compliant Classic HFS disk image per
/// <i>Inside Macintosh: Files</i> (1992), chapter 2 "File Manager".
/// <para>
/// Current scope cuts:
/// <list type="bullet">
///   <item>Flat root directory only (no subdirectories).</item>
///   <item>Allocation block size fixed at 512 bytes.</item>
///   <item>Up to ~30 files — all records fit in a single 512-byte catalog leaf node.</item>
///   <item>ASCII-only filenames (no MacRoman high-byte handling).</item>
///   <item>No resource forks; resource-fork fields in file records are zero.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HfsWriter {
  private const int BootSize = 1024;       // 2 boot sectors
  private const int MdbOffset = 1024;
  private const int MdbSize = 512;         // MDB occupies one sector
  private const int NodeSize = 512;        // B-tree node size
  private const uint AllocBlockSize = 512; // allocation block size in bytes
  private const int MinTotalSectors = 800; // 400 KB minimum image (400 × 1024 / 512 = 800)

  // HFS epoch: 1904-01-01 UTC. .NET DateTime ticks start 0001-01-01.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  // Reserved CNIDs per Inside Macintosh.
  private const uint CnidRootParent = 1; // parent of root
  private const uint CnidRootDir = 2;    // root directory itself
  private const uint CnidExtentsFile = 3;
  private const uint CnidCatalogFile = 4;
  private const uint CnidFirstUser = 16;

  // Catalog record types.
  private const byte RecFolder = 1;
  private const byte RecFile = 2;
  private const byte RecFolderThread = 3;
  private const byte RecFileThread = 4;

  // B-tree node kinds.
  private const sbyte KindIndex = 0;
  private const sbyte KindHeader = 1;
  private const sbyte KindMap = 2;
  private const sbyte KindLeaf = -1;

  // Max key lengths.
  private const byte MaxCatalogKeyLen = 37; // 1 resrv + 4 parentID + 1 nameLen + 31 name
  private const byte MaxExtentsKeyLen = 7;  // 1 forkType + 4 fileID + 2 startBlock

  private readonly List<(string Name, byte[] Data)> _files = [];
  private string _volumeName = "Untitled";

  /// <summary>Sets the HFS volume name (1–27 ASCII chars).</summary>
  public void SetVolumeName(string name) {
    ArgumentNullException.ThrowIfNull(name);
    if (name.Length is 0 or > 27) throw new ArgumentOutOfRangeException(nameof(name), "HFS volume name must be 1–27 chars.");
    _volumeName = name;
  }

  /// <summary>Adds a file to the image root directory.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length is 0 or > 31) throw new ArgumentOutOfRangeException(nameof(name), "HFS file name must be 1–31 chars.");
    this._files.Add((name, data));
  }

  /// <summary>Builds the HFS disk image.</summary>
  public byte[] Build() {
    // --- 1. Compute layout -------------------------------------------------

    // Sort files by (parentID, name) — all have parent = root (cnid=2),
    // so a simple name sort is stable for catalog key ordering.
    var files = this._files
      .Select((f, i) => (f.Name, f.Data, Cnid: CnidFirstUser + (uint)i))
      .OrderBy(f => f.Name, StringComparer.Ordinal)
      .ToList();

    // Fixed allocation-block layout:
    //   abs block  0..1 : extents B-tree  (header node + leaf node = 2 × 512)
    //   abs block  2..3 : catalog B-tree  (header node + leaf node = 2 × 512)
    //   abs block  4..  : file data       (one contiguous extent per file)
    const ushort ExtentsStartAbs = 0;
    const ushort ExtentsBlockCount = 2;
    const ushort CatalogStartAbs = 2;
    const ushort CatalogBlockCount = 2;
    const ushort FirstDataBlock = 4;

    var fileExtents = new List<(ushort StartAbs, ushort BlockCount)>();
    var nextBlock = (uint)FirstDataBlock;
    foreach (var f in files) {
      var blocks = (uint)((f.Data.Length + AllocBlockSize - 1) / AllocBlockSize);
      if (blocks > ushort.MaxValue) throw new InvalidDataException("HFS: file too large for 16-bit extent.");
      fileExtents.Add(((ushort)nextBlock, (ushort)blocks));
      nextBlock += blocks;
    }

    // Total allocation blocks must cover all data. Round up to whatever gives
    // us a 400 KB minimum image.
    var minAllocBlocks = Math.Max((uint)nextBlock, 1u);
    var bitmapSectors = (int)((minAllocBlocks + 8 * 512 - 1) / (8 * 512));
    if (bitmapSectors < 1) bitmapSectors = 1;

    // drAlBlSt = first sector belonging to allocation-block space.
    //   sector 0..1 = boot, sector 2 = MDB, sectors 3..(3+bitmapSectors-1) = bitmap
    //   allocation-block #0 starts right after the bitmap.
    var drVBMSt = (ushort)3;
    var drAlBlSt = (ushort)(drVBMSt + bitmapSectors);

    // Image: boot + MDB + bitmap + (allocBlocks × 512) + alternate MDB + reserved.
    // Minimum 400 KB (800 sectors).
    var dataSectorsNeeded = nextBlock * (AllocBlockSize / 512u);
    var tailSectors = 2; // alt MDB + reserved
    var totalSectors = drAlBlSt + (int)dataSectorsNeeded + tailSectors;
    if (totalSectors < MinTotalSectors) totalSectors = MinTotalSectors;

    // Grow drNmAlBlks so bitmap + allocation space fills the image cleanly.
    var drNmAlBlks = (ushort)((totalSectors - drAlBlSt - tailSectors) * 512u / AllocBlockSize);
    if (drNmAlBlks < nextBlock) throw new InvalidDataException("HFS: layout underflow.");

    // Free blocks count (everything past last used file).
    var used = nextBlock;
    var drFreeBks = (ushort)(drNmAlBlks - used);

    var image = new byte[totalSectors * 512];

    // --- 2. Volume bitmap (allocated blocks = 1-bits, MSB-first) ----------

    var bitmapOffset = drVBMSt * 512;
    for (uint b = 0; b < used; b++) {
      var byteIdx = (int)(b >> 3);
      var bitInByte = 7 - (int)(b & 7);
      image[bitmapOffset + byteIdx] |= (byte)(1 << bitInByte);
    }

    // --- 3. Write file data -----------------------------------------------

    var allocBase = drAlBlSt * 512;
    for (var i = 0; i < files.Count; i++) {
      var data = files[i].Data;
      var (startAbs, _) = fileExtents[i];
      var off = allocBase + (int)(startAbs * AllocBlockSize);
      if (data.Length > 0) data.CopyTo(image.AsSpan(off));
    }

    // --- 4. Extents B-tree ------------------------------------------------
    //
    // Leaf records: one per fork (cnid=3 extents file, cnid=4 catalog file).
    // We don't add leaf records for user files because all user files fit in
    // 3 inline extents inside the file record (which is the normal HFS case).
    //
    // Extents key (7 bytes): forkType(1) + fileID(4) + startBlock(2).

    var extentsLeafRecs = new List<byte[]> {
      BuildExtentsLeafRecord(forkType: 0, fileID: CnidExtentsFile, startBlock: 0, ExtentsStartAbs, ExtentsBlockCount),
      BuildExtentsLeafRecord(forkType: 0, fileID: CnidCatalogFile, startBlock: 0, CatalogStartAbs, CatalogBlockCount),
    };

    var extentsBaseOffset = allocBase + ExtentsStartAbs * (int)AllocBlockSize;
    WriteBTreeHeaderNode(image.AsSpan(extentsBaseOffset, NodeSize),
      treeDepth: 1, rootNode: 1, leafRecords: (uint)extentsLeafRecs.Count,
      firstLeaf: 1, lastLeaf: 1, totalNodes: 2, freeNodes: 0,
      maxKeyLen: MaxExtentsKeyLen);
    WriteLeafNode(image.AsSpan(extentsBaseOffset + NodeSize, NodeSize),
      extentsLeafRecs, prevLeaf: 0, nextLeaf: 0, height: 1);

    // --- 5. Catalog B-tree ------------------------------------------------
    //
    // Leaf records (sorted by HFS key order — (parentID, name) ASCII):
    //   1. Thread record for root directory itself:
    //        key = (parentID=CnidRootDir, name="")
    //        data = file-thread/dir-thread pointing to its parent (CnidRootParent)
    //             and the volume name.
    //   2. Directory record for root:
    //        key = (parentID=CnidRootParent, name=<volumeName>)
    //   3..N. For each user file:
    //        a. File record: key=(CnidRootDir, fileName), data=file record
    //        b. File-thread record: key=(CnidOfFile, ""), data points back to root
    //
    // With a simple ASCII keyspace and a flat root, the sort order is:
    //   thread(root, "") then everything in parent=CnidRootDir sorted by name.
    //   The root-dir record lives under parent=CnidRootParent which sorts BEFORE
    //   CnidRootDir numerically.
    //
    // File-thread records are keyed by (fileCnid, "") so they sort after all
    // entries under CnidRootDir. We'll emit them in ascending CNID order.

    var now = (uint)ToHfsTime(DateTime.UtcNow);

    var catRecs = new List<byte[]>();

    // Key order for a flat volume, in order the leaf must emit them:
    //  (1) Thread for root dir: parentID=CnidRootDir (2), name=""   — sorts first among "parent=2"
    //  (2) Files/dir records under parent=CnidRootDir (2), sorted by name
    //  (3) Dir record for root: parentID=CnidRootParent (1), name=volname — parent=1 < parent=2 so this comes FIRST
    //  (4) Thread records for each file: parentID=fileCnid (>=16), name=""

    // Order:
    //   A. Root-dir record  (parent=1)
    //   B. Root thread      (parent=2, name="")
    //   C. File records     (parent=2, name=<file>)  in ascending ASCII order
    //   D. File threads     (parent=fileCnid, name="")  ascending fileCnid
    catRecs.Add(BuildDirRecord(
      parentID: CnidRootParent, name: this._volumeName,
      dirID: CnidRootDir, valence: (ushort)files.Count,
      crDate: now, mdDate: now));

    catRecs.Add(BuildThreadRecord(
      type: RecFolderThread,
      keyParentID: CnidRootDir, keyName: "",
      targetParent: CnidRootParent, targetName: this._volumeName));

    for (var i = 0; i < files.Count; i++) {
      var (fname, fdata, cnid) = files[i];
      var (startAbs, bcount) = fileExtents[i];
      catRecs.Add(BuildFileRecord(
        parentID: CnidRootDir, name: fname, fileID: cnid,
        dataStart: startAbs, dataBlocks: bcount, dataSize: (uint)fdata.Length,
        crDate: now, mdDate: now));
    }

    for (var i = 0; i < files.Count; i++) {
      var (fname, _, cnid) = files[i];
      catRecs.Add(BuildThreadRecord(
        type: RecFileThread,
        keyParentID: cnid, keyName: "",
        targetParent: CnidRootDir, targetName: fname));
    }

    var catalogBaseOffset = allocBase + CatalogStartAbs * (int)AllocBlockSize;
    WriteBTreeHeaderNode(image.AsSpan(catalogBaseOffset, NodeSize),
      treeDepth: 1, rootNode: 1, leafRecords: (uint)catRecs.Count,
      firstLeaf: 1, lastLeaf: 1, totalNodes: 2, freeNodes: 0,
      maxKeyLen: MaxCatalogKeyLen);
    WriteLeafNode(image.AsSpan(catalogBaseOffset + NodeSize, NodeSize),
      catRecs, prevLeaf: 0, nextLeaf: 0, height: 1);

    // --- 6. Master Directory Block ----------------------------------------

    var nRtFiles = (ushort)files.Count;
    WriteMdb(image.AsSpan(MdbOffset),
      crDate: now, mdDate: now,
      drNmFls: nRtFiles,
      drVBMSt: drVBMSt,
      drAllocPtr: (ushort)used,
      drNmAlBlks: drNmAlBlks,
      drAlBlSt: drAlBlSt,
      drNxtCNID: CnidFirstUser + (uint)files.Count,
      drFreeBks: drFreeBks,
      drFilCnt: (uint)files.Count,
      drDirCnt: 0,
      extentsStartAbs: ExtentsStartAbs, extentsBlockCount: ExtentsBlockCount,
      catalogStartAbs: CatalogStartAbs, catalogBlockCount: CatalogBlockCount,
      volumeName: this._volumeName);

    // Alternate MDB at second-to-last sector.
    image.AsSpan(MdbOffset, MdbSize)
      .CopyTo(image.AsSpan((totalSectors - 2) * 512));

    return image;
  }

  // ------------------------------------------------------------------------
  // MDB
  // ------------------------------------------------------------------------

  private static void WriteMdb(Span<byte> mdb,
    uint crDate, uint mdDate,
    ushort drNmFls, ushort drVBMSt, ushort drAllocPtr, ushort drNmAlBlks, ushort drAlBlSt,
    uint drNxtCNID, ushort drFreeBks, uint drFilCnt, uint drDirCnt,
    ushort extentsStartAbs, ushort extentsBlockCount,
    ushort catalogStartAbs, ushort catalogBlockCount,
    string volumeName) {
    mdb.Clear();
    BinaryPrimitives.WriteUInt16BigEndian(mdb, 0x4244);            // drSigWord "BD"
    BinaryPrimitives.WriteUInt32BigEndian(mdb[2..], crDate);       // drCrDate
    BinaryPrimitives.WriteUInt32BigEndian(mdb[6..], mdDate);       // drLsMod
    BinaryPrimitives.WriteUInt16BigEndian(mdb[10..], 0x0100);      // drAtrb — bit 8 = unmounted-cleanly
    BinaryPrimitives.WriteUInt16BigEndian(mdb[12..], drNmFls);     // drNmFls
    BinaryPrimitives.WriteUInt16BigEndian(mdb[14..], drVBMSt);     // drVBMSt
    BinaryPrimitives.WriteUInt16BigEndian(mdb[16..], drAllocPtr);  // drAllocPtr
    BinaryPrimitives.WriteUInt16BigEndian(mdb[18..], drNmAlBlks);  // drNmAlBlks
    BinaryPrimitives.WriteUInt32BigEndian(mdb[20..], AllocBlockSize); // drAlBlkSiz
    BinaryPrimitives.WriteUInt32BigEndian(mdb[24..], 4 * AllocBlockSize); // drClpSiz
    BinaryPrimitives.WriteUInt16BigEndian(mdb[28..], drAlBlSt);    // drAlBlSt
    BinaryPrimitives.WriteUInt32BigEndian(mdb[30..], drNxtCNID);   // drNxtCNID
    BinaryPrimitives.WriteUInt16BigEndian(mdb[34..], drFreeBks);   // drFreeBks

    // Volume name: Pascal string, 28 bytes total (1 length + 27 name).
    var nameBytes = Encoding.ASCII.GetBytes(volumeName);
    if (nameBytes.Length > 27) nameBytes = nameBytes.AsSpan(0, 27).ToArray();
    mdb[36] = (byte)nameBytes.Length;
    nameBytes.CopyTo(mdb[37..]);

    BinaryPrimitives.WriteUInt32BigEndian(mdb[64..], 0);           // drVolBkUp
    BinaryPrimitives.WriteUInt16BigEndian(mdb[68..], 0);           // drVSeqNum
    BinaryPrimitives.WriteUInt32BigEndian(mdb[70..], 1);           // drWrCnt
    BinaryPrimitives.WriteUInt32BigEndian(mdb[74..], 4 * AllocBlockSize); // drXTClpSiz
    BinaryPrimitives.WriteUInt32BigEndian(mdb[78..], 4 * AllocBlockSize); // drCTClpSiz
    BinaryPrimitives.WriteUInt16BigEndian(mdb[82..], 0);           // drNmRtDirs
    BinaryPrimitives.WriteUInt32BigEndian(mdb[84..], drFilCnt);    // drFilCnt
    BinaryPrimitives.WriteUInt32BigEndian(mdb[88..], drDirCnt);    // drDirCnt
    // drFndrInfo[8] at offset 92 — all zeros (already cleared).
    BinaryPrimitives.WriteUInt16BigEndian(mdb[124..], 0);          // drVCSize
    BinaryPrimitives.WriteUInt16BigEndian(mdb[126..], 0);          // drVBMCSize
    BinaryPrimitives.WriteUInt16BigEndian(mdb[128..], 0);          // drCtlCSize

    // Extents tree file extents at offset 130..145: drXTFlSize + 3×(startBlk, blockCnt)
    BinaryPrimitives.WriteUInt32BigEndian(mdb[130..], (uint)(extentsBlockCount * AllocBlockSize)); // drXTFlSize
    BinaryPrimitives.WriteUInt16BigEndian(mdb[134..], extentsStartAbs);
    BinaryPrimitives.WriteUInt16BigEndian(mdb[136..], extentsBlockCount);
    // remaining 2 extent descriptors are zero.

    // Catalog tree file extents at offset 146..161
    BinaryPrimitives.WriteUInt32BigEndian(mdb[146..], (uint)(catalogBlockCount * AllocBlockSize)); // drCTFlSize
    BinaryPrimitives.WriteUInt16BigEndian(mdb[150..], catalogStartAbs);
    BinaryPrimitives.WriteUInt16BigEndian(mdb[152..], catalogBlockCount);
  }

  // ------------------------------------------------------------------------
  // B-tree nodes
  // ------------------------------------------------------------------------

  private static void WriteBTreeHeaderNode(Span<byte> node,
    ushort treeDepth, uint rootNode, uint leafRecords,
    uint firstLeaf, uint lastLeaf, uint totalNodes, uint freeNodes,
    byte maxKeyLen) {
    node.Clear();
    // Node descriptor.
    BinaryPrimitives.WriteUInt32BigEndian(node[0..], 0);       // fLink
    BinaryPrimitives.WriteUInt32BigEndian(node[4..], 0);       // bLink
    node[8] = unchecked((byte)KindHeader);                     // kind = 1
    node[9] = 0;                                               // height (header node: 0)
    BinaryPrimitives.WriteUInt16BigEndian(node[10..], 3);      // numRecords: BTHdrRec, reserved, BTMapRec
    BinaryPrimitives.WriteUInt16BigEndian(node[12..], 0);      // reserved

    // Record 1: BTHdrRec (106 bytes) at offset 14.
    var hdr = node[14..];
    BinaryPrimitives.WriteUInt16BigEndian(hdr[0..], treeDepth);        // bthDepth
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], rootNode);         // bthRoot
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], leafRecords);      // bthNRecs
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], firstLeaf);       // bthFNode
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], lastLeaf);        // bthLNode
    BinaryPrimitives.WriteUInt16BigEndian(hdr[18..], NodeSize);        // bthNodeSize
    BinaryPrimitives.WriteUInt16BigEndian(hdr[20..], maxKeyLen);       // bthKeyLen (max key length)
    BinaryPrimitives.WriteUInt32BigEndian(hdr[22..], totalNodes);      // bthNNodes
    BinaryPrimitives.WriteUInt32BigEndian(hdr[26..], freeNodes);       // bthFree
    // hdr[30..106] reserved (74 bytes of zeros).

    // Record 2: 128 bytes reserved/unused — offset 120.
    // Record 3: BTMapRec — bitmap of allocated nodes. We have 2 nodes allocated.
    // Header+reserved occupy 14..247 (14 + 106 + 128 = 248). BTMapRec at 248.
    const int bthRecOffset = 14;
    const int reservedRecOffset = 14 + 106;   // 120
    const int mapRecOffset = reservedRecOffset + 128; // 248
    // Mark nodes 0 and 1 as allocated (MSB-first within byte).
    node[mapRecOffset] = 0b11000000;

    // Free-space offset: first byte of BTMapRec is mapRecOffset; the map itself
    // extends to the end of the node minus the pointer list. For our tiny tree
    // we leave only a trivial map.

    // Pointer list at end: 4 offsets (numRecords + 1 free-space pointer),
    // each a uint16 BE, stored in REVERSE order (last record's offset closest to
    // end-of-node boundary).
    var mapRecEnd = mapRecOffset + (NodeSize - mapRecOffset - 2 * 4); // whatever space remains
    // Slot offsets end of node (counted from end):
    //   end-2 : offset of record 1 (BTHdrRec)
    //   end-4 : offset of record 2 (reserved)
    //   end-6 : offset of record 3 (BTMapRec)
    //   end-8 : free-space pointer
    BinaryPrimitives.WriteUInt16BigEndian(node[(NodeSize - 2)..], (ushort)bthRecOffset);
    BinaryPrimitives.WriteUInt16BigEndian(node[(NodeSize - 4)..], (ushort)reservedRecOffset);
    BinaryPrimitives.WriteUInt16BigEndian(node[(NodeSize - 6)..], (ushort)mapRecOffset);
    BinaryPrimitives.WriteUInt16BigEndian(node[(NodeSize - 8)..], (ushort)mapRecEnd);
  }

  private static void WriteLeafNode(Span<byte> node, List<byte[]> records,
    uint prevLeaf, uint nextLeaf, byte height) {
    node.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(node[0..], nextLeaf); // fLink
    BinaryPrimitives.WriteUInt32BigEndian(node[4..], prevLeaf); // bLink
    node[8] = unchecked((byte)KindLeaf);                        // kind = -1 (0xFF)
    node[9] = height;                                           // height = 1 for leaf level
    BinaryPrimitives.WriteUInt16BigEndian(node[10..], (ushort)records.Count); // numRecords
    BinaryPrimitives.WriteUInt16BigEndian(node[12..], 0);       // reserved

    // Pointer-list size = 2 × (numRecords + 1) bytes at end of node.
    // Reject anything that won't fit.
    var pointerListBytes = 2 * (records.Count + 1);
    var dataArea = NodeSize - 14 - pointerListBytes;
    var total = records.Sum(r => r.Length);
    if (total > dataArea)
      throw new InvalidDataException($"HFS: leaf node overflow ({total} > {dataArea} bytes). Reduce file count/name length.");

    var pos = 14;
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      rec.CopyTo(node[pos..]);
      // Pointer list: slot i (0-based) is at node[NodeSize - 2*(i+1)]
      BinaryPrimitives.WriteUInt16BigEndian(node[(NodeSize - 2 * (i + 1))..], (ushort)pos);
      pos += rec.Length;
    }
    // Free-space pointer (after last record).
    BinaryPrimitives.WriteUInt16BigEndian(node[(NodeSize - 2 * (records.Count + 1))..], (ushort)pos);
  }

  // ------------------------------------------------------------------------
  // Catalog records
  // ------------------------------------------------------------------------

  private static byte[] BuildDirRecord(uint parentID, string name,
    uint dirID, ushort valence, uint crDate, uint mdDate) {
    var key = BuildCatalogKey(parentID, name);
    const int DataSize = 70;
    var rec = new byte[AlignEven(key.Length) + DataSize];
    key.CopyTo(rec, 0);
    var d = rec.AsSpan(AlignEven(key.Length));
    d[0] = RecFolder;
    d[1] = 0;
    BinaryPrimitives.WriteUInt16BigEndian(d[2..], 0);       // dirFlags
    BinaryPrimitives.WriteUInt16BigEndian(d[4..], valence); // dirVal
    BinaryPrimitives.WriteUInt32BigEndian(d[6..], dirID);   // dirDirID
    BinaryPrimitives.WriteUInt32BigEndian(d[10..], crDate); // dirCrDat
    BinaryPrimitives.WriteUInt32BigEndian(d[14..], mdDate); // dirMdDat
    BinaryPrimitives.WriteUInt32BigEndian(d[18..], 0);      // dirBkDat
    // dirUsrInfo[16] at 22, dirFndrInfo[16] at 38, dirResrv[4]×uint32 at 54 — all zero.
    return rec;
  }

  private static byte[] BuildFileRecord(uint parentID, string name, uint fileID,
    ushort dataStart, ushort dataBlocks, uint dataSize, uint crDate, uint mdDate) {
    var key = BuildCatalogKey(parentID, name);
    const int DataSize = 102;
    var rec = new byte[AlignEven(key.Length) + DataSize];
    key.CopyTo(rec, 0);
    var d = rec.AsSpan(AlignEven(key.Length));
    d[0] = RecFile;
    d[1] = 0;
    d[2] = 0;                                               // filFlags
    d[3] = 0;                                               // filTyp
    // filUsrWds[16] at 4 — zero
    BinaryPrimitives.WriteUInt32BigEndian(d[20..], fileID); // filFlNum
    BinaryPrimitives.WriteUInt16BigEndian(d[24..], dataStart); // filStBlk
    BinaryPrimitives.WriteUInt32BigEndian(d[26..], dataSize);  // filLgLen
    BinaryPrimitives.WriteUInt32BigEndian(d[30..], (uint)(dataBlocks * AllocBlockSize)); // filPyLen
    BinaryPrimitives.WriteUInt16BigEndian(d[34..], 0);      // filRStBlk
    BinaryPrimitives.WriteUInt32BigEndian(d[36..], 0);      // filRLgLen
    BinaryPrimitives.WriteUInt32BigEndian(d[40..], 0);      // filRPyLen
    BinaryPrimitives.WriteUInt32BigEndian(d[44..], crDate); // filCrDat
    BinaryPrimitives.WriteUInt32BigEndian(d[48..], mdDate); // filMdDat
    BinaryPrimitives.WriteUInt32BigEndian(d[52..], 0);      // filBkDat
    // filFndrInfo[16] at 56 — zero
    BinaryPrimitives.WriteUInt16BigEndian(d[72..], (ushort)(4 * AllocBlockSize)); // filClpSize

    // Data-fork extents at offset 74: 3 × (startAllocBlock uint16 + blockCount uint16)
    BinaryPrimitives.WriteUInt16BigEndian(d[74..], dataStart);
    BinaryPrimitives.WriteUInt16BigEndian(d[76..], dataBlocks);
    // extents 2 and 3 zero.

    // Resource-fork extents at 86 — all zero.
    BinaryPrimitives.WriteUInt32BigEndian(d[98..], 0);      // filResrv
    return rec;
  }

  private static byte[] BuildThreadRecord(byte type,
    uint keyParentID, string keyName,
    uint targetParent, string targetName) {
    var key = BuildCatalogKey(keyParentID, keyName);
    const int DataSize = 46;
    var rec = new byte[AlignEven(key.Length) + DataSize];
    key.CopyTo(rec, 0);
    var d = rec.AsSpan(AlignEven(key.Length));
    d[0] = type;
    d[1] = 0;
    // thdResrv[8] at offset 2 — zero
    BinaryPrimitives.WriteUInt32BigEndian(d[10..], targetParent); // thdParID
    // thdCName at offset 14: Pascal string (1 + up to 31 bytes)
    var nameBytes = Encoding.ASCII.GetBytes(targetName);
    if (nameBytes.Length > 31) nameBytes = nameBytes.AsSpan(0, 31).ToArray();
    d[14] = (byte)nameBytes.Length;
    nameBytes.CopyTo(d[15..]);
    return rec;
  }

  /// <summary>
  /// Builds a Pascal-string HFS catalog key:
  /// <c>keyLen(1) + resrv1(1) + parentID(4) + nameLen(1) + name</c>.
  /// keyLen covers everything after itself. Returned buffer has the key bytes
  /// with whatever length they naturally take (callers align to even).
  /// </summary>
  private static byte[] BuildCatalogKey(uint parentID, string name) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    if (nameBytes.Length > 31) throw new ArgumentOutOfRangeException(nameof(name));
    var keyLen = (byte)(1 + 4 + 1 + nameBytes.Length); // resrv1 + parentID + nameLen + name
    var buf = new byte[1 + keyLen];
    buf[0] = keyLen;
    buf[1] = 0; // resrv1
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(2), parentID);
    buf[6] = (byte)nameBytes.Length;
    nameBytes.CopyTo(buf, 7);
    return buf;
  }

  private static byte[] BuildExtentsLeafRecord(byte forkType, uint fileID, ushort startBlock,
    ushort extentStart, ushort extentBlocks) {
    // Key: keyLen(1) + forkType(1) + fileID(4) + startBlock(2) = 8 bytes total
    // Data: 3 × (startBlock uint16, blockCount uint16) = 12 bytes
    var rec = new byte[8 + 12];
    rec[0] = 7; // keyLen = forkType + fileID + startBlock = 7
    rec[1] = forkType;
    BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(2), fileID);
    BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(6), startBlock);
    BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(8), extentStart);
    BinaryPrimitives.WriteUInt16BigEndian(rec.AsSpan(10), extentBlocks);
    // remaining 2 extents zero
    return rec;
  }

  private static int AlignEven(int n) => (n + 1) & ~1;

  private static long ToHfsTime(DateTime utc) {
    if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    var s = (long)(utc.ToUniversalTime() - HfsEpoch).TotalSeconds;
    if (s < 0) s = 0;
    if (s > uint.MaxValue) s = uint.MaxValue;
    return s;
  }
}

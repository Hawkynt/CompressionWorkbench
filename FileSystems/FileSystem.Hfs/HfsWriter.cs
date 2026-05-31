using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hfs;

/// <summary>
/// Builds a spec-compliant Classic HFS disk image per
/// <i>Inside Macintosh: Files</i> (1992), chapter 2 "File Manager".
/// <para>
/// Layout matches what hfsutils' libhfs expects: 512-byte B*-tree nodes
/// (libhfs hardcodes <c>HFS_BLOCKSZ</c>=512 and validates header-record
/// offsets at exactly 0x00e/0x078/0x0f8/0x1f8). When records can't fit a
/// single leaf, the catalog grows into multiple leaf nodes (chained via the
/// node-descriptor fLink/bLink) and one or more index levels are stacked above
/// them, increasing the tree depth until a single root node fans out over the
/// whole level below it. The header node's BTMapRec caps the catalog at 2048
/// nodes (no chained map nodes), which still permits well over a thousand
/// entries in a single directory.
/// </para>
/// <para>
/// Names passed to <see cref="AddFile(string, byte[])"/> may contain
/// <c>'/'</c> separators denoting subdirectories. Each path component below the
/// final one becomes a real catalog folder (directory record type 1 + directory
/// thread type 3) with its own dirID, inserted under its parent's dirID; the
/// file lands keyed under its immediate parent folder's dirID.
/// </para>
/// <para>
/// Current scope cuts:
/// <list type="bullet">
///   <item>Allocation block size fixed at 512 bytes.</item>
///   <item>ASCII-only filenames (no MacRoman high-byte handling).</item>
///   <item>No resource forks; resource-fork fields in file records are zero.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HfsWriter {
  private const int MdbOffset = 1024;      // MDB lives in sector 2 (after 2 boot sectors)
  private const int MdbSize = 512;         // MDB occupies one sector
  // hfsutils libhfs hardcodes HFS_BLOCKSZ=512 for both extents and catalog
  // B*-tree node size; we MUST match exactly or `bt_readhdr` rejects the
  // image with "malformed b*-tree header node".
  private const int BTreeNodeSize = 512;
  private const int ExtentsNodeSize = BTreeNodeSize;
  private const int CatalogNodeSize = BTreeNodeSize;
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

  /// <summary>
  /// Adds a file to the image. The <paramref name="name"/> may contain
  /// <c>'/'</c> (or <c>'\'</c>) separators to place the file inside a
  /// subdirectory tree; each path component must be 1–31 chars. A name with no
  /// separator lands in the volume root.
  /// </summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var components = SplitPath(name);
    if (components.Length == 0)
      throw new ArgumentOutOfRangeException(nameof(name), "HFS path must contain at least one component.");
    foreach (var component in components)
      if (component.Length is 0 or > 31)
        throw new ArgumentOutOfRangeException(nameof(name), "Each HFS path component must be 1–31 chars.");
    this._files.Add((name, data));
  }

  /// <summary>Splits an AddFile path into its non-empty components.</summary>
  private static string[] SplitPath(string name)
    => name.Split('/', '\\').Where(c => c.Length > 0).ToArray();

  /// <summary>Builds the HFS disk image.</summary>
  public byte[] Build() {
    // --- 1. Compute layout -------------------------------------------------

    // Resolve the AddFile path set into a folder hierarchy: every intermediate
    // path component becomes a folder with its own dirID, and each file records
    // the dirID of its immediate parent folder. Folders are numbered first so
    // their CNIDs stay stable regardless of file count.
    var tree = BuildDirectoryTree(this._files);
    var folders = tree.Folders;

    // Each file is assigned a CNID after all folders. Sort the (parent, name)
    // keyspace later when laying out the catalog leaves; here we just attach a
    // CNID and the parent folder dirID resolved from the tree.
    var files = tree.Files
      .Select((f, i) => (f.LeafName, f.Data, f.ParentDirId, Cnid: tree.FirstFileCnid + (uint)i))
      .ToList();

    // Plan catalog leaf assignment based on record SIZES (which only depend on
    // names, not extent positions). This lets us size the catalog file before
    // we know where file data lives.
    var leafAssignments = PlanCatalogLeaves(files, folders, this._volumeName);
    var hasIndexNode = leafAssignments.Count > 1;
    // When there is more than one leaf the tree grows an index level above the
    // leaves. A single index node holds at most IndexRecordsPerNode pointers, so
    // very large directories need several index nodes plus higher index levels
    // until one node fans out over the whole level below it. Compute the per-level
    // node counts (bottom index level first, root last) so we can size the file.
    var indexLevelCounts = PlanIndexLevels(leafAssignments.Count);
    var indexNodeTotal = indexLevelCounts.Sum();
    // catalog node count: header + N leaves + every index node
    var catalogNodeCount = 1 + leafAssignments.Count + indexNodeTotal;
    var catalogTotalBytes = catalogNodeCount * CatalogNodeSize;
    var catalogBlockCount = (catalogTotalBytes + (int)AllocBlockSize - 1) / (int)AllocBlockSize;

    // Fixed allocation-block layout:
    //   abs block  0..1            : extents B-tree  (header node + leaf node = 2 × 512)
    //   abs block  2..(2+CB-1)     : catalog B-tree  (sized dynamically)
    //   abs block  (2+CB)..        : file data       (one contiguous extent per file)
    const ushort ExtentsStartAbs = 0;
    const ushort ExtentsBlockCount = 2;                                     // 2 × 512 = 1024 B
    const ushort CatalogStartAbs = 2;
    var firstDataBlock = (ushort)(CatalogStartAbs + catalogBlockCount);

    var fileExtents = new List<(ushort StartAbs, ushort BlockCount)>();
    var nextBlock = (uint)firstDataBlock;
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
      BuildExtentsLeafRecord(forkType: 0, fileID: CnidCatalogFile, startBlock: 0, CatalogStartAbs, (ushort)catalogBlockCount),
    };

    var extentsBaseOffset = allocBase + ExtentsStartAbs * (int)AllocBlockSize;
    WriteBTreeHeaderNode(image.AsSpan(extentsBaseOffset, ExtentsNodeSize),
      treeDepth: 1, rootNode: 1, leafRecords: (uint)extentsLeafRecs.Count,
      firstLeaf: 1, lastLeaf: 1, totalNodes: 2, freeNodes: 0,
      maxKeyLen: MaxExtentsKeyLen, nodeSize: ExtentsNodeSize);
    WriteLeafNode(image.AsSpan(extentsBaseOffset + ExtentsNodeSize, ExtentsNodeSize),
      extentsLeafRecs, prevLeaf: 0, nextLeaf: 0, height: 1, nodeSize: ExtentsNodeSize);

    // --- 5. Catalog B-tree ------------------------------------------------
    //
    // The catalog B*-tree is keyed by (parentDirID, CName) and must hold its
    // leaf records in HFS key order: parentDirID ascending, then CName by the
    // case-insensitive MacRoman comparison libhfs uses. For each folder we emit
    // a directory record (type 1, keyed under its parent) plus a directory
    // thread (type 3, keyed under its own dirID, name=""). For each file we emit
    // a file record (type 2, keyed under its parent folder) plus a file thread
    // (type 4, keyed under its own CNID, name=""). The volume root is folder
    // dirID=2 keyed under CnidRootParent (=1) with the volume name.

    var now = (uint)ToHfsTime(DateTime.UtcNow);

    // Collect every catalog record with its (parentDirID, CName) sort key, then
    // sort into HFS key order before laying them into leaves.
    var keyed = new List<(uint Parent, string Name, byte[] Record)>();

    // Root directory record (parent=1, name=<volume>) and its thread (parent=2).
    keyed.Add((CnidRootParent, this._volumeName, BuildDirRecord(
      parentID: CnidRootParent, name: this._volumeName,
      dirID: CnidRootDir, valence: (ushort)tree.RootValence,
      crDate: now, mdDate: now)));
    keyed.Add((CnidRootDir, "", BuildThreadRecord(
      type: RecFolderThread,
      keyParentID: CnidRootDir, keyName: "",
      targetParent: CnidRootParent, targetName: this._volumeName)));

    // Subdirectory folders: directory record (under parent) + directory thread.
    foreach (var folder in folders) {
      keyed.Add((folder.ParentDirId, folder.Name, BuildDirRecord(
        parentID: folder.ParentDirId, name: folder.Name,
        dirID: folder.DirId, valence: (ushort)folder.Valence,
        crDate: now, mdDate: now)));
      keyed.Add((folder.DirId, "", BuildThreadRecord(
        type: RecFolderThread,
        keyParentID: folder.DirId, keyName: "",
        targetParent: folder.ParentDirId, targetName: folder.Name)));
    }

    // Files: file record (under parent folder) + file thread.
    for (var i = 0; i < files.Count; i++) {
      var (fname, fdata, parentDirId, cnid) = files[i];
      var (startAbs, bcount) = fileExtents[i];
      keyed.Add((parentDirId, fname, BuildFileRecord(
        parentID: parentDirId, name: fname, fileID: cnid,
        dataStart: startAbs, dataBlocks: bcount, dataSize: (uint)fdata.Length,
        crDate: now, mdDate: now)));
      keyed.Add((cnid, "", BuildThreadRecord(
        type: RecFileThread,
        keyParentID: cnid, keyName: "",
        targetParent: parentDirId, targetName: fname)));
    }

    keyed.Sort((a, b) => CompareCatalogKey(a.Parent, a.Name, b.Parent, b.Name));
    var catRecs = keyed.Select(k => k.Record).ToList();

    // Verify our pre-computed leaf assignment still fits the actual records.
    if (leafAssignments.Sum() != catRecs.Count)
      throw new InvalidDataException("HFS: catalog leaf-plan record-count mismatch.");

    var catalogBaseOffset = allocBase + CatalogStartAbs * (int)AllocBlockSize;
    var leafCount = leafAssignments.Count;
    var firstLeafNum = 1u;
    var lastLeafNum = (uint)leafCount;
    var totalNodes = (uint)catalogActualNodeCountFromBytes(catalogBlockCount);
    // bthDepth counts every level: 1 leaf level + one per index level. The root
    // is the single node of the topmost level (the last index node we write, or
    // leaf node 1 when there is no index at all). Index nodes are numbered after
    // the leaves, bottom index level first, so the root node is the very last.
    var usedCatNodes = 1u + (uint)leafCount + (uint)indexNodeTotal;
    var freeNodes = totalNodes - usedCatNodes;
    var treeDepth = (ushort)(1 + indexLevelCounts.Count);
    var rootNodeNum = hasIndexNode ? (uint)(1 + leafCount + indexNodeTotal - 1) : 1u;

    WriteBTreeHeaderNode(image.AsSpan(catalogBaseOffset, CatalogNodeSize),
      treeDepth: treeDepth,
      rootNode: rootNodeNum,
      leafRecords: (uint)catRecs.Count,
      firstLeaf: firstLeafNum, lastLeaf: lastLeafNum,
      totalNodes: totalNodes, freeNodes: freeNodes,
      maxKeyLen: MaxCatalogKeyLen, nodeSize: CatalogNodeSize,
      allocatedNodeCount: (int)usedCatNodes);

    // Write each leaf node, threading fLink/bLink between siblings. Remember the
    // first record (its key) and node number of each leaf so the index level
    // above can point at them.
    var recIdx = 0;
    // childKeyAndNode[i] = (firstRecordOfChild, childNodeNumber) for the level
    // directly below the one we are currently building an index for.
    var childRefs = new List<(byte[] FirstRec, uint Node)>();
    for (var leafIdx = 0; leafIdx < leafCount; leafIdx++) {
      var nodeNum = (uint)(1 + leafIdx);
      var recCountInLeaf = leafAssignments[leafIdx];
      var leafRecs = catRecs.GetRange(recIdx, recCountInLeaf);
      childRefs.Add((leafRecs[0], nodeNum));
      recIdx += recCountInLeaf;
      var prev = leafIdx == 0 ? 0u : (uint)leafIdx;
      var next = leafIdx == leafCount - 1 ? 0u : (uint)(leafIdx + 2);
      WriteLeafNode(image.AsSpan(catalogBaseOffset + (int)nodeNum * CatalogNodeSize, CatalogNodeSize),
        leafRecs, prevLeaf: prev, nextLeaf: next, height: 1, nodeSize: CatalogNodeSize);
    }

    // Build each index level bottom-up. Each index node holds one record per
    // child: the child's first key (padded to keyLen=0x25 as libhfs n_index()
    // expects) plus the child node number. Index nodes within a level are
    // chained via fLink/bLink just like leaves. Node numbers continue past the
    // leaves in the same level order, so the single top-level node ends up last.
    var nextIndexNode = (uint)(1 + leafCount);
    for (var level = 0; level < indexLevelCounts.Count; level++) {
      var nodesThisLevel = indexLevelCounts[level];
      var height = (byte)(2 + level); // leaves are height 1
      // Distribute the children across this level's nodes, IndexRecordsPerNode
      // per node (the planner sized the level the same way).
      var nextLevelChildRefs = new List<(byte[] FirstRec, uint Node)>();
      var childPos = 0;
      for (var n = 0; n < nodesThisLevel; n++) {
        var nodeNum = nextIndexNode++;
        var take = Math.Min(IndexRecordsPerNode, childRefs.Count - childPos);
        var indexRecs = new List<byte[]>(take);
        for (var k = 0; k < take; k++)
          indexRecs.Add(BuildCatalogIndexRecord(childRefs[childPos + k].FirstRec, childRefs[childPos + k].Node));
        // The key that represents this index node to the level above is the
        // first key of its first child.
        nextLevelChildRefs.Add((childRefs[childPos].FirstRec, nodeNum));
        var prev = n == 0 ? 0u : nodeNum - 1;
        var next = n == nodesThisLevel - 1 ? 0u : nodeNum + 1;
        WriteIndexNode(image.AsSpan(catalogBaseOffset + (int)nodeNum * CatalogNodeSize, CatalogNodeSize),
          indexRecs, height: height, prevNode: prev, nextNode: next, nodeSize: CatalogNodeSize);
        childPos += take;
      }
      childRefs = nextLevelChildRefs;
    }

    static int catalogActualNodeCountFromBytes(int blockCount) {
      var bytes = blockCount * (int)AllocBlockSize;
      return bytes / CatalogNodeSize;
    }

    // --- 6. Master Directory Block ----------------------------------------

    // drNmFls is the count of files directly in the root directory; drDirCnt /
    // drFilCnt are the volume-wide directory / file totals (root excluded from
    // drDirCnt per Inside Macintosh). drNxtCNID is the first unused CNID, which
    // sits past every allocated folder and file CNID.
    var rootFileCount = (ushort)files.Count(f => f.ParentDirId == CnidRootDir);
    WriteMdb(image.AsSpan(MdbOffset, MdbSize),
      crDate: now, mdDate: now,
      drNmFls: rootFileCount,
      drVBMSt: drVBMSt,
      drAllocPtr: (ushort)used,
      drNmAlBlks: drNmAlBlks,
      drAlBlSt: drAlBlSt,
      drNxtCNID: tree.NextCnid,
      drFreeBks: drFreeBks,
      drFilCnt: (uint)files.Count,
      drDirCnt: (uint)folders.Count,
      extentsStartAbs: ExtentsStartAbs, extentsBlockCount: ExtentsBlockCount,
      catalogStartAbs: CatalogStartAbs, catalogBlockCount: (ushort)catalogBlockCount,
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
    byte maxKeyLen, int nodeSize, int allocatedNodeCount = 2) {
    // hfsutils libhfs/btree.c bt_readhdr() validates EXACTLY:
    //   roff[0]==0x00e, roff[1]==0x078, roff[2]==0x0f8, roff[3]==0x1f8
    // i.e. the node MUST be 512 bytes with header rec at 14, user-pad at
    // 120, bitmap at 248, free-space pointer at 504.
    if (nodeSize != BTreeNodeSize)
      throw new InvalidOperationException("HFS B*-tree node size must be 512 (hfsutils-mandated).");

    node.Clear();
    // Node descriptor.
    BinaryPrimitives.WriteUInt32BigEndian(node[0..], 0);       // ndFLink
    BinaryPrimitives.WriteUInt32BigEndian(node[4..], 0);       // ndBLink
    node[8] = unchecked((byte)KindHeader);                     // ndType = ndHdrNode (1)
    node[9] = 0;                                               // ndNHeight (header: 0)
    BinaryPrimitives.WriteUInt16BigEndian(node[10..], 3);      // ndNRecs: BTHdrRec + reserved-pad + BTMapRec
    BinaryPrimitives.WriteUInt16BigEndian(node[12..], 0);      // ndResv2

    // Record 0: BTHdrRec (106 bytes) at offset 14.
    var hdr = node[14..];
    BinaryPrimitives.WriteUInt16BigEndian(hdr[0..], treeDepth);        // bthDepth
    BinaryPrimitives.WriteUInt32BigEndian(hdr[2..], rootNode);         // bthRoot
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], leafRecords);      // bthNRecs
    BinaryPrimitives.WriteUInt32BigEndian(hdr[10..], firstLeaf);       // bthFNode
    BinaryPrimitives.WriteUInt32BigEndian(hdr[14..], lastLeaf);        // bthLNode
    BinaryPrimitives.WriteUInt16BigEndian(hdr[18..], (ushort)nodeSize); // bthNodeSize
    BinaryPrimitives.WriteUInt16BigEndian(hdr[20..], maxKeyLen);       // bthKeyLen
    BinaryPrimitives.WriteUInt32BigEndian(hdr[22..], totalNodes);      // bthNNodes
    BinaryPrimitives.WriteUInt32BigEndian(hdr[26..], freeNodes);       // bthFree
    // hdr[30..106] reserved (76 bytes of zero).

    // Record 1: 128 bytes reserved/user record — offset 120 (zeros).
    // Record 2: BTMapRec — bitmap of allocated nodes (256 bytes), MSB-first.
    const int bthRecOffset = 0x00e;            // 14
    const int reservedRecOffset = 0x078;       // 120
    const int mapRecOffset = 0x0f8;            // 248
    const int freeSpaceOffset = 0x1f8;         // 504
    var bitmap = node[mapRecOffset..(mapRecOffset + 256)];
    // Mark `allocatedNodeCount` nodes as in-use (libhfs refuses to read a
    // node whose bitmap bit is 0 — see bt_getnode "read unallocated b*-tree
    // node"). The 256-byte BTMapRec inside the header node covers nodes
    // 0..2047. Holding more nodes would require chained B*-tree map nodes,
    // which we don't emit; fail loudly rather than leave nodes unallocated.
    const int HeaderBitmapNodeCapacity = 256 * 8;
    if (allocatedNodeCount > HeaderBitmapNodeCapacity)
      throw new InvalidDataException(
        $"HFS: catalog needs {allocatedNodeCount} B*-tree nodes but the header bitmap caps it at {HeaderBitmapNodeCapacity}. Split into multiple directories.");
    for (var i = 0; i < allocatedNodeCount; i++)
      bitmap[i >> 3] |= (byte)(0x80 >> (i & 7));

    // Pointer list at end: 4 offsets (numRecords + 1 free-space pointer),
    // stored end-to-front so roff[0]..roff[3] read in order are
    // 0x00e, 0x078, 0x0f8, 0x1f8 — exactly what libhfs validates.
    BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 2)..], (ushort)bthRecOffset);
    BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 4)..], (ushort)reservedRecOffset);
    BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 6)..], (ushort)mapRecOffset);
    BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 8)..], (ushort)freeSpaceOffset);
  }

  // ------------------------------------------------------------------------
  // Directory tree
  // ------------------------------------------------------------------------

  /// <summary>A folder discovered while resolving AddFile paths.</summary>
  private sealed class FolderNode {
    public required uint DirId { get; init; }
    public required uint ParentDirId { get; init; }
    public required string Name { get; init; }
    public int Valence { get; set; }
  }

  /// <summary>The resolved folder hierarchy plus per-file parent linkage.</summary>
  private sealed class DirectoryTree {
    public required List<FolderNode> Folders { get; init; }
    public required List<(string LeafName, byte[] Data, uint ParentDirId)> Files { get; init; }
    public required int RootValence { get; init; }   // entries directly under the root dir
    public required uint FirstFileCnid { get; init; } // first CNID assigned to a file
    public required uint NextCnid { get; init; }      // first unused CNID
  }

  /// <summary>
  /// Resolves the AddFile path set into a folder hierarchy. Every intermediate
  /// path component becomes a <see cref="FolderNode"/> with its own dirID, and
  /// each file is linked to the dirID of its immediate parent folder. Folder
  /// dirIDs are allocated first (in discovery order) starting at
  /// <see cref="CnidFirstUser"/>; file CNIDs follow them.
  /// </summary>
  private static DirectoryTree BuildDirectoryTree(List<(string Name, byte[] Data)> entries) {
    var folders = new List<FolderNode>();
    // Maps a normalized directory path ("a", "a/b") to its allocated dirID.
    var dirIdByPath = new Dictionary<string, uint>(StringComparer.Ordinal);
    // Valence counter per dirID (root counted separately).
    var valence = new Dictionary<uint, int>();
    var rootValence = 0;
    var nextCnid = CnidFirstUser;

    var files = new List<(string LeafName, byte[] Data, uint ParentDirId)>();

    foreach (var (name, data) in entries) {
      var components = SplitPath(name);
      // Walk/create the folder chain for everything but the final component.
      var parentDirId = CnidRootDir;
      var pathSoFar = "";
      for (var i = 0; i < components.Length - 1; i++) {
        pathSoFar = pathSoFar.Length == 0 ? components[i] : pathSoFar + "/" + components[i];
        if (!dirIdByPath.TryGetValue(pathSoFar, out var dirId)) {
          dirId = nextCnid++;
          dirIdByPath[pathSoFar] = dirId;
          folders.Add(new FolderNode {
            DirId = dirId,
            ParentDirId = parentDirId,
            Name = components[i],
          });
          valence[dirId] = 0;
          // The new folder bumps its parent's valence.
          if (parentDirId == CnidRootDir) rootValence++;
          else valence[parentDirId] = valence[parentDirId] + 1;
        }
        parentDirId = dirId;
      }

      // The file itself bumps its immediate parent's valence.
      if (parentDirId == CnidRootDir) rootValence++;
      else valence[parentDirId] = valence[parentDirId] + 1;
      files.Add((components[^1], data, parentDirId));
    }

    // Files take CNIDs after all folders.
    var firstFileCnid = nextCnid;
    nextCnid += (uint)files.Count;

    // Push the accumulated valences back onto the folder nodes.
    foreach (var folder in folders)
      folder.Valence = valence[folder.DirId];

    return new DirectoryTree {
      Folders = folders,
      Files = files,
      RootValence = rootValence,
      FirstFileCnid = firstFileCnid,
      NextCnid = nextCnid,
    };
  }

  /// <summary>
  /// Compares two catalog keys per the HFS B*-tree ordering: parentDirID
  /// ascending, then CName by the case-insensitive MacRoman collation libhfs
  /// applies. The empty thread-record name sorts before any real name.
  /// </summary>
  private static int CompareCatalogKey(uint parentA, string nameA, uint parentB, string nameB) {
    if (parentA != parentB) return parentA.CompareTo(parentB);
    var a = Encoding.ASCII.GetBytes(nameA);
    var b = Encoding.ASCII.GetBytes(nameB);
    var min = Math.Min(a.Length, b.Length);
    for (var i = 0; i < min; i++) {
      // libhfs lowercases ASCII letters before comparing (HFS is case-insensitive
      // but case-preserving). Match that so our key order is consistent.
      var ca = ToLowerMacRoman(a[i]);
      var cb = ToLowerMacRoman(b[i]);
      if (ca != cb) return ca.CompareTo(cb);
    }
    return a.Length.CompareTo(b.Length);
  }

  private static byte ToLowerMacRoman(byte c)
    => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 0x20) : c;

  // ------------------------------------------------------------------------
  // Catalog leaf planning
  // ------------------------------------------------------------------------

  /// <summary>
  /// Plans how to distribute catalog records across one or more 512-byte leaf
  /// nodes by computing per-record sizes (which are name-dependent only) in the
  /// exact HFS key order the records will be written. Returns one count per leaf
  /// node, in key order.
  /// </summary>
  private static List<int> PlanCatalogLeaves(
    List<(string LeafName, byte[] Data, uint ParentDirId, uint Cnid)> files,
    List<FolderNode> folders, string volumeName) {
    // Per-record (parent, name, size) tuples for every catalog record, then sort
    // by the same key comparison used when the records are laid out so the
    // greedy bin-pack matches the real on-disk order exactly.
    var sized = new List<(uint Parent, string Name, int Size)>();
    sized.Add((CnidRootParent, volumeName, RecordSize(keyForCatalog(volumeName), DirRecDataLen)));
    sized.Add((CnidRootDir, "", RecordSize(keyForCatalog(""), ThdRecDataLen)));
    foreach (var folder in folders) {
      sized.Add((folder.ParentDirId, folder.Name, RecordSize(keyForCatalog(folder.Name), DirRecDataLen)));
      sized.Add((folder.DirId, "", RecordSize(keyForCatalog(""), ThdRecDataLen)));
    }
    foreach (var f in files) {
      sized.Add((f.ParentDirId, f.LeafName, RecordSize(keyForCatalog(f.LeafName), FilRecDataLen)));
      sized.Add((f.Cnid, "", RecordSize(keyForCatalog(""), ThdRecDataLen)));
    }
    sized.Sort((a, b) => CompareCatalogKey(a.Parent, a.Name, b.Parent, b.Name));
    var sizes = sized.Select(s => s.Size).ToList();

    // Per-leaf budget: nodeSize - 14 (NodeDescriptor) - 2*(N+1) (offset list).
    // Greedy bin-pack: keep filling current leaf until next record won't fit.
    var leaves = new List<int>();
    var current = 0;
    var currentBytes = 14;             // node descriptor
    var currentPtrs = 2;               // free-space pointer
    for (var i = 0; i < sizes.Count; i++) {
      var trial = currentBytes + sizes[i] + 2;     // record + 1 new offset entry
      if (current > 0 && trial + currentPtrs > BTreeNodeSize) {
        leaves.Add(current);
        current = 0;
        currentBytes = 14;
        currentPtrs = 2;
        trial = currentBytes + sizes[i] + 2;
      }
      currentBytes += sizes[i];
      currentPtrs += 2;
      current++;
    }
    if (current > 0) leaves.Add(current);
    return leaves;

    static int keyForCatalog(string name) {
      // BuildCatalogKey returns 1+keyLen bytes; aligned even for record start.
      var nameBytes = Encoding.ASCII.GetByteCount(name);
      var keyLen = 1 + 4 + 1 + nameBytes; // resrv + parentID + nameLen + name
      return AlignEven(1 + keyLen);
    }
    static int RecordSize(int alignedKeyBytes, int dataLen) => alignedKeyBytes + dataLen;
  }

  // Cached data-portion sizes (struct-fixed; see r_unpackcatdata in libhfs/record.c).
  private const int DirRecDataLen = 70;
  private const int FilRecDataLen = 102;
  private const int ThdRecDataLen = 46;

  // Each catalog index record is a key padded to keyLen=0x25 (38 bytes incl. the
  // length prefix) plus a 4-byte child node number = 42 bytes. A 512-byte node
  // reserves 14 for the descriptor and 2*(r+1) for the offset list, so the
  // record count r satisfies 14 + 42r + 2(r+1) <= 512  =>  44r <= 496  =>  r <= 11.
  private const int IndexRecordBytes = 42;
  private const int IndexRecordsPerNode = (BTreeNodeSize - 14 - 2) / (IndexRecordBytes + 2);

  /// <summary>
  /// Plans the index levels stacked above <paramref name="leafCount"/> catalog
  /// leaf nodes. Returns the node count of each index level, bottom level first
  /// and the single-node root level last; an empty list means the leaves need no
  /// index (a one-leaf tree). Each index node fans out over at most
  /// <see cref="IndexRecordsPerNode"/> children, so levels are added until one
  /// node spans the whole level below it.
  /// </summary>
  private static List<int> PlanIndexLevels(int leafCount) {
    var levels = new List<int>();
    if (leafCount <= 1) return levels;
    var children = leafCount;
    while (children > 1) {
      var nodes = (children + IndexRecordsPerNode - 1) / IndexRecordsPerNode;
      levels.Add(nodes);
      children = nodes;
    }
    return levels;
  }

  /// <summary>
  /// Builds an index-node record pointing at <paramref name="childNode"/>.
  /// libhfs <c>n_index()</c> always pads the catalog index key to keyLen=0x25
  /// (37 bytes), then appends a uint32 child node number.
  /// </summary>
  private static byte[] BuildCatalogIndexRecord(byte[] firstLeafRec, uint childNode) {
    // Extract the original key from the leaf record (Pascal-prefixed).
    // firstLeafRec[0] = keyLen byte for that record; copy keyLen+1 bytes.
    var origKeyLen = firstLeafRec[0];
    // Build padded key: 1 byte keyLen=0x25, 0x25 bytes payload zero-padded.
    var rec = new byte[1 + 0x25 + 4];   // 38 + 4 = 42
    rec[0] = 0x25;
    // Copy original payload (resrv + parentID + nameLen + name) into first
    // origKeyLen bytes after the length prefix; remainder stays zero.
    Array.Copy(firstLeafRec, 1, rec, 1, origKeyLen);
    // Child node number at offset 1 + 0x25 = 38 (key is already even-padded).
    BinaryPrimitives.WriteUInt32BigEndian(rec.AsSpan(38), childNode);
    return rec;
  }

  private static void WriteIndexNode(Span<byte> node, List<byte[]> records,
    byte height, uint prevNode, uint nextNode, int nodeSize) {
    if (nodeSize != BTreeNodeSize)
      throw new InvalidOperationException("HFS B*-tree node size must be 512.");
    node.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(node[0..], nextNode); // ndFLink (sibling in same level)
    BinaryPrimitives.WriteUInt32BigEndian(node[4..], prevNode); // ndBLink
    node[8] = unchecked((byte)KindIndex);                  // ndType = ndIndxNode (0)
    node[9] = height;                                      // ndNHeight (>=2)
    BinaryPrimitives.WriteUInt16BigEndian(node[10..], (ushort)records.Count);
    BinaryPrimitives.WriteUInt16BigEndian(node[12..], 0);  // ndResv2

    var pointerListBytes = 2 * (records.Count + 1);
    var dataArea = nodeSize - 14 - pointerListBytes;
    var total = records.Sum(r => r.Length);
    if (total > dataArea)
      throw new InvalidDataException($"HFS: index node overflow ({total} > {dataArea} bytes).");

    var pos = 14;
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      rec.CopyTo(node[pos..]);
      BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 2 * (i + 1))..], (ushort)pos);
      pos += rec.Length;
    }
    BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 2 * (records.Count + 1))..], (ushort)pos);
  }

  private static void WriteLeafNode(Span<byte> node, List<byte[]> records,
    uint prevLeaf, uint nextLeaf, byte height, int nodeSize) {
    node.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(node[0..], nextLeaf); // fLink
    BinaryPrimitives.WriteUInt32BigEndian(node[4..], prevLeaf); // bLink
    node[8] = unchecked((byte)KindLeaf);                        // kind = -1 (0xFF)
    node[9] = height;                                           // height = 1 for leaf level
    BinaryPrimitives.WriteUInt16BigEndian(node[10..], (ushort)records.Count); // numRecords
    BinaryPrimitives.WriteUInt16BigEndian(node[12..], 0);       // reserved

    var pointerListBytes = 2 * (records.Count + 1);
    var dataArea = nodeSize - 14 - pointerListBytes;
    var total = records.Sum(r => r.Length);
    if (total > dataArea)
      throw new InvalidDataException($"HFS: leaf node overflow ({total} > {dataArea} bytes). Reduce file count/name length.");

    var pos = 14;
    for (var i = 0; i < records.Count; i++) {
      var rec = records[i];
      rec.CopyTo(node[pos..]);
      BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 2 * (i + 1))..], (ushort)pos);
      pos += rec.Length;
    }
    BinaryPrimitives.WriteUInt16BigEndian(node[(nodeSize - 2 * (records.Count + 1))..], (ushort)pos);
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

#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.HfsPlus;

/// <summary>
/// In-place HFS+ modifier — performs random-access mutation of an existing
/// HFS+ image without rebuilding the whole filesystem. Designed for the
/// single-leaf catalog shape that <see cref="HfsPlusWriter"/> emits
/// (treeDepth=1, root==leaf node 1). When an Add would overflow the single
/// leaf, the modifier falls back to a writer-driven rebuild via
/// <see cref="HfsPlusWriter"/> so the operation always succeeds.
///
/// <para>What we touch on a small Add:</para>
/// <list type="bullet">
///   <item>The volume header (offset 1024) — file count, free blocks, next CNID.</item>
///   <item>The alternate volume header at <c>imageSize-1024</c> (mirror).</item>
///   <item>The allocation bitmap block (block 1).</item>
///   <item>The catalog leaf node (block 3 → offset 3×blockSize for the standard layout).</item>
///   <item>The newly-allocated data blocks for the file payload.</item>
/// </list>
/// </summary>
public static class HfsPlusModifier {
  private const int VolumeHeaderOffset = 1024;
  private const ushort HfsPlusSignature = 0x482B; // "H+"
  private const ushort HfsxSignature = 0x4858;    // "HX"
  private const uint RootFolderCnid = 2;
  // Catalog file record/struct sizes (TN1150 §6).
  private const int CatalogFileRecordSize = 248;
  private const int CatalogForkDataSize = 80;
  private const int DataForkOffset = 88;
  private const int ResourceForkOffset = 168;
  // HFS+ epoch: 1904-01-01T00:00:00Z.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// Adds (or replaces by name) a file. If the catalog leaf cannot fit the new
  /// record, falls back to a full rebuild so the call always succeeds.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    // Replace-by-name: if it already exists, remove first so we don't end up
    // with two records sharing the same key.
    RemoveFile(image, name, wipeData: true);

    if (!TryAddInPlace(image, name, data))
      RebuildAdd(image, name, data);
  }

  /// <summary>
  /// Removes the named file. Returns true if it was present and removed,
  /// false if no such entry exists.
  /// </summary>
  public static bool RemoveFile(Stream image, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);

    var img = ReadAll(image);
    var ctx = ParseVolume(img);
    if (ctx is null) return false;

    // Catalog node 0 is the header; node 1 is the leaf in the writer's layout.
    var catalogBase = (int)(ctx.CatalogStartBlock * ctx.BlockSize);
    var leafBase = catalogBase + (int)ctx.FirstLeafNode * ctx.NodeSize;
    var leaf = img.AsSpan(leafBase, ctx.NodeSize);

    // Locate the file record (recordType 2, key parent==RootFolderCnid, name match).
    if (!TryFindFileRecord(leaf, ctx.NodeSize, name, out var fileRecIdx, out var fileCnid,
        out var startBlock, out var blockCount))
      return false;

    // Locate the matching file thread record (key parent==fileCnid, empty name).
    var threadRecIdx = FindThreadRecord(leaf, ctx.NodeSize, fileCnid);

    // Wipe data blocks.
    if (wipeData && blockCount > 0) {
      var dataOffset = (long)startBlock * ctx.BlockSize;
      var byteLen = (long)blockCount * ctx.BlockSize;
      if (dataOffset + byteLen <= img.Length)
        img.AsSpan((int)dataOffset, (int)byteLen).Clear();
    }

    // Free bitmap bits.
    var bitmapBase = ctx.BlockSize; // allocation bitmap lives at block 1
    for (uint b = startBlock; b < startBlock + blockCount; b++)
      ClearBitmapBit(img, (int)bitmapBase, b);

    // Remove records from the leaf (highest index first to avoid shifting issues).
    var indices = threadRecIdx >= 0
      ? new[] { Math.Max(fileRecIdx, threadRecIdx), Math.Min(fileRecIdx, threadRecIdx) }
      : new[] { fileRecIdx };
    foreach (var idx in indices)
      RemoveLeafRecord(leaf, ctx.NodeSize, idx);

    // Update VH counters.
    var vh = img.AsSpan(VolumeHeaderOffset);
    var fileCount = BinaryPrimitives.ReadUInt32BigEndian(vh[32..]);
    if (fileCount > 0) BinaryPrimitives.WriteUInt32BigEndian(vh[32..], fileCount - 1);
    var freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[48..]);
    BinaryPrimitives.WriteUInt32BigEndian(vh[48..], freeBlocks + blockCount);
    BinaryPrimitives.WriteUInt32BigEndian(vh[20..], HfsTimestamp(DateTime.UtcNow));

    // Decrement leafRecords in catalog header.
    DecrementLeafRecords(img, ctx, indices.Length);

    // The removed file was a direct child of the root folder, so drop the root
    // folder record's valence by 1 to keep fsck_hfs's directory-item-count
    // check satisfied.
    AdjustRootFolderValence(img, ctx, -1);

    // Mirror VH to alternate.
    MirrorAlternateVh(img);

    WriteAll(image, img);
    return true;
  }

  // ── In-place add ─────────────────────────────────────────────────────────

  private static bool TryAddInPlace(Stream image, string name, byte[] data) {
    var img = ReadAll(image);
    var ctx = ParseVolume(img);
    if (ctx is null) return false;
    // Only handle the simple shape the writer emits: treeDepth=1, single leaf
    // node 1 covering all records.
    if (ctx.TreeDepth != 1 || ctx.RootNode != 1 || ctx.FirstLeafNode != 1 || ctx.LastLeafNode != 1)
      return false;

    // Catalog node 0 is the header; node 1 is the leaf in the writer's layout.
    var catalogBase = (int)(ctx.CatalogStartBlock * ctx.BlockSize);
    var leafBase = catalogBase + (int)ctx.FirstLeafNode * ctx.NodeSize;
    var leaf = img.AsSpan(leafBase, ctx.NodeSize);

    // Allocate data blocks via bitmap walk.
    var blocksNeeded = (uint)((data.Length + ctx.BlockSize - 1) / ctx.BlockSize);
    var bitmapBase = (int)ctx.BlockSize;
    var allocated = AllocateContiguous(img, bitmapBase, ctx.TotalBlocks, blocksNeeded);
    if (allocated == 0 && blocksNeeded > 0) return false;

    // Pick a fresh CNID.
    var vh = img.AsSpan(VolumeHeaderOffset);
    var nextCnid = BinaryPrimitives.ReadUInt32BigEndian(vh[64..]);
    if (nextCnid < 16) nextCnid = 16;
    var fileCnid = nextCnid;

    // Build the two records.
    var fileRec = BuildFileRecord(fileCnid, RootFolderCnid, name, data.Length, allocated, blocksNeeded, ctx.BlockSize);
    var threadRec = BuildFileThreadRecord(fileCnid, RootFolderCnid, name);

    // Try insertion. If overflow, undo bitmap allocation and signal fallback.
    if (!TryInsertLeafRecord(leaf, ctx.NodeSize, fileRec, RootFolderCnid, name) ||
        !TryInsertLeafRecord(leaf, ctx.NodeSize, threadRec, fileCnid, "")) {
      for (uint b = allocated; b < allocated + blocksNeeded; b++)
        ClearBitmapBit(img, bitmapBase, b);
      return false;
    }

    // Write file payload to allocated blocks.
    if (data.Length > 0) {
      var off = (long)allocated * ctx.BlockSize;
      if (off + data.Length > img.Length) return false;
      data.CopyTo(img, (int)off);
    }

    // Update VH: fileCount++, nextCatalogID++, freeBlocks-=blocksNeeded, modifyDate.
    var fileCount = BinaryPrimitives.ReadUInt32BigEndian(vh[32..]);
    BinaryPrimitives.WriteUInt32BigEndian(vh[32..], fileCount + 1);
    BinaryPrimitives.WriteUInt32BigEndian(vh[64..], fileCnid + 1);
    var freeBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[48..]);
    BinaryPrimitives.WriteUInt32BigEndian(vh[48..], freeBlocks - blocksNeeded);
    BinaryPrimitives.WriteUInt32BigEndian(vh[20..], HfsTimestamp(DateTime.UtcNow));
    BinaryPrimitives.WriteUInt32BigEndian(vh[52..], allocated + blocksNeeded); // nextAllocation hint

    // Bump leafRecords by 2 in catalog header.
    IncrementLeafRecords(img, ctx, 2);

    // The new file lives directly under the root folder, so the root folder
    // record's valence (its child count) must rise by 1; fsck_hfs reports
    // "Invalid directory item count" otherwise. The root folder record is
    // keyed (parent=1, name=volume), the only recordType-1 record under
    // parent CNID 1.
    AdjustRootFolderValence(img, ctx, +1);

    // Mirror to alt VH.
    MirrorAlternateVh(img);
    WriteAll(image, img);
    return true;
  }

  private static void RebuildAdd(Stream image, string name, byte[] data) {
    image.Position = 0;
    using var r = new HfsPlusReader(image, leaveOpen: true);
    var existing = new List<(string Name, byte[] Data)>();
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
      existing.Add((e.Name, r.Extract(e)));
    }
    existing.Add((name, data));
    var w = new HfsPlusWriter();
    foreach (var (n, d) in existing) w.AddFile(n, d);
    var rebuilt = w.Build();
    image.Position = 0;
    image.Write(rebuilt);
    image.SetLength(rebuilt.Length);
  }

  // ── Volume context ───────────────────────────────────────────────────────

  private sealed class VolumeContext {
    public uint BlockSize;
    public uint TotalBlocks;
    public uint CatalogStartBlock;
    public uint CatalogBlockCount;
    public ushort NodeSize;
    public ushort TreeDepth;
    public uint RootNode;
    public uint FirstLeafNode;
    public uint LastLeafNode;
  }

  private static VolumeContext? ParseVolume(byte[] img) {
    if (img.Length < VolumeHeaderOffset + 512) return null;
    var vh = img.AsSpan(VolumeHeaderOffset);
    var sig = BinaryPrimitives.ReadUInt16BigEndian(vh);
    if (sig != HfsPlusSignature && sig != HfsxSignature) return null;

    var blockSize = BinaryPrimitives.ReadUInt32BigEndian(vh[40..]);
    var totalBlocks = BinaryPrimitives.ReadUInt32BigEndian(vh[44..]);
    if (blockSize == 0) return null;
    var catalogStart = BinaryPrimitives.ReadUInt32BigEndian(vh[288..]);
    var catalogCount = BinaryPrimitives.ReadUInt32BigEndian(vh[292..]);
    if (catalogStart == 0 || catalogCount == 0) return null;

    var catalogBase = (long)catalogStart * blockSize;
    if (catalogBase + 14 > img.Length) return null;
    var hdrNode = img.AsSpan((int)catalogBase);
    if ((sbyte)hdrNode[8] != 1) return null;       // not a header node
    var hdr = hdrNode[14..];
    var treeDepth = BinaryPrimitives.ReadUInt16BigEndian(hdr);
    var rootNode = BinaryPrimitives.ReadUInt32BigEndian(hdr[2..]);
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(hdr[10..]);
    var lastLeaf = BinaryPrimitives.ReadUInt32BigEndian(hdr[14..]);
    var nodeSize = BinaryPrimitives.ReadUInt16BigEndian(hdr[18..]);
    if (nodeSize == 0) return null;

    return new VolumeContext {
      BlockSize = blockSize,
      TotalBlocks = totalBlocks,
      CatalogStartBlock = catalogStart,
      CatalogBlockCount = catalogCount,
      NodeSize = nodeSize,
      TreeDepth = treeDepth,
      RootNode = rootNode,
      FirstLeafNode = firstLeaf,
      LastLeafNode = lastLeaf,
    };
  }

  // ── Leaf record helpers ─────────────────────────────────────────────────

  /// <summary>
  /// Locates a file record (recordType=2) whose catalog key has parent CNID
  /// equal to the root folder and whose name matches <paramref name="name"/>.
  /// </summary>
  private static bool TryFindFileRecord(ReadOnlySpan<byte> leaf, int nodeSize, string name,
      out int recordIndex, out uint fileCnid, out uint startBlock, out uint blockCount) {
    recordIndex = -1; fileCnid = 0; startBlock = 0; blockCount = 0;
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    var nameBytes = Encoding.BigEndianUnicode.GetBytes(name);

    for (var i = 0; i < numRecords; i++) {
      var recOff = ReadOffset(leaf, nodeSize, i);
      if (recOff < 14 || recOff + 8 > nodeSize) continue;
      var keyLen = BinaryPrimitives.ReadUInt16BigEndian(leaf[recOff..]);
      var parent = BinaryPrimitives.ReadUInt32BigEndian(leaf[(recOff + 2)..]);
      if (parent != RootFolderCnid) continue;
      var nameLen = BinaryPrimitives.ReadUInt16BigEndian(leaf[(recOff + 6)..]);
      var nameByteLen = nameLen * 2;
      if (recOff + 8 + nameByteLen > nodeSize) continue;
      if (nameByteLen != nameBytes.Length) continue;
      var equal = true;
      for (var b = 0; b < nameByteLen; b++) {
        if (leaf[recOff + 8 + b] != nameBytes[b]) { equal = false; break; }
      }
      if (!equal) continue;

      var dataOff = recOff + 2 + keyLen;
      if ((dataOff & 1) != 0) dataOff++;
      if (dataOff + 248 > nodeSize) continue;
      var recType = BinaryPrimitives.ReadInt16BigEndian(leaf[dataOff..]);
      if (recType != 2) continue;

      fileCnid = BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataOff + 8)..]);
      startBlock = BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataOff + DataForkOffset + 16)..]);
      blockCount = BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataOff + DataForkOffset + 20)..]);
      recordIndex = i;
      return true;
    }
    return false;
  }

  /// <summary>
  /// Finds the file thread record whose key is (parent=fileCnid, name="").
  /// </summary>
  private static int FindThreadRecord(ReadOnlySpan<byte> leaf, int nodeSize, uint fileCnid) {
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    for (var i = 0; i < numRecords; i++) {
      var recOff = ReadOffset(leaf, nodeSize, i);
      if (recOff < 14 || recOff + 8 > nodeSize) continue;
      var keyLen = BinaryPrimitives.ReadUInt16BigEndian(leaf[recOff..]);
      var parent = BinaryPrimitives.ReadUInt32BigEndian(leaf[(recOff + 2)..]);
      var nameLen = BinaryPrimitives.ReadUInt16BigEndian(leaf[(recOff + 6)..]);
      if (parent != fileCnid || nameLen != 0) continue;
      var dataOff = recOff + 2 + keyLen;
      if ((dataOff & 1) != 0) dataOff++;
      if (dataOff + 2 > nodeSize) continue;
      var recType = BinaryPrimitives.ReadInt16BigEndian(leaf[dataOff..]);
      if (recType == 4) return i;
    }
    return -1;
  }

  private static int ReadOffset(ReadOnlySpan<byte> leaf, int nodeSize, int recordIndex) {
    var offsetPos = nodeSize - 2 * (recordIndex + 1);
    return BinaryPrimitives.ReadUInt16BigEndian(leaf[offsetPos..]);
  }

  /// <summary>
  /// Inserts <paramref name="record"/> into the leaf, keeping records sorted by
  /// (parent CNID asc, name UTF-16BE binary asc). Returns false when the record
  /// + its new offset slot won't fit in the available free space.
  /// </summary>
  private static bool TryInsertLeafRecord(Span<byte> leaf, int nodeSize,
      byte[] record, uint keyParent, string keyName) {
    int numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    var newKeyName = Encoding.BigEndianUnicode.GetBytes(keyName);

    // Snapshot current offset table BEFORE any mutation.
    var oldOffsets = new ushort[numRecords + 1];
    for (var i = 0; i < numRecords + 1; i++)
      oldOffsets[i] = BinaryPrimitives.ReadUInt16BigEndian(leaf[(nodeSize - 2 * (i + 1))..]);
    var freeOff = oldOffsets[numRecords];

    // Determine insertion index by walking records in key order.
    var insertIndex = numRecords;
    for (var i = 0; i < numRecords; i++) {
      var off = oldOffsets[i];
      var p = BinaryPrimitives.ReadUInt32BigEndian(leaf[(off + 2)..]);
      var nl = BinaryPrimitives.ReadUInt16BigEndian(leaf[(off + 6)..]);
      var cmp = CompareKey(p, leaf.Slice(off + 8, nl * 2), keyParent, newKeyName);
      if (cmp >= 0) { insertIndex = i; break; }
    }

    var newRecLen = record.Length;
    if ((newRecLen & 1) != 0) newRecLen++; // pad to even

    // Capacity check: free space must hold the new record + a new offset slot.
    var existingTableBytes = 2 * (numRecords + 1);
    var availableBefore = nodeSize - freeOff - existingTableBytes;
    if (availableBefore < newRecLen + 2) return false;

    var insertPos = insertIndex < numRecords ? oldOffsets[insertIndex] : freeOff;

    // Shift payload [insertPos..freeOff) forward by newRecLen via temp buffer.
    var bytesToShift = freeOff - insertPos;
    if (bytesToShift > 0) {
      Span<byte> temp = bytesToShift <= 256 ? stackalloc byte[bytesToShift] : new byte[bytesToShift];
      leaf.Slice(insertPos, bytesToShift).CopyTo(temp);
      temp.CopyTo(leaf.Slice(insertPos + newRecLen, bytesToShift));
    }
    record.AsSpan().CopyTo(leaf.Slice(insertPos, record.Length));
    if (newRecLen > record.Length) leaf[insertPos + record.Length] = 0;

    // Build new offsets (numRecords+2 entries).
    var newOffsets = new ushort[numRecords + 2];
    for (var i = 0; i < insertIndex; i++) newOffsets[i] = oldOffsets[i];
    newOffsets[insertIndex] = (ushort)insertPos;
    for (var i = insertIndex; i < numRecords; i++) newOffsets[i + 1] = (ushort)(oldOffsets[i] + newRecLen);
    newOffsets[numRecords + 1] = (ushort)(oldOffsets[numRecords] + newRecLen);

    // Clear the old offset table region first (it's now one entry shorter on the
    // end side). Then write the new (longer) table.
    for (var i = 0; i < oldOffsets.Length; i++) {
      var pos = nodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], 0);
    }
    for (var i = 0; i < newOffsets.Length; i++) {
      var pos = nodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], newOffsets[i]);
    }

    BinaryPrimitives.WriteUInt16BigEndian(leaf[10..], (ushort)(numRecords + 1));
    return true;
  }

  private static void RemoveLeafRecord(Span<byte> leaf, int nodeSize, int recordIndex) {
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    if (recordIndex < 0 || recordIndex >= numRecords) return;

    // Read offset table.
    var offsets = new ushort[numRecords + 1];
    for (var i = 0; i < numRecords + 1; i++)
      offsets[i] = BinaryPrimitives.ReadUInt16BigEndian(leaf[(nodeSize - 2 * (i + 1))..]);

    var recStart = offsets[recordIndex];
    var recEnd = offsets[recordIndex + 1];
    var recLen = recEnd - recStart;
    var bytesAfter = offsets[numRecords] - recEnd;

    // Shift payload [recEnd..freeOff) backward by recLen.
    if (bytesAfter > 0)
      leaf.Slice(recEnd, bytesAfter).CopyTo(leaf.Slice(recStart, bytesAfter));
    // Zero the freed tail bytes.
    leaf.Slice(offsets[numRecords] - recLen, recLen).Clear();

    // Build new offsets array of size numRecords (one fewer record).
    var newOffsets = new ushort[numRecords];
    for (var i = 0; i < recordIndex; i++) newOffsets[i] = offsets[i];
    for (var i = recordIndex + 1; i <= numRecords; i++)
      newOffsets[i - 1] = (ushort)(offsets[i] - recLen);

    // Clear old offset table region (the topmost entry slot frees up by 2 bytes).
    // First zero all old entries, then write new.
    for (var i = 0; i < numRecords + 1; i++) {
      var pos = nodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], 0);
    }
    for (var i = 0; i < newOffsets.Length; i++) {
      var pos = nodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], newOffsets[i]);
    }
    BinaryPrimitives.WriteUInt16BigEndian(leaf[10..], (ushort)(numRecords - 1));
  }

  // ── Catalog header maintenance ──────────────────────────────────────────

  private static void IncrementLeafRecords(byte[] img, VolumeContext ctx, int delta) {
    var hdrBase = (int)(ctx.CatalogStartBlock * ctx.BlockSize);
    var hdr = img.AsSpan(hdrBase + 14);
    var n = BinaryPrimitives.ReadUInt32BigEndian(hdr[6..]);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], n + (uint)delta);
  }

  private static void DecrementLeafRecords(byte[] img, VolumeContext ctx, int delta) {
    var hdrBase = (int)(ctx.CatalogStartBlock * ctx.BlockSize);
    var hdr = img.AsSpan(hdrBase + 14);
    var n = BinaryPrimitives.ReadUInt32BigEndian(hdr[6..]);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], n >= delta ? n - (uint)delta : 0u);
  }

  /// <summary>
  /// Adjusts the root folder record's valence (its direct-child count) by
  /// <paramref name="delta"/>. The root folder record is the single
  /// recordType-1 (kHFSPlusFolderRecord) record whose catalog key has parent
  /// CNID 1; its valence field sits at body offset 4 (TN1150
  /// HFSPlusCatalogFolder). Re-scans the leaf so it is robust to record shifts
  /// from a preceding insert/remove.
  /// </summary>
  private static void AdjustRootFolderValence(byte[] img, VolumeContext ctx, int delta) {
    var catalogBase = (int)(ctx.CatalogStartBlock * ctx.BlockSize);
    var leafBase = catalogBase + (int)ctx.FirstLeafNode * ctx.NodeSize;
    var leaf = img.AsSpan(leafBase, ctx.NodeSize);
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    for (var i = 0; i < numRecords; i++) {
      var recOff = ReadOffset(leaf, ctx.NodeSize, i);
      if (recOff < 14 || recOff + 8 > ctx.NodeSize) continue;
      var keyLen = BinaryPrimitives.ReadUInt16BigEndian(leaf[recOff..]);
      var parent = BinaryPrimitives.ReadUInt32BigEndian(leaf[(recOff + 2)..]);
      if (parent != 1) continue; // root folder is keyed under CnidRootParent (1)
      var dataOff = recOff + 2 + keyLen;
      if ((dataOff & 1) != 0) dataOff++;
      if (dataOff + 8 > ctx.NodeSize) continue;
      var recType = BinaryPrimitives.ReadInt16BigEndian(leaf[dataOff..]);
      if (recType != 1) continue; // kHFSPlusFolderRecord
      var valence = BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataOff + 4)..]);
      var adjusted = delta >= 0 ? valence + (uint)delta
                                : (valence >= (uint)(-delta) ? valence - (uint)(-delta) : 0u);
      BinaryPrimitives.WriteUInt32BigEndian(leaf[(dataOff + 4)..], adjusted);
      return;
    }
  }

  // ── Bitmap helpers ──────────────────────────────────────────────────────

  /// <summary>
  /// Allocates <paramref name="count"/> contiguous free blocks via a linear
  /// bitmap walk. Returns the start block, or 0 if nothing fits (or if 0
  /// blocks were requested — caller treats 0+0 specially).
  /// </summary>
  private static uint AllocateContiguous(byte[] img, int bitmapBase, uint totalBlocks, uint count) {
    if (count == 0) return 0;
    var run = 0u;
    var runStart = 0u;
    for (uint b = 0; b < totalBlocks; b++) {
      if (BitmapBitIsSet(img, bitmapBase, b)) { run = 0; continue; }
      if (run == 0) runStart = b;
      run++;
      if (run >= count) {
        for (uint i = 0; i < count; i++) SetBitmapBit(img, bitmapBase, runStart + i);
        return runStart;
      }
    }
    return 0;
  }

  private static bool BitmapBitIsSet(byte[] img, int bitmapBase, uint block) {
    var byteIdx = bitmapBase + (int)(block / 8);
    var bitIdx = (int)(7 - (block % 8));
    if (byteIdx >= img.Length) return true;
    return (img[byteIdx] & (1 << bitIdx)) != 0;
  }

  private static void SetBitmapBit(byte[] img, int bitmapBase, uint block) {
    var byteIdx = bitmapBase + (int)(block / 8);
    var bitIdx = (int)(7 - (block % 8));
    if (byteIdx >= img.Length) return;
    img[byteIdx] |= (byte)(1 << bitIdx);
  }

  private static void ClearBitmapBit(byte[] img, int bitmapBase, uint block) {
    var byteIdx = bitmapBase + (int)(block / 8);
    var bitIdx = (int)(7 - (block % 8));
    if (byteIdx >= img.Length) return;
    img[byteIdx] &= (byte)~(1 << bitIdx);
  }

  // ── Record builders (mirror HfsPlusWriter) ──────────────────────────────

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

  private static byte[] BuildFileRecord(uint fileCnid, uint parentCnid, string name,
      long logicalSize, uint startBlock, uint blockCount, uint blockSize) {
    var key = BuildCatalogKey(parentCnid, name);
    var recData = new byte[CatalogFileRecordSize];
    BinaryPrimitives.WriteInt16BigEndian(recData, 2);                 // recordType = kHFSPlusFileRecord
    BinaryPrimitives.WriteUInt16BigEndian(recData.AsSpan(2), 0x0002); // flags = kHFSThreadExistsMask
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(8), fileCnid);
    var now = HfsTimestamp(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(12), now);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(16), now);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(20), now);
    BinaryPrimitives.WriteUInt32BigEndian(recData.AsSpan(24), now);
    WriteForkData(recData.AsSpan(DataForkOffset, CatalogForkDataSize), logicalSize, blockSize, startBlock, blockCount);
    WriteForkData(recData.AsSpan(ResourceForkOffset, CatalogForkDataSize), 0, blockSize, 0, 0);

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

  private static void WriteForkData(Span<byte> dst, long logicalSize, uint clumpSize, uint startBlock, uint blockCount) {
    BinaryPrimitives.WriteUInt64BigEndian(dst, (ulong)logicalSize);
    BinaryPrimitives.WriteUInt32BigEndian(dst[8..], clumpSize);
    BinaryPrimitives.WriteUInt32BigEndian(dst[12..], blockCount);
    BinaryPrimitives.WriteUInt32BigEndian(dst[16..], startBlock);
    BinaryPrimitives.WriteUInt32BigEndian(dst[20..], blockCount);
  }

  // ── Misc helpers ────────────────────────────────────────────────────────

  /// <summary>
  /// Compares two HFS+ catalog keys: first by parent CNID, then by UTF-16BE
  /// binary name comparison.
  /// </summary>
  private static int CompareKey(uint pa, ReadOnlySpan<byte> na, uint pb, ReadOnlySpan<byte> nb) {
    if (pa != pb) return pa.CompareTo(pb);
    var min = Math.Min(na.Length, nb.Length);
    for (var i = 0; i < min; i++) {
      if (na[i] != nb[i]) return na[i].CompareTo(nb[i]);
    }
    return na.Length.CompareTo(nb.Length);
  }

  private static void MirrorAlternateVh(byte[] img) {
    if (img.Length < 1024) return;
    var altOff = img.Length - 1024;
    img.AsSpan(VolumeHeaderOffset, 512).CopyTo(img.AsSpan(altOff, 512));
  }

  private static byte[] ReadAll(Stream image) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    return ms.ToArray();
  }

  private static void WriteAll(Stream image, byte[] data) {
    image.Position = 0;
    image.Write(data);
    image.SetLength(data.Length);
  }

  private static uint HfsTimestamp(DateTime dt) {
    if (dt < HfsEpoch) return 0;
    var seconds = (dt - HfsEpoch).TotalSeconds;
    return seconds > uint.MaxValue ? uint.MaxValue : (uint)seconds;
  }
}

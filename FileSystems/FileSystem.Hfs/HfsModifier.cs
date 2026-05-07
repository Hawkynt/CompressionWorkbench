#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hfs;

/// <summary>
/// In-place HFS classic modifier — performs random-access mutation of an
/// existing HFS image without rebuilding the entire filesystem. Tuned for the
/// single-leaf catalog shape that <see cref="HfsWriter"/> emits for the common
/// case (≤ ~30 files per image, no index node required). When an Add would
/// overflow the single 512-byte leaf, falls back to a writer-driven rebuild
/// transparently so the call always succeeds.
///
/// <para>What we touch on a small Add (single-leaf path):</para>
/// <list type="bullet">
///   <item>The MDB at offset 1024 — drNmFls, drFilCnt, drFreeBks, drNxtCNID, drAllocPtr, drLsMod.</item>
///   <item>The alternate MDB at <c>(totalSectors - 2) × 512</c> (mirror).</item>
///   <item>The volume bitmap starting at <c>drVBMSt × 512</c>.</item>
///   <item>The catalog leaf node (allocation block 2 in the writer's layout).</item>
///   <item>The newly-allocated allocation blocks for the file payload.</item>
/// </list>
/// </summary>
public static class HfsModifier {
  private const int MdbOffset = 1024;
  private const int MdbSize = 512;
  private const ushort HfsMagic = 0x4244;
  private const int BTreeNodeSize = 512;
  // Reserved CNIDs.
  private const uint CnidRootParent = 1;
  private const uint CnidRootDir = 2;
  private const uint CnidFirstUser = 16;
  // Catalog record types.
  private const byte RecFolder = 1;
  private const byte RecFile = 2;
  private const byte RecFolderThread = 3;
  private const byte RecFileThread = 4;
  // HFS data sizes per Inside Macintosh.
  private const int FilRecDataLen = 102;
  private const int ThdRecDataLen = 46;

  // HFS epoch.
  private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// Adds (or replaces by name) a file. If the catalog leaf cannot fit the new
  /// records, falls back to a writer-driven rebuild so the call always succeeds.
  /// </summary>
  public static void AddFile(Stream image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    if (name.Length is 0 or > 31)
      throw new ArgumentOutOfRangeException(nameof(name), "HFS file name must be 1–31 chars.");

    // Replace-by-name semantics: drop any existing entry with this name first.
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
    if (!ctx.IsSingleLeaf) return false;  // we only mutate the simple shape in-place

    var leafBase = ctx.AllocBase + ctx.CatalogStartAbs * (int)ctx.AllocBlockSize + BTreeNodeSize;
    var leaf = img.AsSpan(leafBase, BTreeNodeSize);

    if (!TryFindFileRecord(leaf, name, out var fileRecIdx, out var fileCnid,
        out var startBlock, out var blockCount))
      return false;
    var threadRecIdx = FindFileThreadRecord(leaf, fileCnid);

    // Wipe + free data blocks.
    if (blockCount > 0) {
      var dataOffset = ctx.AllocBase + (long)startBlock * ctx.AllocBlockSize;
      var byteLen = (long)blockCount * ctx.AllocBlockSize;
      if (wipeData && dataOffset + byteLen <= img.Length)
        img.AsSpan((int)dataOffset, (int)byteLen).Clear();
      for (uint b = startBlock; b < startBlock + blockCount; b++)
        ClearBitmapBit(img, ctx.BitmapBase, b);
    }

    // Remove records from leaf — highest index first.
    var indices = threadRecIdx >= 0
      ? new[] { Math.Max(fileRecIdx, threadRecIdx), Math.Min(fileRecIdx, threadRecIdx) }
      : new[] { fileRecIdx };
    foreach (var idx in indices) RemoveLeafRecord(leaf, idx);

    // Decrement catalog leafRecords field.
    var hdr = img.AsSpan(leafBase - BTreeNodeSize + 14);
    var leafRecs = BinaryPrimitives.ReadUInt32BigEndian(hdr[6..]);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], leafRecs >= indices.Length ? leafRecs - (uint)indices.Length : 0);

    // Decrement root-dir valence (count of entries in root). The root-dir record
    // is the first record under parent==CnidRootParent.
    AdjustRootValence(leaf, -1);

    // Update MDB counters.
    var mdb = img.AsSpan(MdbOffset);
    UpdateUInt16(mdb, 12, -1);  // drNmFls
    UpdateUInt32(mdb, 84, -1);  // drFilCnt
    UpdateUInt16Add(mdb, 34, blockCount); // drFreeBks
    BinaryPrimitives.WriteUInt32BigEndian(mdb[6..], (uint)ToHfsTime(DateTime.UtcNow));

    MirrorAlternateMdb(img, ctx);
    WriteAll(image, img);
    return true;
  }

  // ── In-place add ────────────────────────────────────────────────────────

  private static bool TryAddInPlace(Stream image, string name, byte[] data) {
    var img = ReadAll(image);
    var ctx = ParseVolume(img);
    if (ctx is null) return false;
    if (!ctx.IsSingleLeaf) return false;

    var leafBase = ctx.AllocBase + ctx.CatalogStartAbs * (int)ctx.AllocBlockSize + BTreeNodeSize;
    var leaf = img.AsSpan(leafBase, BTreeNodeSize);

    // Allocate file data: contiguous run starting after the catalog blocks.
    var bs = ctx.AllocBlockSize;
    var blocksNeeded = (uint)((data.Length + bs - 1) / bs);
    var allocated = AllocateContiguous(img, ctx.BitmapBase, ctx.NumAllocBlocks, blocksNeeded);
    if (allocated == 0 && blocksNeeded > 0) return false;
    if (allocated > ushort.MaxValue || blocksNeeded > ushort.MaxValue) {
      for (uint b = allocated; b < allocated + blocksNeeded; b++) ClearBitmapBit(img, ctx.BitmapBase, b);
      return false;
    }

    // Pick a fresh CNID from MDB drNxtCNID.
    var mdb = img.AsSpan(MdbOffset);
    var nxtCnid = BinaryPrimitives.ReadUInt32BigEndian(mdb[30..]);
    if (nxtCnid < CnidFirstUser) nxtCnid = CnidFirstUser;
    var fileCnid = nxtCnid;

    var fileRec = BuildFileRecord(parentID: CnidRootDir, name: name, fileID: fileCnid,
        dataStart: (ushort)allocated, dataBlocks: (ushort)blocksNeeded,
        dataSize: (uint)data.Length, blockSize: bs);
    var threadRec = BuildThreadRecord(type: RecFileThread,
        keyParentID: fileCnid, keyName: "",
        targetParent: CnidRootDir, targetName: name);

    if (!TryInsertLeafRecord(leaf, fileRec, CnidRootDir, name) ||
        !TryInsertLeafRecord(leaf, threadRec, fileCnid, "")) {
      for (uint b = allocated; b < allocated + blocksNeeded; b++) ClearBitmapBit(img, ctx.BitmapBase, b);
      return false;
    }

    // Write file payload to allocated blocks.
    if (data.Length > 0) {
      var dst = ctx.AllocBase + (long)allocated * bs;
      if (dst + data.Length > img.Length) {
        // Best-effort rollback: bitmap (records already inserted, but adding fails).
        for (uint b = allocated; b < allocated + blocksNeeded; b++) ClearBitmapBit(img, ctx.BitmapBase, b);
        return false;
      }
      data.CopyTo(img, (int)dst);
    }

    // Bump catalog leafRecords by 2.
    var hdr = img.AsSpan(leafBase - BTreeNodeSize + 14);
    var leafRecs = BinaryPrimitives.ReadUInt32BigEndian(hdr[6..]);
    BinaryPrimitives.WriteUInt32BigEndian(hdr[6..], leafRecs + 2);

    // Bump root-dir valence (file count under root).
    AdjustRootValence(leaf, +1);

    // Update MDB counters.
    UpdateUInt16(mdb, 12, +1);                    // drNmFls
    UpdateUInt32(mdb, 84, +1);                    // drFilCnt
    UpdateUInt16Sub(mdb, 34, blocksNeeded);       // drFreeBks
    BinaryPrimitives.WriteUInt32BigEndian(mdb[30..], fileCnid + 1); // drNxtCNID
    // drAllocPtr (16) — hint pointer; bump past the block we just used.
    BinaryPrimitives.WriteUInt16BigEndian(mdb[16..], (ushort)Math.Min(allocated + blocksNeeded, ctx.NumAllocBlocks));
    BinaryPrimitives.WriteUInt32BigEndian(mdb[6..], (uint)ToHfsTime(DateTime.UtcNow));

    MirrorAlternateMdb(img, ctx);
    WriteAll(image, img);
    return true;
  }

  private static void RebuildAdd(Stream image, string name, byte[] data) {
    image.Position = 0;
    var r = new HfsReader(image);
    var existing = new List<(string Name, byte[] Data)>();
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
      existing.Add((e.Name, r.Extract(e)));
    }
    existing.Add((name, data));
    var w = new HfsWriter();
    foreach (var (n, d) in existing) w.AddFile(n, d);
    var rebuilt = w.Build();
    image.Position = 0;
    image.Write(rebuilt);
    image.SetLength(rebuilt.Length);
  }

  // ── Volume context ──────────────────────────────────────────────────────

  private sealed class VolumeContext {
    public int AllocBase;          // byte offset of allocation block 0 (drAlBlSt × 512)
    public uint AllocBlockSize;
    public ushort NumAllocBlocks;
    public ushort CatalogStartAbs;
    public ushort CatalogBlockCount;
    public int BitmapBase;          // drVBMSt × 512
    public int TotalSectors;
    public bool IsSingleLeaf;       // catalog is a single leaf node (no index)
  }

  private static VolumeContext? ParseVolume(byte[] img) {
    if (img.Length < MdbOffset + 162) return null;
    var mdb = img.AsSpan(MdbOffset);
    var sig = BinaryPrimitives.ReadUInt16BigEndian(mdb);
    if (sig != HfsMagic) return null;
    var drVBMSt = BinaryPrimitives.ReadUInt16BigEndian(mdb[14..]);
    var drNmAlBlks = BinaryPrimitives.ReadUInt16BigEndian(mdb[18..]);
    var drAlBlkSiz = BinaryPrimitives.ReadUInt32BigEndian(mdb[20..]);
    var drAlBlSt = BinaryPrimitives.ReadUInt16BigEndian(mdb[28..]);
    var ctStart = BinaryPrimitives.ReadUInt16BigEndian(mdb[150..]);
    var ctBlockCount = BinaryPrimitives.ReadUInt16BigEndian(mdb[152..]);
    if (drAlBlkSiz == 0 || drNmAlBlks == 0 || ctBlockCount == 0) return null;

    var allocBase = drAlBlSt * 512;
    var catalogBase = allocBase + ctStart * (int)drAlBlkSiz;
    if (catalogBase + BTreeNodeSize > img.Length) return null;

    // Inspect catalog header to confirm single-leaf shape.
    var hdrNode = img.AsSpan(catalogBase);
    if ((sbyte)hdrNode[8] != 1) return null;       // not a header node
    var hdr = hdrNode[14..];
    var treeDepth = BinaryPrimitives.ReadUInt16BigEndian(hdr);
    var rootNode = BinaryPrimitives.ReadUInt32BigEndian(hdr[2..]);
    var firstLeaf = BinaryPrimitives.ReadUInt32BigEndian(hdr[10..]);
    var lastLeaf = BinaryPrimitives.ReadUInt32BigEndian(hdr[14..]);
    var single = treeDepth == 1 && rootNode == 1 && firstLeaf == 1 && lastLeaf == 1;

    return new VolumeContext {
      AllocBase = allocBase,
      AllocBlockSize = drAlBlkSiz,
      NumAllocBlocks = drNmAlBlks,
      CatalogStartAbs = ctStart,
      CatalogBlockCount = ctBlockCount,
      BitmapBase = drVBMSt * 512,
      TotalSectors = img.Length / 512,
      IsSingleLeaf = single,
    };
  }

  // ── Leaf record helpers ─────────────────────────────────────────────────

  /// <summary>
  /// Locates a file record (recordType=2) under parent==CnidRootDir whose name
  /// matches <paramref name="name"/>.
  /// </summary>
  private static bool TryFindFileRecord(ReadOnlySpan<byte> leaf, string name,
      out int recordIndex, out uint fileCnid, out ushort startBlock, out ushort blockCount) {
    recordIndex = -1; fileCnid = 0; startBlock = 0; blockCount = 0;
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    var nameBytes = Encoding.Latin1.GetBytes(name);

    for (var i = 0; i < numRecords; i++) {
      var recOff = ReadOffset(leaf, i);
      if (recOff < 14 || recOff + 1 > leaf.Length) continue;
      var keyLen = leaf[recOff];
      if (keyLen < 6 || recOff + 1 + keyLen > leaf.Length) continue;
      var parent = BinaryPrimitives.ReadUInt32BigEndian(leaf[(recOff + 2)..]);
      if (parent != CnidRootDir) continue;
      var nameLen = leaf[recOff + 6];
      if (recOff + 7 + nameLen > leaf.Length) continue;
      if (nameLen != nameBytes.Length) continue;
      var equal = true;
      for (var b = 0; b < nameLen; b++) if (leaf[recOff + 7 + b] != nameBytes[b]) { equal = false; break; }
      if (!equal) continue;

      var dataPos = recOff + 1 + keyLen;
      if ((dataPos & 1) != 0) dataPos++;
      if (dataPos + FilRecDataLen > leaf.Length) continue;
      if (leaf[dataPos] != RecFile) continue;

      fileCnid = BinaryPrimitives.ReadUInt32BigEndian(leaf[(dataPos + 20)..]);
      startBlock = BinaryPrimitives.ReadUInt16BigEndian(leaf[(dataPos + 74)..]);
      blockCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[(dataPos + 76)..]);
      recordIndex = i;
      return true;
    }
    return false;
  }

  /// <summary>
  /// Locates the file thread record whose key parent is <paramref name="fileCnid"/>
  /// and whose name is empty.
  /// </summary>
  private static int FindFileThreadRecord(ReadOnlySpan<byte> leaf, uint fileCnid) {
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    for (var i = 0; i < numRecords; i++) {
      var recOff = ReadOffset(leaf, i);
      if (recOff < 14 || recOff + 7 > leaf.Length) continue;
      var keyLen = leaf[recOff];
      if (keyLen < 6) continue;
      var parent = BinaryPrimitives.ReadUInt32BigEndian(leaf[(recOff + 2)..]);
      var nameLen = leaf[recOff + 6];
      if (parent != fileCnid || nameLen != 0) continue;
      var dataPos = recOff + 1 + keyLen;
      if ((dataPos & 1) != 0) dataPos++;
      if (dataPos + ThdRecDataLen > leaf.Length) continue;
      if (leaf[dataPos] == RecFileThread) return i;
    }
    return -1;
  }

  /// <summary>Adjusts the root-dir record's valence (file count) by delta.</summary>
  private static void AdjustRootValence(Span<byte> leaf, int delta) {
    var numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    for (var i = 0; i < numRecords; i++) {
      var recOff = ReadOffset(leaf, i);
      if (recOff < 14 || recOff + 7 > leaf.Length) continue;
      var keyLen = leaf[recOff];
      if (keyLen < 6) continue;
      var parent = BinaryPrimitives.ReadUInt32BigEndian(leaf[(recOff + 2)..]);
      if (parent != CnidRootParent) continue;
      var dataPos = recOff + 1 + keyLen;
      if ((dataPos & 1) != 0) dataPos++;
      if (dataPos + 6 > leaf.Length) continue;
      if (leaf[dataPos] != RecFolder) continue;
      var valence = BinaryPrimitives.ReadUInt16BigEndian(leaf[(dataPos + 4)..]);
      var nv = (int)valence + delta;
      if (nv < 0) nv = 0;
      if (nv > ushort.MaxValue) nv = ushort.MaxValue;
      BinaryPrimitives.WriteUInt16BigEndian(leaf[(dataPos + 4)..], (ushort)nv);
      return;
    }
  }

  private static int ReadOffset(ReadOnlySpan<byte> leaf, int recordIndex) {
    var offsetPos = BTreeNodeSize - 2 * (recordIndex + 1);
    return BinaryPrimitives.ReadUInt16BigEndian(leaf[offsetPos..]);
  }

  /// <summary>
  /// Inserts <paramref name="record"/> sorted by HFS catalog key (parentID asc,
  /// then MacRoman-like binary name asc). Returns false on insufficient space.
  /// </summary>
  private static bool TryInsertLeafRecord(Span<byte> leaf, byte[] record, uint keyParent, string keyName) {
    int numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    var newKeyName = Encoding.Latin1.GetBytes(keyName);

    // Snapshot offsets BEFORE any mutation.
    var oldOffsets = new ushort[numRecords + 1];
    for (var i = 0; i < numRecords + 1; i++)
      oldOffsets[i] = BinaryPrimitives.ReadUInt16BigEndian(leaf[(BTreeNodeSize - 2 * (i + 1))..]);
    var freeOff = oldOffsets[numRecords];

    // Determine insertion index by walking existing records.
    var insertIndex = numRecords;
    for (var i = 0; i < numRecords; i++) {
      var off = oldOffsets[i];
      var p = BinaryPrimitives.ReadUInt32BigEndian(leaf[(off + 2)..]);
      var nl = leaf[off + 6];
      var cmp = CompareKey(p, leaf.Slice(off + 7, nl), keyParent, newKeyName);
      if (cmp >= 0) { insertIndex = i; break; }
    }

    var newRecLen = record.Length;
    if ((newRecLen & 1) != 0) newRecLen++;

    var existingTableBytes = 2 * (numRecords + 1);
    var available = BTreeNodeSize - freeOff - existingTableBytes;
    if (available < newRecLen + 2) return false;

    var insertPos = insertIndex < numRecords ? oldOffsets[insertIndex] : freeOff;

    // Shift payload [insertPos..freeOff) forward by newRecLen.
    var bytesToShift = freeOff - insertPos;
    if (bytesToShift > 0) {
      Span<byte> temp = bytesToShift <= 256 ? stackalloc byte[bytesToShift] : new byte[bytesToShift];
      leaf.Slice(insertPos, bytesToShift).CopyTo(temp);
      temp.CopyTo(leaf.Slice(insertPos + newRecLen, bytesToShift));
    }
    record.AsSpan().CopyTo(leaf.Slice(insertPos, record.Length));
    if (newRecLen > record.Length) leaf[insertPos + record.Length] = 0;

    // Build new offsets (numRecords+2 entries) from the snapshot.
    var newOffsets = new ushort[numRecords + 2];
    for (var i = 0; i < insertIndex; i++) newOffsets[i] = oldOffsets[i];
    newOffsets[insertIndex] = (ushort)insertPos;
    for (var i = insertIndex; i < numRecords; i++) newOffsets[i + 1] = (ushort)(oldOffsets[i] + newRecLen);
    newOffsets[numRecords + 1] = (ushort)(oldOffsets[numRecords] + newRecLen);

    // Clear old offset region (now one entry shorter at high end).
    for (var i = 0; i < oldOffsets.Length; i++) {
      var pos = BTreeNodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], 0);
    }
    for (var i = 0; i < newOffsets.Length; i++) {
      var pos = BTreeNodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], newOffsets[i]);
    }

    BinaryPrimitives.WriteUInt16BigEndian(leaf[10..], (ushort)(numRecords + 1));
    return true;
  }

  private static void RemoveLeafRecord(Span<byte> leaf, int recordIndex) {
    int numRecords = BinaryPrimitives.ReadUInt16BigEndian(leaf[10..]);
    if (recordIndex < 0 || recordIndex >= numRecords) return;

    var offsets = new ushort[numRecords + 1];
    for (var i = 0; i < numRecords + 1; i++)
      offsets[i] = BinaryPrimitives.ReadUInt16BigEndian(leaf[(BTreeNodeSize - 2 * (i + 1))..]);

    var recStart = offsets[recordIndex];
    var recEnd = offsets[recordIndex + 1];
    var recLen = recEnd - recStart;
    var bytesAfter = offsets[numRecords] - recEnd;

    if (bytesAfter > 0)
      leaf.Slice(recEnd, bytesAfter).CopyTo(leaf.Slice(recStart, bytesAfter));
    leaf.Slice(offsets[numRecords] - recLen, recLen).Clear();

    var newOffsets = new ushort[numRecords];
    for (var i = 0; i < recordIndex; i++) newOffsets[i] = offsets[i];
    for (var i = recordIndex + 1; i <= numRecords; i++) newOffsets[i - 1] = (ushort)(offsets[i] - recLen);

    // Zero entire old offset region; then write new entries.
    for (var i = 0; i < numRecords + 1; i++) {
      var pos = BTreeNodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], 0);
    }
    for (var i = 0; i < newOffsets.Length; i++) {
      var pos = BTreeNodeSize - 2 * (i + 1);
      BinaryPrimitives.WriteUInt16BigEndian(leaf[pos..], newOffsets[i]);
    }
    BinaryPrimitives.WriteUInt16BigEndian(leaf[10..], (ushort)(numRecords - 1));
  }

  // ── Bitmap helpers ──────────────────────────────────────────────────────

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
    var byteIdx = bitmapBase + (int)(block >> 3);
    var bitIdx = 7 - (int)(block & 7);
    if (byteIdx >= img.Length) return true;
    return (img[byteIdx] & (1 << bitIdx)) != 0;
  }

  private static void SetBitmapBit(byte[] img, int bitmapBase, uint block) {
    var byteIdx = bitmapBase + (int)(block >> 3);
    var bitIdx = 7 - (int)(block & 7);
    if (byteIdx >= img.Length) return;
    img[byteIdx] |= (byte)(1 << bitIdx);
  }

  private static void ClearBitmapBit(byte[] img, int bitmapBase, uint block) {
    var byteIdx = bitmapBase + (int)(block >> 3);
    var bitIdx = 7 - (int)(block & 7);
    if (byteIdx >= img.Length) return;
    img[byteIdx] &= (byte)~(1 << bitIdx);
  }

  // ── Record builders (mirror HfsWriter) ──────────────────────────────────

  private static byte[] BuildCatalogKey(uint parentID, string name) {
    var nameBytes = Encoding.Latin1.GetBytes(name);
    if (nameBytes.Length > 31) throw new ArgumentOutOfRangeException(nameof(name));
    var keyLen = (byte)(1 + 4 + 1 + nameBytes.Length);
    var buf = new byte[1 + keyLen];
    buf[0] = keyLen;
    buf[1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(2), parentID);
    buf[6] = (byte)nameBytes.Length;
    nameBytes.CopyTo(buf, 7);
    return buf;
  }

  private static byte[] BuildFileRecord(uint parentID, string name, uint fileID,
      ushort dataStart, ushort dataBlocks, uint dataSize, uint blockSize) {
    var key = BuildCatalogKey(parentID, name);
    var rec = new byte[AlignEven(key.Length) + FilRecDataLen];
    key.CopyTo(rec, 0);
    var d = rec.AsSpan(AlignEven(key.Length));
    d[0] = RecFile;
    d[1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(d[20..], fileID);
    BinaryPrimitives.WriteUInt16BigEndian(d[24..], dataStart);
    BinaryPrimitives.WriteUInt32BigEndian(d[26..], dataSize);
    BinaryPrimitives.WriteUInt32BigEndian(d[30..], (uint)(dataBlocks * blockSize));
    var now = (uint)ToHfsTime(DateTime.UtcNow);
    BinaryPrimitives.WriteUInt32BigEndian(d[44..], now);   // crDate
    BinaryPrimitives.WriteUInt32BigEndian(d[48..], now);   // mdDate
    BinaryPrimitives.WriteUInt16BigEndian(d[72..], (ushort)(4 * blockSize));
    // First inline data-fork extent at offset 74.
    BinaryPrimitives.WriteUInt16BigEndian(d[74..], dataStart);
    BinaryPrimitives.WriteUInt16BigEndian(d[76..], dataBlocks);
    return rec;
  }

  private static byte[] BuildThreadRecord(byte type, uint keyParentID, string keyName,
      uint targetParent, string targetName) {
    var key = BuildCatalogKey(keyParentID, keyName);
    var rec = new byte[AlignEven(key.Length) + ThdRecDataLen];
    key.CopyTo(rec, 0);
    var d = rec.AsSpan(AlignEven(key.Length));
    d[0] = type;
    d[1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(d[10..], targetParent);
    var nameBytes = Encoding.Latin1.GetBytes(targetName);
    if (nameBytes.Length > 31) nameBytes = nameBytes.AsSpan(0, 31).ToArray();
    d[14] = (byte)nameBytes.Length;
    nameBytes.CopyTo(d[15..]);
    return rec;
  }

  // ── MDB / housekeeping ──────────────────────────────────────────────────

  private static void MirrorAlternateMdb(byte[] img, VolumeContext ctx) {
    var totalSectors = ctx.TotalSectors;
    if (totalSectors < 4) return;
    var altOff = (totalSectors - 2) * 512;
    if (altOff + MdbSize > img.Length) return;
    img.AsSpan(MdbOffset, MdbSize).CopyTo(img.AsSpan(altOff, MdbSize));
  }

  private static int CompareKey(uint pa, ReadOnlySpan<byte> na, uint pb, ReadOnlySpan<byte> nb) {
    if (pa != pb) return pa.CompareTo(pb);
    var min = Math.Min(na.Length, nb.Length);
    for (var i = 0; i < min; i++) if (na[i] != nb[i]) return na[i].CompareTo(nb[i]);
    return na.Length.CompareTo(nb.Length);
  }

  private static void UpdateUInt16(Span<byte> mdb, int off, int delta) {
    var v = BinaryPrimitives.ReadUInt16BigEndian(mdb[off..]);
    var nv = (int)v + delta;
    if (nv < 0) nv = 0;
    if (nv > ushort.MaxValue) nv = ushort.MaxValue;
    BinaryPrimitives.WriteUInt16BigEndian(mdb[off..], (ushort)nv);
  }

  private static void UpdateUInt32(Span<byte> mdb, int off, int delta) {
    var v = BinaryPrimitives.ReadUInt32BigEndian(mdb[off..]);
    var nv = (long)v + delta;
    if (nv < 0) nv = 0;
    if (nv > uint.MaxValue) nv = uint.MaxValue;
    BinaryPrimitives.WriteUInt32BigEndian(mdb[off..], (uint)nv);
  }

  private static void UpdateUInt16Add(Span<byte> mdb, int off, uint delta) {
    var v = BinaryPrimitives.ReadUInt16BigEndian(mdb[off..]);
    var nv = (uint)v + delta;
    if (nv > ushort.MaxValue) nv = ushort.MaxValue;
    BinaryPrimitives.WriteUInt16BigEndian(mdb[off..], (ushort)nv);
  }

  private static void UpdateUInt16Sub(Span<byte> mdb, int off, uint delta) {
    var v = BinaryPrimitives.ReadUInt16BigEndian(mdb[off..]);
    var nv = v >= delta ? (int)(v - delta) : 0;
    BinaryPrimitives.WriteUInt16BigEndian(mdb[off..], (ushort)nv);
  }

  private static int AlignEven(int n) => (n + 1) & ~1;

  private static long ToHfsTime(DateTime utc) {
    if (utc.Kind == DateTimeKind.Unspecified) utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    var s = (long)(utc.ToUniversalTime() - HfsEpoch).TotalSeconds;
    if (s < 0) s = 0;
    if (s > uint.MaxValue) s = uint.MaxValue;
    return s;
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
}

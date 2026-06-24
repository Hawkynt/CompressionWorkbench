#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Genuine in-place add for NTFS images — the inverse of <see cref="NtfsRemover"/>.
/// Inserts a small file into the root directory by editing only the structures the
/// change touches: it claims a free MFT record slot (from the MFT's cluster-rounding
/// slack), writes a spec-shaped FILE record (header + USA fixup + $STANDARD_INFORMATION
/// + $FILE_NAME + resident or non-resident $DATA), sets the record's bit in
/// <c>$MFT:$BITMAP</c>, allocates any data clusters from <c>$Bitmap</c>, and inserts a
/// collation-sorted index entry into the root directory's resident <c>$INDEX_ROOT</c>.
/// Existing files, their MFT records and their data clusters stay byte-identical at
/// their original offsets — no whole-image re-pack.
/// <para>
/// The in-place path also handles the structural growth cases: it grows the MFT
/// (allocating a cluster, extending <c>$MFT:$DATA</c>'s run list — the reader follows
/// the VCN→LCN mapping — widening <c>$MFT:$BITMAP</c> and re-syncing <c>$MFTMirr</c>)
/// when no record slot is free; it spills a directory index that outgrows the resident
/// <c>$INDEX_ROOT</c> into a pointer-form root + non-resident <c>$INDEX_ALLOCATION</c>
/// (a single INDX leaf, USA-fixed) + <c>$BITMAP</c>, growing the INDX block size as the
/// directory grows; and it creates intermediate sub-directory FILE records for nested
/// targets (<c>a/b/c.txt</c>), linking them through their parents' <c>$I30</c> indexes.
/// It still throws (so the caller can rebuild) when an allocation cannot be satisfied —
/// no free data clusters, no contiguous index-allocation run, or a record/attribute that
/// will not fit.
/// </para>
/// </summary>
public static class NtfsInPlaceAdder {

  private const uint RootRecord = 5;
  private const int FirstUserRecord = 16;

  /// <summary>
  /// Adds (or replaces by name) <paramref name="name"/> into the root directory of the
  /// in-memory NTFS image. Throws <see cref="NotSupportedException"/> / <see cref="IOException"/>
  /// for the structural cases above so the caller can rebuild instead.
  /// </summary>
  public static void AddFile(byte[] image, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var geo = ParseBoot(image);

    // Split a nested path (a/b/c.txt) into its directory chain + leaf file name.
    var parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new ArgumentException("NTFS in-place add: empty name.", nameof(name));
    var leafName = parts[^1];

    // Resolve/create each intermediate directory, descending from the root, so the
    // leaf file lands in the right parent.
    var parentRecord = RootRecord;
    for (var i = 0; i < parts.Length - 1; i++)
      parentRecord = EnsureDirectory(image, geo, parentRecord, parts[i]);

    // Replace-by-name only for top-level (the remover keys on the flat name);
    // nested replaces fall through as fresh adds, which is acceptable for add.
    if (parts.Length == 1)
      try { NtfsRemover.Remove(image, leafName); } catch (FileNotFoundException) { /* new file */ }

    // Decide $DATA residency: small files live inside the MFT record (resident).
    var resident = data.Length <= geo.ResidentThreshold;
    List<(long Lcn, long Count)> dataRuns = [];
    if (!resident) {
      var clustersNeeded = (data.Length + geo.ClusterSize - 1) / geo.ClusterSize;
      dataRuns = AllocateClusters(image, geo, clustersNeeded);
      WriteDataToRuns(image, geo, dataRuns, data);
    }

    var slot = AllocateMftSlot(image, geo);
    var fileRecord = BuildFileRecord(geo, (uint)slot, leafName, parentRecord, resident ? data : null,
      resident ? null : dataRuns, data.Length);

    var recordOffset = (int)MftRecordOffset(image, geo, slot);
    fileRecord.CopyTo(image, recordOffset);

    SetMftBitmapBit(image, geo, slot);
    ExtendMftDataSize(image, geo, slot);
    InsertIndexEntry(image, geo, parentRecord, (uint)slot, leafName);
    SyncMftMirror(image, geo); // record 0 (sizes/$BITMAP) changed — keep $MFTMirr identical
  }

  // Returns a free MFT record slot, growing the MFT in place if none is free.
  private static int AllocateMftSlot(byte[] image, Geo geo) {
    var slot = FindFreeMftRecord(image, geo);
    return slot >= 0 ? slot : GrowMft(image, geo);
  }

  // Finds (or creates) the immediate child directory `dirName` under `parentRecord`,
  // returning its MFT record number. A created directory gets a spec-shaped FILE
  // record (STD_INFO + FILE_NAME with the DIRECTORY flag + empty resident
  // $INDEX_ROOT) and is linked into the parent's $I30 index.
  private static uint EnsureDirectory(byte[] image, Geo geo, uint parentRecord, string dirName) {
    var existing = FindChildInIndex(image, geo, parentRecord, dirName);
    if (existing > 0) return existing;

    var slot = AllocateMftSlot(image, geo);
    var record = BuildDirectoryRecord(geo, (uint)slot, dirName, parentRecord);
    var recordOffset = (int)MftRecordOffset(image, geo, slot);
    record.CopyTo(image, recordOffset);

    SetMftBitmapBit(image, geo, slot);
    ExtendMftDataSize(image, geo, slot);
    InsertIndexEntry(image, geo, parentRecord, (uint)slot, dirName);
    SyncMftMirror(image, geo);
    return (uint)slot;
  }

  // Looks up an immediate child by name in a directory's $I30 index (resident or
  // spilled). Returns its MFT record number, or 0 if not present.
  private static uint FindChildInIndex(byte[] image, Geo geo, uint dirRecord, string childName) {
    var dirOff = (int)MftRecordOffset(image, geo, (int)dirRecord);
    if (dirOff < 0 || dirOff + geo.MftRecordSize > image.Length) return 0;
    var dir = image.AsSpan(dirOff, geo.MftRecordSize).ToArray();
    ApplyFixup(dir);
    var (rootPos, _) = FindAttr(dir, 0x90, unnamedOnly: false);
    if (rootPos < 0) return 0;
    foreach (var (n, r) in CollectDirectoryLeafEntries(image, geo, dir, rootPos))
      if (string.Equals(n, childName, StringComparison.OrdinalIgnoreCase)) return r;
    return 0;
  }

  // Builds a directory FILE record: STD_INFO (DIRECTORY) + FILE_NAME (DIRECTORY) +
  // an empty resident $INDEX_ROOT ($I30) holding only the end-marker entry.
  private static byte[] BuildDirectoryRecord(Geo geo, uint recordNum, string name, uint parent) {
    var record = new byte[geo.MftRecordSize];
    var usaCount = 1 + geo.MftRecordSize / geo.BytesPerSector;
    const int attrStart = 56;

    record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 42);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)usaCount);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);                 // sequence
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 1);                 // hard link count
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), attrStart);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 0x03);              // in-use + directory
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)geo.MftRecordSize);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), recordNum);

    var pos = attrStart;
    pos = WriteStdInfo(record, pos, isDirectory: true);
    pos = WriteFileName(record, pos, name, parent, 0, isDirectory: true);
    pos = WriteEmptyIndexRoot(record, pos);

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0xFFFFFFFF);
    pos += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)pos);
    WriteUsaFixup(record, geo);
    return record;
  }

  // Writes an empty resident $INDEX_ROOT (0x90, named "$I30"): index-root header +
  // index-node header + a single end-marker entry. Mirrors NtfsWriter exactly.
  private static int WriteEmptyIndexRoot(byte[] record, int pos) {
    var name = Encoding.Unicode.GetBytes("$I30");
    var nameOffset = 24;
    var valueOffset = (nameOffset + name.Length + 7) & ~7;

    var rootHeader = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(rootHeader.AsSpan(0), 0x30); // FILE_NAME collation key
    BinaryPrimitives.WriteUInt32LittleEndian(rootHeader.AsSpan(4), 1);     // FILENAME collation
    BinaryPrimitives.WriteUInt32LittleEndian(rootHeader.AsSpan(8), 4096);  // bytes per INDX block
    rootHeader[12] = 1;

    var end = BuildEndMarker();
    var valueLen = rootHeader.Length + 16 /*node hdr*/ + end.Length;
    var attrLen = (valueOffset + valueLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x90);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 9] = (byte)(name.Length / 2);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 10), (ushort)nameOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), (ushort)valueOffset);
    name.CopyTo(record, pos + nameOffset);

    var vp = pos + valueOffset;
    rootHeader.CopyTo(record, vp);
    var nhdr = vp + 16;
    BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(nhdr), 16);
    BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(nhdr + 4), 16 + end.Length);
    BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(nhdr + 8), 16 + end.Length);
    end.CopyTo(record, nhdr + 16);
    return pos + attrLen;
  }

  // ntfs-3g derives the number of valid MFT records from $MFT's $DATA data_size
  // (NOT allocated_size). A record placed in the cluster-rounding slack past that
  // size reads as "non-allocated"; bump the real/initialised size so the new slot is
  // in range. The clusters are already allocated, so no $Bitmap/run change is needed.
  private static void ExtendMftDataSize(byte[] image, Geo geo, int slot) {
    var rec0Off = (int)geo.MftOffset;
    var rec0 = image.AsSpan(rec0Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);
    var (attrPos, _) = FindAttr(rec0, 0x80, unnamedOnly: true);
    if (attrPos < 0 || rec0[attrPos + 8] == 0) return; // resident $MFT — not our images
    var needed = (long)(slot + 1) * geo.MftRecordSize;
    var realSize = BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 48));
    var allocSize = BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 40));
    if (needed <= realSize) return;
    if (needed > allocSize)
      throw new IOException("NTFS in-place add: MFT would need to grow past its allocated clusters.");
    BinaryPrimitives.WriteInt64LittleEndian(rec0.AsSpan(attrPos + 48), needed); // real size
    BinaryPrimitives.WriteInt64LittleEndian(rec0.AsSpan(attrPos + 56), needed); // initialised size
    WriteUsaFixup(rec0, geo);
    rec0.CopyTo(image, rec0Off);
  }

  // ── MFT growth ────────────────────────────────────────────────────────────

  // Grows the MFT by one cluster in place: allocates a cluster from $Bitmap
  // (preferring the cluster immediately after the MFT's current last extent so
  // growth stays contiguous), appends it to $MFT:$DATA's run list, bumps the
  // allocated size, zeroes the new record slots and widens $MFT:$BITMAP to cover
  // them. Returns the first newly available record slot. The new slot's real /
  // initialised size is bumped by ExtendMftDataSize once the record is written.
  private static int GrowMft(byte[] image, Geo geo) {
    var rec0Off = (int)geo.MftOffset;
    var rec0 = image.AsSpan(rec0Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);

    var (dataPos, dataLen) = FindAttr(rec0, 0x80, unnamedOnly: true);
    if (dataPos < 0 || rec0[dataPos + 8] == 0)
      throw new IOException("NTFS in-place add: cannot grow a resident/degenerate $MFT.");

    var runsOff = dataPos + BinaryPrimitives.ReadUInt16LittleEndian(rec0.AsSpan(dataPos + 32));
    var runs = DecodeDataRuns(rec0, runsOff);
    if (runs.Count == 0) throw new IOException("NTFS in-place add: $MFT has no data runs.");

    long mftClusters = 0;
    foreach (var (_, c) in runs) mftClusters += c;
    var lastLcn = runs[^1].Lcn;
    var lastEnd = lastLcn + runs[^1].Count;

    // The MFT's allocated bytes must stay a whole number of clusters; the new
    // slot is the first record in the freshly allocated cluster.
    var firstNewSlot = (int)(mftClusters * geo.ClusterSize / geo.MftRecordSize);

    // Prefer a contiguous extension (the cluster right after the MFT) so the run
    // list need not grow; otherwise take any free cluster (a new, possibly
    // non-contiguous, run — the reader follows VCN→LCN so this stays readable).
    var newLcn = AllocateSpecificOrAnyCluster(image, geo, lastEnd);

    // Zero the new cluster's record slots so they read as free.
    var newClusterByteOff = (int)(newLcn * geo.ClusterSize);
    if (newClusterByteOff + geo.ClusterSize > image.Length)
      throw new IOException("NTFS in-place add: MFT growth cluster out of image range.");
    image.AsSpan(newClusterByteOff, geo.ClusterSize).Clear();

    // Append the cluster to the run list (coalesce if contiguous).
    if (newLcn == lastEnd)
      runs[^1] = (lastLcn, runs[^1].Count + 1);
    else
      runs.Add((newLcn, 1));

    var newMftClusters = mftClusters + 1;
    var newAllocBytes = newMftClusters * geo.ClusterSize;
    var newRunBytes = EncodeDataRuns(runs);

    // Rewrite record 0's $DATA attribute (run list + sizes), shifting any
    // following attributes ($BITMAP) by the run-list length delta.
    const int dataRunsOffset = 64; // matches NtfsWriter's non-resident layout
    var newDataAttrLen = (dataRunsOffset + newRunBytes.Length + 7) & ~7;
    var usedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec0.AsSpan(24));
    var tailStart = dataPos + dataLen;          // first byte after the old $DATA attr
    var tailLen = usedSize - tailStart;          // $BITMAP + end marker
    var delta = newDataAttrLen - dataLen;
    if (usedSize + delta + 8 > geo.MftRecordSize)
      throw new IOException("NTFS in-place add: $MFT record 0 cannot hold the grown $DATA run list.");

    var rebuilt = new byte[geo.MftRecordSize];
    rec0.AsSpan(0, dataPos).CopyTo(rebuilt);               // header + STD_INFO + FILE_NAME
    // $DATA attribute header: copy the fixed non-resident header (0..dataRunsOffset)
    // from the original, then patch length / sizes / runs.
    rec0.AsSpan(dataPos, dataRunsOffset).CopyTo(rebuilt.AsSpan(dataPos));
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(dataPos + 4), (uint)newDataAttrLen);
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(dataPos + 32), dataRunsOffset);
    BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(dataPos + 24), newMftClusters - 1); // last VCN
    BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(dataPos + 40), newAllocBytes);       // allocated
    // real (48) / initialised (56) sizes are bumped by ExtendMftDataSize per slot.
    newRunBytes.CopyTo(rebuilt, dataPos + dataRunsOffset);
    // Pad the gap between run list end and the 8-byte-aligned attribute end with 0.
    // Copy the trailing attributes ($BITMAP, end marker) after the grown $DATA.
    var newTailStart = dataPos + newDataAttrLen;
    rec0.AsSpan(tailStart, tailLen).CopyTo(rebuilt.AsSpan(newTailStart));
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(24), (uint)(usedSize + delta));

    WriteUsaFixup(rebuilt, geo);
    rebuilt.CopyTo(image, rec0Off);

    // Widen $MFT:$BITMAP to cover the new slots (clear bits — slots are free).
    EnsureMftBitmapCovers(image, geo, firstNewSlot);

    SyncMftMirror(image, geo); // record 0 changed
    return firstNewSlot;
  }

  // Allocates the preferred cluster if it is free in $Bitmap; otherwise allocates
  // the first free cluster. Marks it used and returns its LCN.
  private static long AllocateSpecificOrAnyCluster(byte[] image, Geo geo, long preferred) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    if (preferred >= 0 && preferred < geo.TotalClusters) {
      var bOff = bmByteOffset + (int)(preferred / 8);
      var mask = (byte)(1 << (int)(preferred % 8));
      var clusterByte = preferred * geo.ClusterSize;
      if (bOff < image.Length && (image[bOff] & mask) == 0
          && clusterByte + geo.ClusterSize <= image.Length) {
        image[bOff] |= mask;
        return preferred;
      }
    }
    var runs = AllocateClusters(image, geo, 1);
    return runs[0].Lcn;
  }

  // Returns the byte offset of the cluster-allocation bitmap ($Bitmap, record 6's
  // non-resident $DATA) and its first run's LCN.
  private static (int ByteOffset, long Lcn) LocateClusterBitmap(byte[] image, Geo geo) {
    var rec6Off = (int)(geo.MftOffset + 6L * geo.MftRecordSize);
    var rec6 = image.AsSpan(rec6Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec6);
    var (attrPos, _) = FindAttr(rec6, 0x80, unnamedOnly: true);
    if (attrPos < 0 || rec6[attrPos + 8] == 0)
      throw new IOException("NTFS in-place add: $Bitmap not found / resident.");
    var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec6.AsSpan(attrPos + 32));
    var bmLcn = FirstRunLcn(rec6, runsOff);
    return ((int)(bmLcn * geo.ClusterSize), bmLcn);
  }

  // Widens $MFT:$BITMAP so its declared size covers `slot` (and clears its bit so
  // the slot reads as free). The bitmap stays within its already-allocated cluster.
  private static void EnsureMftBitmapCovers(byte[] image, Geo geo, int slot) {
    var rec0Off = (int)geo.MftOffset;
    var rec0 = image.AsSpan(rec0Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);
    var (attrPos, _) = FindAttr(rec0, 0xB0, unnamedOnly: false);
    if (attrPos < 0) throw new IOException("NTFS in-place add: $MFT:$BITMAP not found.");
    if (rec0[attrPos + 8] == 0) return; // resident bitmap: SetMftBitmapBit handles bounds

    var byteIndex = slot / 8;
    var allocBytes = BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 40));
    if (byteIndex >= allocBytes)
      throw new IOException("NTFS in-place add: $MFT:$BITMAP cluster would overflow on growth.");

    var realSize = BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 48));
    if (byteIndex + 1 > realSize) {
      var live = image.AsSpan(rec0Off, geo.MftRecordSize).ToArray();
      ApplyFixup(live);
      BinaryPrimitives.WriteInt64LittleEndian(live.AsSpan(attrPos + 48), byteIndex + 1);
      BinaryPrimitives.WriteInt64LittleEndian(live.AsSpan(attrPos + 56), byteIndex + 1);
      WriteUsaFixup(live, geo);
      live.CopyTo(image, rec0Off);
    }
  }

  // ── Boot geometry ─────────────────────────────────────────────────────────

  private readonly record struct Geo(
      int BytesPerSector, int SectorsPerCluster, int ClusterSize,
      long MftOffset, long MftMirrOffset, int MftRecordSize, long TotalClusters) {
    public int ResidentThreshold => 700;
  }

  private static Geo ParseBoot(byte[] image) {
    var bps = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bps == 0) bps = 512;
    var spc = image[13] == 0 ? (byte)8 : image[13];
    var clusterSize = bps * spc;
    var totalSectors = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(40));
    var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(48));
    var mftMirrCluster = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(56));
    var cpr = (sbyte)image[64];
    var recSize = cpr < 0 ? 1 << (-cpr) : cpr * clusterSize;
    return new Geo(bps, spc, clusterSize, mftCluster * clusterSize, mftMirrCluster * clusterSize,
      recSize, totalSectors / spc);
  }

  // $MFTMirr (record 1) holds byte-identical copies of MFT records 0..3; ntfs-3g
  // rejects the volume if any differ. Re-sync after editing record 0 (sizes/bitmap).
  private static void SyncMftMirror(byte[] image, Geo geo) {
    var mirrorRecords = Math.Min(4, MftMirrRecordCount(image, geo));
    for (var i = 0; i < mirrorRecords; i++) {
      var src = (int)(geo.MftOffset + (long)i * geo.MftRecordSize);
      var dst = (int)(geo.MftMirrOffset + (long)i * geo.MftRecordSize);
      if (src + geo.MftRecordSize > image.Length || dst + geo.MftRecordSize > image.Length) break;
      image.AsSpan(src, geo.MftRecordSize).CopyTo(image.AsSpan(dst));
    }
  }

  private static int MftMirrRecordCount(byte[] image, Geo geo) {
    var rec1 = image.AsSpan((int)(geo.MftOffset + geo.MftRecordSize), geo.MftRecordSize).ToArray();
    ApplyFixup(rec1);
    var (attrPos, _) = FindAttr(rec1, 0x80, unnamedOnly: true);
    if (attrPos < 0) return 4;
    var size = rec1[attrPos + 8] == 0
      ? BinaryPrimitives.ReadUInt32LittleEndian(rec1.AsSpan(attrPos + 16))
      : BinaryPrimitives.ReadInt64LittleEndian(rec1.AsSpan(attrPos + 48));
    return (int)Math.Max(1, size / geo.MftRecordSize);
  }

  // ── MFT record slot ─────────────────────────────────────────────────────────

  // The MFT's allocated byte span = record 0's $DATA allocated size; record slots in
  // the cluster-rounding slack past the last used record are free (zeroed).
  private static int FindFreeMftRecord(byte[] image, Geo geo) {
    var mftDataAllocBytes = MftDataAllocatedBytes(image, geo);
    var maxSlots = (int)(mftDataAllocBytes / geo.MftRecordSize);
    for (var i = FirstUserRecord; i < maxSlots; i++) {
      var off = (int)MftRecordOffset(image, geo, i);
      if (off < 0 || off + 4 > image.Length) break;
      // Free slot = no "FILE" signature (zeroed) or in-use flag clear.
      var isFile = image[off] == 'F' && image[off + 1] == 'I' && image[off + 2] == 'L' && image[off + 3] == 'E';
      if (!isFile) return i;
      var copy = image.AsSpan(off, geo.MftRecordSize).ToArray();
      ApplyFixup(copy);
      if ((BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(22)) & 0x01) == 0) return i;
    }
    return -1; // none free within the MFT's allocated clusters — caller grows the MFT
  }

  private static List<(long Lcn, long Count)> DecodeDataRuns(byte[] record, int offset) {
    var runs = new List<(long Lcn, long Count)>();
    long prevLcn = 0;
    while (offset < record.Length) {
      var header = record[offset];
      if (header == 0) break;
      var lenB = header & 0x0F;
      var offB = (header >> 4) & 0x0F;
      offset++;
      long length = 0;
      for (var i = 0; i < lenB; i++) length |= (long)record[offset + i] << (i * 8);
      offset += lenB;
      long delta = 0;
      for (var i = 0; i < offB; i++) delta |= (long)record[offset + i] << (i * 8);
      if (offB > 0 && (record[offset + offB - 1] & 0x80) != 0)
        for (var i = offB; i < 8; i++) delta |= (long)0xFF << (i * 8);
      offset += offB;
      prevLcn += delta;
      runs.Add((prevLcn, length));
    }
    return runs;
  }

  // Maps an MFT record slot to its physical byte offset via the $MFT:$DATA run list.
  // The MFT is NOT necessarily contiguous: when it grows past a region already taken
  // by $LogFile/$Bitmap/etc., growth appends a non-contiguous run, so a record's
  // physical location must follow the VCN→LCN mapping, not slot*recordSize.
  private static long MftRecordOffset(byte[] image, Geo geo, int slot) {
    var vcnByte = (long)slot * geo.MftRecordSize;
    var targetCluster = vcnByte / geo.ClusterSize;
    var offsetInCluster = vcnByte % geo.ClusterSize;

    var rec0 = image.AsSpan((int)geo.MftOffset, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);
    var (dataPos, _) = FindAttr(rec0, 0x80, unnamedOnly: true);
    if (dataPos < 0 || rec0[dataPos + 8] == 0)
      return geo.MftOffset + vcnByte; // resident/degenerate — fall back to contiguous
    var runsOff = dataPos + BinaryPrimitives.ReadUInt16LittleEndian(rec0.AsSpan(dataPos + 32));
    var runs = DecodeDataRuns(rec0, runsOff);

    long vcn = 0;
    foreach (var (lcn, count) in runs) {
      if (targetCluster < vcn + count) {
        var lcnForCluster = lcn + (targetCluster - vcn);
        return lcnForCluster * geo.ClusterSize + offsetInCluster;
      }
      vcn += count;
    }
    return -1; // beyond the MFT's mapped clusters
  }

  private static long MftDataAllocatedBytes(byte[] image, Geo geo) {
    var rec0 = image.AsSpan((int)geo.MftOffset, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);
    var (attrPos, _) = FindAttr(rec0, 0x80, unnamedOnly: true);
    if (attrPos < 0) throw new InvalidDataException("NTFS: $MFT $DATA not found.");
    return BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 40)); // allocated size
  }

  // ── $MFT:$BITMAP bit ─────────────────────────────────────────────────────────

  private static void SetMftBitmapBit(byte[] image, Geo geo, int recordNum) {
    var rec0Off = (int)geo.MftOffset;
    var rec0 = image.AsSpan(rec0Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec0);
    var (attrPos, _) = FindAttr(rec0, 0xB0, unnamedOnly: false); // $MFT:$BITMAP (named "$I30"? no — unnamed bitmap)
    if (attrPos < 0) throw new InvalidDataException("NTFS: $MFT:$BITMAP not found.");

    var byteIndex = recordNum / 8;
    var bit = recordNum % 8;

    if (rec0[attrPos + 8] == 0) {
      // Resident bitmap (small images): value lives inside record 0.
      var valOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec0.AsSpan(attrPos + 20));
      var valLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec0.AsSpan(attrPos + 16));
      if (byteIndex >= valLen)
        throw new IOException("NTFS in-place add: $MFT:$BITMAP would need to grow (resident).");
      // Patch the byte in the live image record, then re-fixup the whole record.
      var live = image.AsSpan(rec0Off, geo.MftRecordSize).ToArray();
      ApplyFixup(live);
      live[valOff + byteIndex] |= (byte)(1 << bit);
      WriteUsaFixup(live, geo);
      live.CopyTo(image, rec0Off);
      return;
    }

    // Non-resident bitmap: locate its first data run cluster and set the bit there.
    var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec0.AsSpan(attrPos + 32));
    var firstLcn = FirstRunLcn(rec0, runsOff);
    var allocBytes = BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 40));
    if (byteIndex >= allocBytes)
      throw new IOException("NTFS in-place add: $MFT:$BITMAP cluster would overflow.");
    var bmByteOffset = (int)(firstLcn * geo.ClusterSize + byteIndex);
    if (bmByteOffset >= image.Length) throw new IOException("NTFS: $MFT:$BITMAP offset out of range.");
    image[bmByteOffset] |= (byte)(1 << bit);

    // Bump the declared data/real/initialised size of $MFT:$BITMAP if this record
    // extended its coverage, so ntfs-3g treats the bit as authoritative.
    var realSize = BinaryPrimitives.ReadInt64LittleEndian(rec0.AsSpan(attrPos + 48));
    if (byteIndex + 1 > realSize) {
      var live = image.AsSpan(rec0Off, geo.MftRecordSize).ToArray();
      ApplyFixup(live);
      BinaryPrimitives.WriteInt64LittleEndian(live.AsSpan(attrPos + 48), byteIndex + 1);
      BinaryPrimitives.WriteInt64LittleEndian(live.AsSpan(attrPos + 56), byteIndex + 1);
      WriteUsaFixup(live, geo);
      live.CopyTo(image, rec0Off);
    }
  }

  // ── $Bitmap cluster allocation ──────────────────────────────────────────────

  private static List<(long Lcn, long Count)> AllocateClusters(byte[] image, Geo geo, int count) {
    // $Bitmap is record 6's unnamed $DATA (non-resident). Decode its run, scan for
    // free bits, set them, return the LCNs (coalesced into runs).
    var rec6Off = (int)(geo.MftOffset + 6L * geo.MftRecordSize);
    var rec6 = image.AsSpan(rec6Off, geo.MftRecordSize).ToArray();
    ApplyFixup(rec6);
    var (attrPos, _) = FindAttr(rec6, 0x80, unnamedOnly: true);
    if (attrPos < 0 || rec6[attrPos + 8] == 0)
      throw new IOException("NTFS in-place add: $Bitmap not found / resident.");
    var runsOff = attrPos + BinaryPrimitives.ReadUInt16LittleEndian(rec6.AsSpan(attrPos + 32));
    var bmLcn = FirstRunLcn(rec6, runsOff);
    var bmByteOffset = (int)(bmLcn * geo.ClusterSize);

    var allocated = new List<long>(count);
    var maxCluster = geo.TotalClusters;
    for (long c = 0; c < maxCluster && allocated.Count < count; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff >= image.Length) break;
      var mask = (byte)(1 << (int)(c % 8));
      if ((image[bOff] & mask) != 0) continue;
      var clusterByte = c * geo.ClusterSize;
      if (clusterByte + geo.ClusterSize > image.Length) continue;
      image[bOff] |= mask;
      allocated.Add(c);
    }
    if (allocated.Count < count)
      throw new IOException($"NTFS in-place add: only {allocated.Count} free clusters, need {count}.");

    // Coalesce consecutive LCNs into runs.
    var runs = new List<(long Lcn, long Count)>();
    foreach (var lcn in allocated) {
      if (runs.Count > 0 && runs[^1].Lcn + runs[^1].Count == lcn)
        runs[^1] = (runs[^1].Lcn, runs[^1].Count + 1);
      else
        runs.Add((lcn, 1));
    }
    return runs;
  }

  // Allocates `count` consecutive free clusters from $Bitmap as one contiguous run
  // (needed for a multi-cluster INDX leaf, which must occupy a single run). Throws
  // if no contiguous gap of that size exists.
  private static long AllocateContiguousClusters(byte[] image, Geo geo, int count) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    var maxCluster = geo.TotalClusters;
    for (long start = 0; start + count <= maxCluster; start++) {
      var ok = true;
      for (long c = start; c < start + count; c++) {
        var bOff = bmByteOffset + (int)(c / 8);
        var clusterByte = c * geo.ClusterSize;
        if (bOff >= image.Length || (image[bOff] & (1 << (int)(c % 8))) != 0
            || clusterByte + geo.ClusterSize > image.Length) { ok = false; break; }
      }
      if (!ok) continue;
      for (long c = start; c < start + count; c++)
        image[bmByteOffset + (int)(c / 8)] |= (byte)(1 << (int)(c % 8));
      return start;
    }
    throw new IOException($"NTFS in-place add: no contiguous run of {count} clusters for the index.");
  }

  private static void WriteDataToRuns(byte[] image, Geo geo, List<(long Lcn, long Count)> runs, byte[] data) {
    var pos = 0;
    foreach (var (lcn, count) in runs) {
      var byteOff = (int)(lcn * geo.ClusterSize);
      var span = (int)Math.Min(count * geo.ClusterSize, data.Length - pos);
      if (span > 0) data.AsSpan(pos, span).CopyTo(image.AsSpan(byteOff));
      // zero the cluster-tail slack of the final run
      var runBytes = (int)(count * geo.ClusterSize);
      if (span < runBytes) image.AsSpan(byteOff + span, runBytes - span).Clear();
      pos += span;
    }
  }

  // ── FILE record construction (mirrors NtfsWriter exactly) ─────────────────────

  private static byte[] BuildFileRecord(Geo geo, uint recordNum, string name, uint parent,
      byte[]? residentData, List<(long Lcn, long Count)>? runs, long dataSize) {
    var record = new byte[geo.MftRecordSize];
    var usaCount = 1 + geo.MftRecordSize / geo.BytesPerSector;
    const int attrStart = 56;

    record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 42);                 // USA offset
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)usaCount);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);                 // sequence
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 1);                 // hard link count
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), attrStart);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 0x01);              // in-use, file
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)geo.MftRecordSize);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), recordNum);

    var pos = attrStart;
    pos = WriteStdInfo(record, pos);
    pos = WriteFileName(record, pos, name, parent, dataSize);
    pos = residentData != null
      ? WriteResidentData(record, pos, residentData)
      : WriteNonResidentData(record, pos, geo, runs!, dataSize);

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0xFFFFFFFF);
    pos += 4;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)pos);         // used size

    WriteUsaFixup(record, geo);
    return record;
  }

  private static int WriteStdInfo(byte[] record, int pos, bool isDirectory = false) {
    const int valueLen = 48;
    var attrLen = (24 + valueLen + 7) & ~7;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x10);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);
    var now = DateTime.UtcNow.ToFileTimeUtc();
    var v = pos + 24;
    for (var t = 0; t < 4; t++) BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + t * 8), now);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(v + 32), isDirectory ? 0x10u : 0x80u); // DIRECTORY / NORMAL
    return pos + attrLen;
  }

  private static int WriteFileName(byte[] record, int pos, string name, uint parent, long size, bool isDirectory = false) {
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var valueLen = 66 + name.Length * 2;
    var attrLen = (24 + valueLen + 7) & ~7;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x30);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 12] = 1;                                                            // resident_flags = INDEXED
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);
    var v = pos + 24;
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v), (long)parent | (1L << 48));
    var now = DateTime.UtcNow.ToFileTimeUtc();
    for (var t = 0; t < 4; t++) BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 8 + t * 8), now);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 40), size);            // allocated
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 48), size);            // real
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(v + 56), isDirectory ? 0x10000000u : 0u); // DIRECTORY flag
    record[v + 64] = (byte)name.Length;
    record[v + 65] = 1;                                                              // Win32 namespace
    nameBytes.CopyTo(record, v + 66);
    return pos + attrLen;
  }

  private static int WriteResidentData(byte[] record, int pos, byte[] data) {
    var attrLen = (24 + data.Length + 7) & ~7;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)data.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);
    data.CopyTo(record, pos + 24);
    return pos + attrLen;
  }

  private static int WriteNonResidentData(byte[] record, int pos, Geo geo,
      List<(long Lcn, long Count)> runs, long dataSize) {
    var dataRuns = EncodeDataRuns(runs);
    const int dataRunsOffset = 64;
    var attrLen = (dataRunsOffset + dataRuns.Length + 7) & ~7;
    long totalClusters = 0;
    foreach (var (_, c) in runs) totalClusters += c;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 1;                                                             // non-resident
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 24), totalClusters - 1); // last VCN
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 32), dataRunsOffset);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 40), totalClusters * geo.ClusterSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 48), dataSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 56), dataSize);
    dataRuns.CopyTo(record, pos + dataRunsOffset);
    return pos + attrLen;
  }

  // ── Root $INDEX_ROOT / $INDEX_ALLOCATION entry insertion ─────────────────────

  // Inserts (recordNum, name) into a directory's $I30 index, choosing the
  // representation that fits: a resident $INDEX_ROOT while the entries are small,
  // or a spilled pointer-form $INDEX_ROOT + non-resident $INDEX_ALLOCATION (a
  // single INDX leaf) + $BITMAP once they outgrow the MFT record. Subsequent adds
  // into an already-spilled directory re-pack the leaf in place.
  private static void InsertRootIndexEntry(byte[] image, Geo geo, uint recordNum, string name)
    => InsertIndexEntry(image, geo, RootRecord, recordNum, name);

  private static void InsertIndexEntry(byte[] image, Geo geo, uint dirRecord, uint recordNum, string name) {
    // A directory record may live in a non-contiguous grown MFT run, so its byte
    // offset must come from the $MFT:$DATA VCN→LCN mapping, not slot*recordSize.
    var dirOff = (int)MftRecordOffset(image, geo, (int)dirRecord);
    var dir = image.AsSpan(dirOff, geo.MftRecordSize).ToArray();
    ApplyFixup(dir);

    var (rootPos, _) = FindAttr(dir, 0x90, unnamedOnly: false);
    if (rootPos < 0) throw new InvalidDataException("NTFS: directory $INDEX_ROOT not found.");

    // Gather the directory's current leaf entries (name + record), regardless of
    // whether the index is resident or already spilled into $INDEX_ALLOCATION.
    var entries = CollectDirectoryLeafEntries(image, geo, dir, rootPos);
    entries.Add((name, recordNum));
    entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    // Try resident first: rebuild record `dirRecord` with all entries inline.
    if (TryWriteResidentIndexRoot(image, geo, dirRecord, dir, rootPos, entries))
      return;

    // Otherwise spill (or re-pack the existing spill) into a single INDX leaf.
    WriteSpilledIndex(image, geo, dirRecord, dir, rootPos, entries);
  }

  // Reads every FILE_NAME leaf entry (record + name) the directory's $I30 index
  // currently holds. For a resident $INDEX_ROOT the entries live inline; for a
  // spilled index they live in the INDX leaf blocks of $INDEX_ALLOCATION.
  private static List<(string Name, uint Record)> CollectDirectoryLeafEntries(
      byte[] image, Geo geo, byte[] dir, int rootPos) {
    var result = new List<(string Name, uint Record)>();

    // The $INDEX_ROOT attribute is ALWAYS resident; "spilled" is signalled by a
    // non-resident $INDEX_ALLOCATION (type 0xA0) — the resident root then holds
    // only routing pointers and the real FILE_NAME entries live in the INDX leaves.
    var (allocPos, _) = FindAttr(dir, 0xA0, unnamedOnly: false);
    if (allocPos < 0) {
      // Resident index: walk the inline entries.
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(rootPos + 20));
      var valStart = rootPos + valueOffset;
      var nodeHdr = valStart + 16;
      var entriesStart = nodeHdr + BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(nodeHdr));
      var usedEnd = nodeHdr + BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(nodeHdr + 4));
      CollectLeafEntriesFrom(dir, entriesStart, usedEnd, result);
      return result;
    }

    var runsOff = allocPos + BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(allocPos + 32));
    var runs = DecodeDataRuns(dir, runsOff);
    var blockSize = ReadIndexBlockSize(dir, rootPos);
    foreach (var (lcn, count) in runs) {
      var runBytes = count * geo.ClusterSize;
      for (long b = 0; b * blockSize < runBytes; b++) {
        var off = (int)(lcn * geo.ClusterSize + b * blockSize);
        if (off < 0 || off + blockSize > image.Length) continue;
        CollectLeafEntriesFromIndxBlock(image, off, blockSize, result);
      }
    }
    return result;
  }

  private static void CollectLeafEntriesFrom(byte[] buf, int start, int end, List<(string, uint)> result) {
    var p = start;
    while (p + 16 <= end && p + 16 <= buf.Length) {
      var entryLen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 8));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 12));
      if (entryLen < 16) break;
      if ((flags & 0x02) != 0) break; // end marker
      var mftRef = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(p)) & 0x0000FFFFFFFFFFFF;
      var keyNameLen = buf[p + 16 + 64];
      var entryName = Encoding.Unicode.GetString(buf, p + 16 + 66, keyNameLen * 2);
      if (mftRef > 0) result.Add((entryName, (uint)mftRef));
      p += entryLen;
    }
  }

  private static void CollectLeafEntriesFromIndxBlock(byte[] image, int blockOff, int blockSize, List<(string, uint)> result) {
    var block = image.AsSpan(blockOff, blockSize).ToArray();
    if (block[0] != 'I' || block[1] != 'N' || block[2] != 'D' || block[3] != 'X') return;
    ApplyFixupGeneric(block);
    // INDEX_HEADER sits at the fixed offset 24 (right after index_block_vcn);
    // entries_offset / index_length are relative to that sub-header start.
    const int subHeader = 24;
    if (subHeader + 16 > block.Length) return;
    var entriesStart = subHeader + BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(subHeader));
    var contentEnd = subHeader + BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(subHeader + 4));
    CollectLeafEntriesFrom(block, entriesStart, contentEnd, result);
  }

  // Bytes-per-INDX-block from the directory's index-root header (value offset 8).
  private static int ReadIndexBlockSize(byte[] dir, int rootPos) {
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(rootPos + 20));
    var size = BinaryPrimitives.ReadInt32LittleEndian(dir.AsSpan(rootPos + valueOffset + 8));
    return size > 0 ? size : 4096;
  }

  // Attempts to rebuild the directory record with a resident $INDEX_ROOT holding
  // all entries inline. Returns false (leaving the image untouched) when they no
  // longer fit, signalling the caller to spill.
  private static bool TryWriteResidentIndexRoot(byte[] image, Geo geo, uint dirRecord, byte[] dir,
      int rootPos, List<(string Name, uint Record)> entries) {
    using var es = new MemoryStream();
    foreach (var (n, r) in entries) es.Write(BuildIndexEntry(r, n));
    es.Write(BuildEndMarker());
    var entriesData = es.ToArray();

    var rootHeader = ReadOrBuildIndexRootHeader(dir, rootPos, indexBlockSize: 0);
    var valueLen = rootHeader.Length + 16 /*node hdr*/ + entriesData.Length;
    var nameLenChars = dir[rootPos + 9];
    var valueOffset = (24 + nameLenChars * 2 + 7) & ~7;
    var attrLen = (valueOffset + valueLen + 7) & ~7;

    // The prefix before $INDEX_ROOT plus this attribute plus the end marker must
    // fit in the record. ($INDEX_ROOT is the last attribute in a resident dir.)
    if (rootPos + attrLen + 8 > geo.MftRecordSize) return false;

    var rebuilt = new byte[geo.MftRecordSize];
    dir.AsSpan(0, rootPos).CopyTo(rebuilt);
    WriteIndexRootAttrHeader(rebuilt, rootPos, attrLen, nameLenChars, valueOffset, valueLen);
    CopyAttrName(dir, rootPos, rebuilt, valueOffset);
    var vp = rootPos + valueOffset;
    rootHeader.CopyTo(rebuilt.AsSpan(vp));
    var nhdr = vp + rootHeader.Length;
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr), 16);
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr + 4), 16 + entriesData.Length);
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr + 8), 16 + entriesData.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(nhdr + 12), 0); // small index
    entriesData.CopyTo(rebuilt, nhdr + 16);

    var endPos = rootPos + attrLen;
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(endPos), 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(24), (uint)(endPos + 8));
    SetDirectoryFlag(rebuilt);
    WriteUsaFixup(rebuilt, geo);
    rebuilt.CopyTo(image, (int)MftRecordOffset(image, geo, (int)dirRecord));
    return true;
  }

  // Spills (or re-packs an existing spill of) the directory index into a single
  // INDX leaf block: a pointer-form $INDEX_ROOT (one end-marker pointer at VCN 0),
  // a non-resident $INDEX_ALLOCATION (type 0xA0, named "$I30") holding the leaf,
  // and a resident $BITMAP (type 0xB0, named "$I30"). The INDX block size is grown
  // (power-of-two, ≥ one cluster, ≤ 64 KiB) until all entries fit one leaf.
  private static void WriteSpilledIndex(byte[] image, Geo geo, uint dirRecord, byte[] dir,
      int rootPos, List<(string Name, uint Record)> entries) {
    // Build the leaf entry stream once to size the block.
    using var es = new MemoryStream();
    foreach (var (n, r) in entries) es.Write(BuildIndexEntry(r, n));
    es.Write(BuildEndMarker());
    var entryStream = es.ToArray();

    // Choose an INDX block size that fits the whole entry stream in one leaf.
    var blockSize = 0;
    for (var size = Math.Max(4096, geo.ClusterSize); size <= 64 * 1024; size *= 2) {
      var usaBytes = (1 + size / geo.BytesPerSector) * 2;
      var subHeaderOffset = (24 + usaBytes + 7) & ~7;
      var capacity = size - (subHeaderOffset + 16);
      if (entryStream.Length <= capacity) { blockSize = size; break; }
    }
    if (blockSize == 0)
      throw new NotSupportedException(
        $"NTFS in-place add: directory index too large for a single INDX leaf ({entryStream.Length} bytes).");

    var clustersPerBlock = Math.Max(1, blockSize / geo.ClusterSize);

    // Reuse the existing $INDEX_ALLOCATION clusters if the directory is already
    // spilled and they're big enough; otherwise allocate fresh ones.
    long allocLcn;
    long allocClusters;
    var (existingAllocPos, _) = FindAttr(dir, 0xA0, unnamedOnly: false);
    if (existingAllocPos >= 0) {
      var allocPos = existingAllocPos;
      var runsOff = allocPos + BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(allocPos + 32));
      var existingRuns = DecodeDataRuns(dir, runsOff);
      long existingClusters = 0; foreach (var (_, c) in existingRuns) existingClusters += c;
      if (existingRuns.Count == 1 && existingClusters >= clustersPerBlock) {
        allocLcn = existingRuns[0].Lcn;
        allocClusters = clustersPerBlock;
      } else {
        // Free old + allocate a fresh contiguous run (the block size grew).
        foreach (var (lcn, count) in existingRuns) FreeClusters(image, geo, lcn, count);
        allocLcn = AllocateContiguousClusters(image, geo, (int)clustersPerBlock);
        allocClusters = clustersPerBlock;
      }
    } else {
      allocLcn = AllocateContiguousClusters(image, geo, (int)clustersPerBlock);
      allocClusters = clustersPerBlock;
    }

    // Build and write the single INDX leaf at VCN 0.
    var leaf = BuildIndxLeaf(geo, entryStream, blockSize, vcn: 0);
    var leafByteOff = (int)(allocLcn * geo.ClusterSize);
    if (leafByteOff + blockSize > image.Length) throw new IOException("NTFS in-place add: INDX leaf out of range.");
    image.AsSpan(leafByteOff, blockSize).Clear();
    leaf.CopyTo(image, leafByteOff);

    // Pointer $INDEX_ROOT: a lone end-marker entry with the subnode flag pointing
    // at VCN 0 (the only leaf). 24-byte entry: header(16) + child VCN(8).
    var rootHeader = ReadOrBuildIndexRootHeader(dir, rootPos, indexBlockSize: blockSize);
    rootHeader[12] = (byte)Math.Max(1, blockSize / geo.ClusterSize); // clusters per INDX block
    var pointerEntries = new byte[24];
    BinaryPrimitives.WriteUInt16LittleEndian(pointerEntries.AsSpan(8), 24);   // entry length
    BinaryPrimitives.WriteUInt16LittleEndian(pointerEntries.AsSpan(12), 0x03); // last + has subnode
    BinaryPrimitives.WriteInt64LittleEndian(pointerEntries.AsSpan(16), 0);     // child VCN 0

    var valueLen = rootHeader.Length + 16 + pointerEntries.Length;
    var nameLenChars = dir[rootPos + 9];
    var valueOffset = (24 + nameLenChars * 2 + 7) & ~7;
    var rootAttrLen = (valueOffset + valueLen + 7) & ~7;

    // $INDEX_ALLOCATION (0xA0, named "$I30"): non-resident, single run.
    var allocName = Encoding.Unicode.GetBytes("$I30");
    var allocNameOff = 64;
    var allocRunsOff = (allocNameOff + allocName.Length + 7) & ~7;
    var allocRuns = EncodeDataRuns([(allocLcn, allocClusters)]);
    var allocAttrLen = (allocRunsOff + allocRuns.Length + 7) & ~7;

    // $BITMAP (0xB0, named "$I30"): resident, one bit per leaf → 8 bytes minimum.
    var bmName = Encoding.Unicode.GetBytes("$I30");
    var bmValueOff = (24 + bmName.Length + 7) & ~7;
    var bitmap = new byte[8];
    bitmap[0] = 0x01; // leaf 0 allocated
    var bmAttrLen = (bmValueOff + bitmap.Length + 7) & ~7;

    var total = rootPos + rootAttrLen + allocAttrLen + bmAttrLen + 8;
    if (total > geo.MftRecordSize)
      throw new NotSupportedException(
        $"NTFS in-place add: spilled index attributes don't fit the directory record ({total}>{geo.MftRecordSize}).");

    var rebuilt = new byte[geo.MftRecordSize];
    dir.AsSpan(0, rootPos).CopyTo(rebuilt);

    // 0x90 pointer $INDEX_ROOT.
    WriteIndexRootAttrHeader(rebuilt, rootPos, rootAttrLen, nameLenChars, valueOffset, valueLen);
    CopyAttrName(dir, rootPos, rebuilt, valueOffset);
    var vp = rootPos + valueOffset;
    rootHeader.CopyTo(rebuilt.AsSpan(vp));
    var nhdr = vp + rootHeader.Length;
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr), 16);
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr + 4), 16 + pointerEntries.Length);
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr + 8), 16 + pointerEntries.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(nhdr + 12), 1); // LARGE_INDEX
    pointerEntries.CopyTo(rebuilt, nhdr + 16);

    // 0xA0 $INDEX_ALLOCATION.
    var ap = rootPos + rootAttrLen;
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(ap), 0xA0);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(ap + 4), (uint)allocAttrLen);
    rebuilt[ap + 8] = 1; // non-resident
    rebuilt[ap + 9] = (byte)(allocName.Length / 2);
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(ap + 10), (ushort)allocNameOff);
    BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(ap + 24), allocClusters - 1); // last VCN
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(ap + 32), (ushort)allocRunsOff);
    BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(ap + 40), allocClusters * geo.ClusterSize); // allocated
    BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(ap + 48), (long)blockSize); // real size = one leaf
    BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(ap + 56), (long)blockSize); // initialised
    allocName.CopyTo(rebuilt, ap + allocNameOff);
    allocRuns.CopyTo(rebuilt, ap + allocRunsOff);

    // 0xB0 $BITMAP.
    var bp = ap + allocAttrLen;
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(bp), 0xB0);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(bp + 4), (uint)bmAttrLen);
    rebuilt[bp + 8] = 0; // resident
    rebuilt[bp + 9] = (byte)(bmName.Length / 2);
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(bp + 10), 24);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(bp + 16), (uint)bitmap.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(bp + 20), (ushort)bmValueOff);
    bmName.CopyTo(rebuilt, bp + 24);
    bitmap.CopyTo(rebuilt, bp + bmValueOff);

    var endPos = bp + bmAttrLen;
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(endPos), 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(24), (uint)(endPos + 8));
    SetDirectoryFlag(rebuilt);
    WriteUsaFixup(rebuilt, geo);
    rebuilt.CopyTo(image, (int)MftRecordOffset(image, geo, (int)dirRecord));

    if (dirRecord <= 3) SyncMftMirror(image, geo);
  }

  // Builds one INDX leaf block. ntfs-3g's INDEX_BLOCK layout is fixed:
  //   [0]  magic "INDX"
  //   [4]  usa_ofs, [6] usa_count
  //   [8]  LSN, [16] index_block_vcn          → NTFS_RECORD + vcn = 24 bytes
  //   [24] INDEX_HEADER: entries_offset, index_length, allocated_size, flags
  //   [40] update-sequence array (usa_ofs = 0x28), then the entry stream.
  // ntfs-3g reads allocated_size at the FIXED offset 32 and requires
  // allocated_size + 0x18 == index_block_size, so the sub-header MUST sit at 24.
  private static byte[] BuildIndxLeaf(Geo geo, byte[] entryStream, int blockSize, long vcn) {
    var block = new byte[blockSize];
    const int subHeaderOffset = 24;            // INDEX_HEADER immediately after the VCN
    const int usaOffset = 40;                  // 0x28: USA starts after the 16-byte sub-header
    var usaCount = 1 + blockSize / geo.BytesPerSector;
    var usaBytes = usaCount * 2;
    var entriesStart = (usaOffset + usaBytes + 7) & ~7;

    block[0] = (byte)'I'; block[1] = (byte)'N'; block[2] = (byte)'D'; block[3] = (byte)'X';
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(4), usaOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(6), (ushort)usaCount);
    BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(16), vcn);

    var entriesRel = entriesStart - subHeaderOffset;        // entries_offset is relative to sub-header
    var totalSize = entriesRel + entryStream.Length;
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(subHeaderOffset), entriesRel);              // entries_offset
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(subHeaderOffset + 4), totalSize);            // index_length
    BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(subHeaderOffset + 8), blockSize - subHeaderOffset); // allocated_size
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(subHeaderOffset + 12), 0);                  // flags: leaf
    entryStream.CopyTo(block, entriesStart);

    ApplyIndxUsaFixup(block, geo.BytesPerSector, usaOffset);
    return block;
  }

  private static void ApplyIndxUsaFixup(byte[] block, int bytesPerSector, int usaOffset) {
    const ushort usn = 0x0001;
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(usaOffset), usn);
    var sectors = block.Length / bytesPerSector;
    for (var s = 0; s < sectors; s++) {
      var sectorEnd = s * bytesPerSector + bytesPerSector - 2;
      var usaSlot = usaOffset + 2 + s * 2;
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(usaSlot), BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(sectorEnd)));
      BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(sectorEnd), usn);
    }
  }

  // 16-byte $INDEX_ROOT header (attr type, collation, bytes-per-block, clusters-
  // per-block). Reused from the existing root when present so the collation rule
  // and block size carry over; otherwise built fresh for a FILE_NAME index.
  private static byte[] ReadOrBuildIndexRootHeader(byte[] dir, int rootPos, int indexBlockSize) {
    var header = new byte[16];
    if (dir[rootPos] == 0x90) {
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(rootPos + 20));
      dir.AsSpan(rootPos + valueOffset, 16).CopyTo(header);
    } else {
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x30); // FILE_NAME key
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);     // FILENAME collation
    }
    if (indexBlockSize > 0) {
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)indexBlockSize);
      header[12] = 1; // clusters-per-block; the spill path overwrites this with the real value
    }
    return header;
  }

  private static void WriteIndexRootAttrHeader(byte[] rec, int pos, int attrLen, int nameLenChars,
      int valueOffset, int valueLen) {
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos), 0x90);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos + 4), (uint)attrLen);
    rec[pos + 8] = 0;                       // resident
    rec[pos + 9] = (byte)nameLenChars;       // name length (4 = "$I30")
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos + 10), 24); // name offset
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(pos + 16), (uint)valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(pos + 20), (ushort)valueOffset);
  }

  // Copies the "$I30" attribute name (between header and value) into the rebuilt
  // record, using the source record's name when available or synthesising it.
  private static void CopyAttrName(byte[] src, int srcPos, byte[] dst, int valueOffset) {
    var nameLenChars = src[srcPos + 9];
    if (nameLenChars > 0 && src[srcPos] == 0x90) {
      var srcNameOff = BinaryPrimitives.ReadUInt16LittleEndian(src.AsSpan(srcPos + 10));
      src.AsSpan(srcPos + srcNameOff, nameLenChars * 2).CopyTo(dst.AsSpan(srcPos + 24));
    } else if (nameLenChars > 0) {
      Encoding.Unicode.GetBytes("$I30").CopyTo(dst.AsSpan(srcPos + 24));
    }
  }

  private static void SetDirectoryFlag(byte[] record)
    => BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 0x03); // in-use + directory

  private static byte[] BuildIndexEntry(uint recordNum, string name) {
    var nameBytes = Encoding.Unicode.GetBytes(name);
    var keyLen = 66 + name.Length * 2;
    var entryLen = (16 + keyLen + 7) & ~7;
    var e = new byte[entryLen];
    BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(0), (long)recordNum | (1L << 48)); // MFT ref (seq 1)
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(8), (ushort)entryLen);
    BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(10), (ushort)keyLen);
    // flags @12 = 0 (leaf, not last)
    // key = $FILE_NAME: parent ref @0, timestamps, sizes, name. ntfs-3g only needs
    // parent ref + name + namespace for index lookups; fill the rest coherently.
    var k = 16;
    BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(k), (long)RootRecord | (1L << 48));
    var now = DateTime.UtcNow.ToFileTimeUtc();
    for (var t = 0; t < 4; t++) BinaryPrimitives.WriteInt64LittleEndian(e.AsSpan(k + 8 + t * 8), now);
    e[k + 64] = (byte)name.Length;
    e[k + 65] = 1;
    nameBytes.CopyTo(e, k + 66);
    return e;
  }

  private static byte[] BuildEndMarker() {
    var m = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(m.AsSpan(12), 0x02);
    return m;
  }

  // Clears `count` cluster bits in $Bitmap starting at `lcn` (used when re-spilling
  // grows the INDX block size and the old allocation must be released).
  private static void FreeClusters(byte[] image, Geo geo, long lcn, long count) {
    var (bmByteOffset, _) = LocateClusterBitmap(image, geo);
    for (long c = lcn; c < lcn + count; c++) {
      var bOff = bmByteOffset + (int)(c / 8);
      if (bOff < 0 || bOff >= image.Length) continue;
      image[bOff] &= (byte)~(1 << (int)(c % 8));
    }
  }

  // USA fixup for an INDX (or any) block where the record size may exceed 512:
  // sectors are bytesPerSector wide. Used when re-reading INDX leaves.
  private static void ApplyFixupGeneric(byte[] block) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(6));
    if (usaOffset + usaCount * 2 > block.Length || usaCount < 2) return;
    var usn = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(usaOffset));
    // Sector stride is (block length / (usaCount-1)).
    var stride = block.Length / (usaCount - 1);
    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * stride - 2;
      if (sectorEnd + 2 > block.Length) break;
      if (BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(sectorEnd)) != usn) continue;
      block.AsSpan(usaOffset + i * 2, 2).CopyTo(block.AsSpan(sectorEnd));
    }
  }

  // ── Shared primitives ─────────────────────────────────────────────────────────

  private static (int Pos, int Len) FindAttr(byte[] record, uint type, bool unnamedOnly) {
    var first = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));
    var pos = (int)first;
    while (pos + 16 <= used && pos + 16 <= record.Length) {
      var t = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
      if (t == 0xFFFFFFFF) break;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
      if (len < 16 || pos + len > record.Length) break;
      if (t == type && (!unnamedOnly || record[pos + 9] == 0)) return (pos, len);
      pos += len;
    }
    return (-1, 0);
  }

  private static long FirstRunLcn(byte[] record, int runsOffset) {
    var header = record[runsOffset];
    var lengthBytes = header & 0x0F;
    var offsetBytes = (header >> 4) & 0x0F;
    long lcn = 0;
    var o = runsOffset + 1 + lengthBytes;
    for (var i = 0; i < offsetBytes; i++) lcn |= (long)record[o + i] << (i * 8);
    if (offsetBytes > 0 && (record[o + offsetBytes - 1] & 0x80) != 0)
      for (var i = offsetBytes; i < 8; i++) lcn |= (long)0xFF << (i * 8);
    return lcn;
  }

  private static byte[] EncodeDataRuns(List<(long Lcn, long Count)> runs) {
    using var ms = new MemoryStream();
    long prev = 0;
    foreach (var (lcn, count) in runs) {
      var offset = lcn - prev;
      var lenB = FieldBytes(count, false);
      var offB = FieldBytes(offset, true);
      ms.WriteByte((byte)((offB << 4) | lenB));
      for (var i = 0; i < lenB; i++) ms.WriteByte((byte)(count >> (i * 8)));
      for (var i = 0; i < offB; i++) ms.WriteByte((byte)(offset >> (i * 8)));
      prev = lcn;
    }
    ms.WriteByte(0);
    return ms.ToArray();
  }

  private static int FieldBytes(long value, bool signed) {
    if (value == 0) return signed ? 0 : 1;
    if (!signed) return value <= 0xFF ? 1 : value <= 0xFFFF ? 2 : value <= 0xFFFFFF ? 3 : 4;
    if (value >= -128 && value <= 127) return 1;
    if (value >= -32768 && value <= 32767) return 2;
    if (value >= -8388608 && value <= 8388607) return 3;
    return 4;
  }

  // Reverse the USA fixup (for parsing a copy) — identical to NtfsRemover.
  private static void ApplyFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaOffset + usaCount * 2 > record.Length || usaCount < 2) return;
    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;
      if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd)) != usn) continue;
      record.AsSpan(usaOffset + i * 2, 2).CopyTo(record.AsSpan(sectorEnd));
    }
  }

  // Apply the forward USA fixup (for writing a record) — mirrors NtfsWriter.ApplyUsaFixup.
  private static void WriteUsaFixup(byte[] record, Geo geo) {
    const ushort usn = 0x0001;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(42), usn);
    var sectors = geo.MftRecordSize / geo.BytesPerSector;
    for (var s = 0; s < sectors; s++) {
      var sectorEnd = s * geo.BytesPerSector + 510;
      var usaSlot = 44 + s * 2;
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaSlot), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd)));
      BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sectorEnd), usn);
    }
  }
}

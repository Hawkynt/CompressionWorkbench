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
/// Cases the in-place path does not yet handle throw so the caller can fall back to the
/// verified rebuild: nested sub-directory targets, a root index that would spill out of
/// the resident <c>$INDEX_ROOT</c>, no free MFT slot, or no free data clusters.
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
    if (name.Contains('/') || name.Contains('\\'))
      throw new NotSupportedException("NTFS in-place add: nested sub-directory targets use rebuild.");

    var geo = ParseBoot(image);

    // Replace-by-name: drop any prior entry (frees its record + clusters + index entry).
    try { NtfsRemover.Remove(image, name); } catch (FileNotFoundException) { /* new file */ }

    // Decide $DATA residency: small files live inside the MFT record (resident).
    var resident = data.Length <= geo.ResidentThreshold;
    List<(long Lcn, long Count)> dataRuns = [];
    if (!resident) {
      var clustersNeeded = (data.Length + geo.ClusterSize - 1) / geo.ClusterSize;
      dataRuns = AllocateClusters(image, geo, clustersNeeded);
      WriteDataToRuns(image, geo, dataRuns, data);
    }

    var slot = FindFreeMftRecord(image, geo);
    if (slot < 0)
      // MFT growth requires a contiguous MFT-zone reservation in NtfsWriter and
      // non-contiguous-MFT support in NtfsReader (a non-contiguous appended run is
      // unreadable today). Until both land, refuse rather than emit a record the
      // readers can't follow. Tracked as follow-up; caller may rebuild.
      throw new NotSupportedException(
        "NTFS in-place add: MFT is full (growth requires a reserved contiguous MFT zone — not yet implemented).");
    var fileRecord = BuildFileRecord(geo, (uint)slot, name, RootRecord, resident ? data : null,
      resident ? null : dataRuns, data.Length);

    var recordOffset = (int)MftRecordOffset(image, geo, slot);
    fileRecord.CopyTo(image, recordOffset);

    SetMftBitmapBit(image, geo, slot);
    ExtendMftDataSize(image, geo, slot);
    InsertRootIndexEntry(image, geo, (uint)slot, name);
    SyncMftMirror(image, geo); // record 0 (sizes/$BITMAP) changed — keep $MFTMirr identical
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

  private static int WriteStdInfo(byte[] record, int pos) {
    const int valueLen = 48;
    var attrLen = (24 + valueLen + 7) & ~7;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x10);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);
    var now = DateTime.UtcNow.ToFileTimeUtc();
    var v = pos + 24;
    for (var t = 0; t < 4; t++) BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + t * 8), now);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(v + 32), 0x80u);          // FILE_ATTRIBUTE_NORMAL
    return pos + attrLen;
  }

  private static int WriteFileName(byte[] record, int pos, string name, uint parent, long size) {
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

  // ── Root $INDEX_ROOT entry insertion (collation-sorted, resident only) ────────

  private static void InsertRootIndexEntry(byte[] image, Geo geo, uint recordNum, string name) {
    var rootOff = (int)(geo.MftOffset + RootRecord * geo.MftRecordSize);
    var root = image.AsSpan(rootOff, geo.MftRecordSize).ToArray();
    ApplyFixup(root);

    var (attrPos, attrLen) = FindAttr(root, 0x90, unnamedOnly: false);
    if (attrPos < 0) throw new InvalidDataException("NTFS: root $INDEX_ROOT not found.");
    if (root[attrPos + 8] != 0)
      throw new NotSupportedException("NTFS in-place add: root index is non-resident (spilled).");

    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(attrPos + 20));
    var valStart = attrPos + valueOffset;
    var valLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(attrPos + 16));

    // Index-root header (16) + index-node header (16) then entries.
    var nodeHdr = valStart + 16;
    var entriesOffset = BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(nodeHdr));
    var entriesStart = nodeHdr + entriesOffset;
    var usedEnd = nodeHdr + BinaryPrimitives.ReadInt32LittleEndian(root.AsSpan(nodeHdr + 4));

    // Collect existing leaf entries (name + 16-byte header + key), find the end marker.
    var existing = new List<(string Name, byte[] Bytes)>();
    var p = entriesStart;
    var endMarker = Array.Empty<byte>();
    while (p + 16 <= usedEnd && p + 16 <= root.Length) {
      var entryLen = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(p + 8));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(p + 12));
      if (entryLen < 16) break;
      var bytes = root.AsSpan(p, entryLen).ToArray();
      if ((flags & 0x02) != 0) { endMarker = bytes; break; }      // last entry (no key)
      var keyNameLen = root[p + 16 + 64];
      var entryName = Encoding.Unicode.GetString(root, p + 16 + 66, keyNameLen * 2);
      existing.Add((entryName, bytes));
      p += entryLen;
    }
    if (endMarker.Length == 0) endMarker = BuildEndMarker();

    // Build the new entry and merge in collation order (case-insensitive).
    var newEntry = BuildIndexEntry(recordNum, name);
    existing.Add((name, newEntry));
    existing.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    using var ms = new MemoryStream();
    foreach (var (_, b) in existing) ms.Write(b);
    ms.Write(endMarker);
    var entriesData = ms.ToArray();

    var newValLen = 16 /*index-root hdr*/ + 16 /*node hdr*/ + entriesData.Length;
    // Does the grown attribute still fit in the record (used size + delta)?
    var oldAttrLen = attrLen;
    var newAttrLen = (valueOffset + newValLen + 7) & ~7;
    var usedSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(root.AsSpan(24));
    if (usedSize - oldAttrLen + newAttrLen + 8 > geo.MftRecordSize)
      throw new NotSupportedException(
        $"NTFS in-place add: root index would overflow the MFT record (needs spill). " +
        $"used={usedSize} oldAttr={oldAttrLen} newAttr={newAttrLen} rec={geo.MftRecordSize} entries={existing.Count}");

    // Rebuild record 5: copy attributes up to $INDEX_ROOT, re-emit $INDEX_ROOT grown,
    // then the end marker. ($INDEX_ROOT is the last attribute in a resident root dir.)
    var rebuilt = new byte[geo.MftRecordSize];
    root.AsSpan(0, attrPos).CopyTo(rebuilt);                       // header + STD_INFO + FILE_NAME
    // $INDEX_ROOT attribute header (resident, named "$I30").
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(attrPos), 0x90);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(attrPos + 4), (uint)newAttrLen);
    rebuilt[attrPos + 9] = root[attrPos + 9];                      // name length (4 = "$I30")
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(attrPos + 10), BinaryPrimitives.ReadUInt16LittleEndian(root.AsSpan(attrPos + 10)));
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(attrPos + 16), (uint)newValLen);
    BinaryPrimitives.WriteUInt16LittleEndian(rebuilt.AsSpan(attrPos + 20), (ushort)valueOffset);
    // attribute name ("$I30") between header and value.
    root.AsSpan(attrPos + 24, valueOffset - 24).CopyTo(rebuilt.AsSpan(attrPos + 24));
    // index-root header (16) copied from the original, then fresh node header + entries.
    root.AsSpan(valStart, 16).CopyTo(rebuilt.AsSpan(attrPos + valueOffset));
    var nhdr = attrPos + valueOffset + 16;
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr), 16);                       // entries offset
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr + 4), 16 + entriesData.Length); // total size
    BinaryPrimitives.WriteInt32LittleEndian(rebuilt.AsSpan(nhdr + 8), 16 + entriesData.Length); // allocated
    entriesData.CopyTo(rebuilt, nhdr + 16);

    var endPos = attrPos + newAttrLen;
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(endPos), 0xFFFFFFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(24), (uint)(endPos + 4));

    // Carry over the header fields the prefix copy already brought (USA offset/count,
    // flags, etc.); only used-size and the index changed.
    WriteUsaFixup(rebuilt, geo);
    rebuilt.CopyTo(image, rootOff);
    _ = valLen;
  }

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

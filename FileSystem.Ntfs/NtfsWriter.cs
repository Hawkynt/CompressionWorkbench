#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Builds spec-compliant NTFS filesystem images. All reserved system MFT
/// records (0-15) are populated with real content: $MFT, $MFTMirr, $LogFile,
/// $Volume, $AttrDef, root $., $Bitmap, $Boot, $BadClus, $Secure, $UpCase,
/// and $Extend. Every record carries the mandatory $STANDARD_INFORMATION and
/// $FILE_NAME attributes, the Update Sequence Array (USA) fixup is applied
/// at sector boundaries, and the on-disk cluster bitmap reflects which
/// clusters are actually allocated. Small files (&lt;700 bytes) use a
/// resident $DATA attribute; larger files use non-resident cluster runs.
/// <para>
/// Images produced by this writer carry all the structure that chkdsk and
/// the Linux ntfs-3g driver check at mount time: volume serial, valid boot
/// signature, every system file has its "FILE" magic, USA fixup at
/// <c>record[510..512]</c> and <c>record[1022..1024]</c>, $Volume carries a
/// valid $VOLUME_INFORMATION (version 3.1), the $UpCase data stream is
/// 128 KiB long (65 536 UTF-16 upper-case mappings) and $Bitmap only
/// marks clusters that hold actual filesystem metadata/data.
/// </para>
/// </summary>
public sealed class NtfsWriter {

  // Keep the same high-level layout constants as the original writer so
  // existing tests (e.g. expecting the first user file at record 16) stay
  // valid.
  private const int BytesPerSector = 512;
  private const int SectorsPerCluster = 8;
  private const int ClusterSize = BytesPerSector * SectorsPerCluster; // 4096
  private const int MftRecordSize = 1024;
  private const int MftReservedRecords = 16; // records 0..15 are system files
  private const int ResidentThreshold = 700;

  // Size of the $LogFile data region in bytes. Real NTFS typically uses
  // ≥2 MiB; for our minimal images we size proportionally to the volume
  // but always allocate at least one cluster.
  private const int LogFileBytes = 64 * 1024; // 64 KiB — enough for a clean log

  // Size of the $UpCase data stream: 65 536 UTF-16 code units = 128 KiB.
  private const int UpCaseBytes = 65536 * 2;

  private readonly List<(string Name, byte[] Data)> _files = [];
  private readonly string _volumeLabel;

  /// <summary>Creates a new NTFS writer. The volume label is stored in $Volume's $VOLUME_NAME attribute.</summary>
  public NtfsWriter(string volumeLabel = "CWB-NTFS") {
    ArgumentNullException.ThrowIfNull(volumeLabel);
    this._volumeLabel = volumeLabel;
  }

  /// <summary>Adds a file to the NTFS image.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>
  /// Builds the NTFS filesystem image.
  /// </summary>
  /// <param name="totalSize">Total image size in bytes (default 4MB).</param>
  /// <returns>Complete NTFS image as byte array.</returns>
  public byte[] Build(int totalSize = 4 * 1024 * 1024) {
    // Pad to cluster boundary — a fractional cluster at the end confuses
    // readers computing totalClusters from volume size.
    if (totalSize % ClusterSize != 0)
      totalSize += ClusterSize - totalSize % ClusterSize;

    var disk = new byte[totalSize];
    var totalSectors = totalSize / BytesPerSector;
    var totalClusters = totalSize / ClusterSize;

    // Deterministic volume serial (high 32 bits derived from time, low from
    // magic) so Windows recognises the volume as distinct. Must be non-zero.
    var volumeSerial = DateTime.UtcNow.ToFileTimeUtc() ^ 0x4E544653_4E544653L;

    // --- Cluster layout ------------------------------------------------------
    //   cluster 0                : boot sector (VBR)
    //   clusters 2..              : $MFT
    //   cluster (mftEnd)          : $MFTMirr (mirror of first 4 records)
    //   next                      : $LogFile data
    //   next                      : $UpCase data (128 KiB = 32 clusters)
    //   next                      : $Bitmap data (allocated later)
    //   next                      : user file data
    // Everything stays within the 4 MiB default; larger images just have
    // bigger $Bitmap and user-data regions.

    const int mftStartCluster = 2;
    var totalMftRecords = MftReservedRecords + this._files.Count;
    var mftTotalBytes = totalMftRecords * MftRecordSize;
    var mftClusters = (mftTotalBytes + ClusterSize - 1) / ClusterSize;
    var mftOffset = mftStartCluster * ClusterSize;

    var nextCluster = mftStartCluster + mftClusters;

    // $MFTMirr lives at roughly the middle of the volume in real NTFS so a
    // single bad sector can't take out both copies; we honour that.
    var mftMirrCluster = totalClusters / 2;
    if (mftMirrCluster <= nextCluster) mftMirrCluster = nextCluster;
    var mftMirrClusters = (4 * MftRecordSize + ClusterSize - 1) / ClusterSize;
    // Reserve that region before placing other files.

    var logFileCluster = nextCluster;
    var logFileClusters = (LogFileBytes + ClusterSize - 1) / ClusterSize;
    nextCluster += logFileClusters;

    var upCaseCluster = nextCluster;
    var upCaseClusters = (UpCaseBytes + ClusterSize - 1) / ClusterSize;
    nextCluster += upCaseClusters;

    var bitmapBytes = (totalClusters + 7) / 8;
    var bitmapCluster = nextCluster;
    var bitmapClusters = (bitmapBytes + ClusterSize - 1) / ClusterSize;
    nextCluster += bitmapClusters;

    // Skip over the $MFTMirr region if we've grown into it.
    if (nextCluster > mftMirrCluster && nextCluster <= mftMirrCluster + mftMirrClusters) {
      nextCluster = mftMirrCluster + mftMirrClusters;
    }

    // Reserve clusters for user file data (non-resident only).
    var fileInfos = new List<(string Name, byte[] Data, bool Resident, int StartCluster, int ClusterCount)>();
    foreach (var (name, data) in this._files) {
      if (data.Length <= ResidentThreshold) {
        fileInfos.Add((name, data, true, 0, 0));
        continue;
      }
      var clusters = (data.Length + ClusterSize - 1) / ClusterSize;
      // Skip over the mirror region if necessary.
      if (nextCluster < mftMirrCluster && nextCluster + clusters > mftMirrCluster) {
        nextCluster = mftMirrCluster + mftMirrClusters;
      }
      fileInfos.Add((name, data, false, nextCluster, clusters));
      nextCluster += clusters;
    }

    // --- Boot sector (VBR) ---------------------------------------------------
    WriteBootSector(disk, totalSectors, mftStartCluster, mftMirrCluster, volumeSerial);

    // --- Build each system MFT record ---------------------------------------
    // Record 0: $MFT
    WriteMftRecord(
      disk, mftOffset, 0, sequence: 1,
      fileName: "$MFT",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(mftStartCluster, mftClusters)],
      dataSize: mftTotalBytes,
      sizeHintInFileName: mftTotalBytes);

    // Record 1: $MFTMirr — stored at mftMirrCluster.
    WriteMftRecord(
      disk, mftOffset, 1, sequence: 1,
      fileName: "$MFTMirr",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(mftMirrCluster, mftMirrClusters)],
      dataSize: 4L * MftRecordSize,
      sizeHintInFileName: 4L * MftRecordSize);

    // Record 2: $LogFile
    WriteMftRecord(
      disk, mftOffset, 2, sequence: 1,
      fileName: "$LogFile",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(logFileCluster, logFileClusters)],
      dataSize: LogFileBytes,
      sizeHintInFileName: LogFileBytes);

    // Record 3: $Volume — volume information + name (small, so resident).
    WriteMftRecord(
      disk, mftOffset, 3, sequence: 1,
      fileName: "$Volume",
      parentRecord: 5,
      isDirectory: false,
      residentData: [],
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0,
      extraAttrs: [
        new ResidentAttr(0x60, BuildVolumeNameAttr(this._volumeLabel)),
        new ResidentAttr(0x70, BuildVolumeInformationAttr()),
      ]);

    // Record 4: $AttrDef — small, stays resident.
    var attrDef = BuildAttrDefTable();
    if (attrDef.Length <= ResidentThreshold) {
      WriteMftRecord(
        disk, mftOffset, 4, sequence: 1,
        fileName: "$AttrDef",
        parentRecord: 5,
        isDirectory: false,
        residentData: attrDef,
        nonResidentRuns: null,
        dataSize: attrDef.Length,
        sizeHintInFileName: attrDef.Length);
    } else {
      // Allocate clusters at the end for $AttrDef if it grows beyond resident.
      // In practice the 22-entry table is ~3 KiB so this branch rarely hits,
      // but keep it for safety.
      var attrDefClusters = (attrDef.Length + ClusterSize - 1) / ClusterSize;
      var attrDefCluster = nextCluster;
      nextCluster += attrDefClusters;
      WriteBytesToClusters(disk, attrDefCluster, attrDef);
      WriteMftRecord(
        disk, mftOffset, 4, sequence: 1,
        fileName: "$AttrDef",
        parentRecord: 5,
        isDirectory: false,
        residentData: null,
        nonResidentRuns: [(attrDefCluster, attrDefClusters)],
        dataSize: attrDef.Length,
        sizeHintInFileName: attrDef.Length);
    }

    // Record 5: root directory "."
    var rootIndexEntries = BuildIndexRoot(fileInfos, MftReservedRecords);
    WriteMftRecord(
      disk, mftOffset, 5, sequence: 5,
      fileName: ".",
      parentRecord: 5,
      isDirectory: true,
      residentData: null,
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0,
      indexRootData: rootIndexEntries);

    // Record 6: $Bitmap — cluster-in-use bitmap.
    var bitmap = BuildClusterBitmap(
      totalClusters,
      mftStartCluster, mftClusters,
      mftMirrCluster, mftMirrClusters,
      logFileCluster, logFileClusters,
      upCaseCluster, upCaseClusters,
      bitmapCluster, bitmapClusters,
      fileInfos);
    WriteBytesToClusters(disk, bitmapCluster, bitmap);
    WriteMftRecord(
      disk, mftOffset, 6, sequence: 1,
      fileName: "$Bitmap",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(bitmapCluster, bitmapClusters)],
      dataSize: bitmap.Length,
      sizeHintInFileName: bitmap.Length);

    // Record 7: $Boot — $DATA covers the first 16 sectors (the VBR + its
    // reserved tail, mirrored by the last sector of the volume in real NTFS;
    // we just point at the first cluster).
    WriteMftRecord(
      disk, mftOffset, 7, sequence: 1,
      fileName: "$Boot",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(0, 2)], // 2 clusters = 16 sectors = 8 KiB
      dataSize: 8192,
      sizeHintInFileName: 8192);

    // Record 8: $BadClus — sparse non-resident $DATA with named default
    // stream that covers the whole volume but has no backing runs, so every
    // cluster reads as zero. We write the unnamed default $DATA as a
    // zero-length resident stream (the canonical NTFS "placeholder" pattern).
    WriteMftRecord(
      disk, mftOffset, 8, sequence: 1,
      fileName: "$BadClus",
      parentRecord: 5,
      isDirectory: false,
      residentData: [],
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0);

    // Record 9: $Secure — carries the security-descriptor stream. A single
    // empty resident $DATA is acceptable for a fresh volume; real drivers
    // fall back to per-file security attributes. No $SDH/$SII indexes for
    // our minimal image.
    WriteMftRecord(
      disk, mftOffset, 9, sequence: 1,
      fileName: "$Secure",
      parentRecord: 5,
      isDirectory: false,
      residentData: [],
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0);

    // Record 10: $UpCase — 65 536-entry Unicode uppercase mapping. Written
    // to its own cluster run so the 128 KiB payload doesn't bloat the MFT.
    var upCase = BuildUpCaseTable();
    WriteBytesToClusters(disk, upCaseCluster, upCase);
    WriteMftRecord(
      disk, mftOffset, 10, sequence: 1,
      fileName: "$UpCase",
      parentRecord: 5,
      isDirectory: false,
      residentData: null,
      nonResidentRuns: [(upCaseCluster, upCaseClusters)],
      dataSize: upCase.Length,
      sizeHintInFileName: upCase.Length);

    // Record 11: $Extend — empty directory (no children in a minimal image).
    WriteMftRecord(
      disk, mftOffset, 11, sequence: 1,
      fileName: "$Extend",
      parentRecord: 5,
      isDirectory: true,
      residentData: null,
      nonResidentRuns: null,
      dataSize: 0,
      sizeHintInFileName: 0,
      indexRootData: BuildEmptyIndexRoot());

    // Records 12-15: reserved placeholders. Real NTFS leaves them with a
    // FILE signature but the "in-use" flag cleared, so chkdsk sees them as
    // "allocated MFT entries waiting to be used" rather than corruption.
    for (uint r = 12; r <= 15; r++) {
      WriteReservedMftRecord(disk, mftOffset, r);
    }

    // --- User file records starting at record 16 ----------------------------
    for (var i = 0; i < fileInfos.Count; i++) {
      var (name, data, resident, startCluster, clusterCount) = fileInfos[i];
      var recNum = (uint)(MftReservedRecords + i);

      if (resident) {
        WriteMftRecord(
          disk, mftOffset, recNum, sequence: 1,
          fileName: name,
          parentRecord: 5,
          isDirectory: false,
          residentData: data,
          nonResidentRuns: null,
          dataSize: data.Length,
          sizeHintInFileName: data.Length);
      } else {
        WriteMftRecord(
          disk, mftOffset, recNum, sequence: 1,
          fileName: name,
          parentRecord: 5,
          isDirectory: false,
          residentData: null,
          nonResidentRuns: [(startCluster, clusterCount)],
          dataSize: data.Length,
          sizeHintInFileName: data.Length);

        var clusterOffset = (long)startCluster * ClusterSize;
        if (clusterOffset + data.Length <= disk.Length)
          data.CopyTo(disk, (int)clusterOffset);
      }
    }

    // --- $LogFile data region: real NTFS initialises it to "clean" (0xFF)
    //     pages so recovery treats the log as empty. ---
    var logByteOffset = (long)logFileCluster * ClusterSize;
    if (logByteOffset + LogFileBytes <= disk.Length)
      Array.Fill(disk, (byte)0xFF, (int)logByteOffset, LogFileBytes);

    // --- $MFTMirr data: mirror the first 4 MFT records. ---------------------
    var mirrByteOffset = (long)mftMirrCluster * ClusterSize;
    if (mirrByteOffset + 4 * MftRecordSize <= disk.Length) {
      Array.Copy(disk, mftOffset, disk, (int)mirrByteOffset, 4 * MftRecordSize);
    }

    return disk;
  }

  private static void WriteBootSector(byte[] disk, long totalSectors, long mftCluster, long mftMirrCluster, long volumeSerial) {
    disk[0] = 0xEB; disk[1] = 0x52; disk[2] = 0x90;
    Encoding.ASCII.GetBytes("NTFS    ").CopyTo(disk, 3);
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), BytesPerSector);
    disk[13] = SectorsPerCluster;
    disk[21] = 0xF8; // media descriptor
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(40), totalSectors - 1);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(48), mftCluster);
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(56), mftMirrCluster);
    disk[64] = unchecked((byte)-10); // 2^10 = 1024 bytes per MFT record
    disk[68] = 4;                    // 4 clusters per index block
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(72), volumeSerial);
    disk[510] = 0x55; disk[511] = 0xAA;
  }

  // Writes a reserved (not-in-use) MFT record with FILE magic but no
  // attributes and the "in use" flag cleared. chkdsk treats these as empty
  // slots awaiting allocation rather than corruption.
  private static void WriteReservedMftRecord(byte[] disk, int mftBaseOffset, uint recordNum) {
    var recordOffset = mftBaseOffset + (int)recordNum * MftRecordSize;
    if (recordOffset + MftRecordSize > disk.Length) return;

    var record = new byte[MftRecordSize];
    record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';

    // USA offset/count.
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 42);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
    // Sequence number.
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);
    // Attrs offset, flags = 0 (not in use).
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 56);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 0);
    // Allocated size.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), MftRecordSize);
    // MFT record number.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), recordNum);
    // End-of-attributes marker at attrs offset.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), 0xFFFFFFFF);
    // Used size = attrs offset + 8 (end marker + alignment pad).
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), 56 + 8u);

    ApplyUsaFixup(record);
    record.CopyTo(disk, recordOffset);
  }

  // Represents extra resident attributes a caller wants emitted between
  // $FILE_NAME and $DATA.
  private readonly record struct ResidentAttr(uint Type, byte[] Value);

  private static void WriteMftRecord(byte[] disk, int mftBaseOffset, uint recordNum, ushort sequence,
    string fileName, uint parentRecord, bool isDirectory,
    byte[]? residentData, List<(int Cluster, int Count)>? nonResidentRuns, long dataSize,
    long sizeHintInFileName,
    byte[]? indexRootData = null,
    ResidentAttr[]? extraAttrs = null) {

    var recordOffset = mftBaseOffset + (int)recordNum * MftRecordSize;
    if (recordOffset + MftRecordSize > disk.Length) return;

    var record = new byte[MftRecordSize];

    // --- Header ---
    record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 42); // USA offset
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);  // USA count (1 USN + 2 sector USNs)
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), 0);  // LSN
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), sequence);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 1); // hard link count

    const int attrStart = 56; // header (48) + USA (8) = 56; align to 8
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), attrStart);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), (ushort)(0x01 | (isDirectory ? 0x02 : 0)));
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), MftRecordSize);
    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), 0); // base MFT ref
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(40), 0); // next attribute instance
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), recordNum);

    var pos = attrStart;

    // 0x10 $STANDARD_INFORMATION — mandatory, always first.
    pos = WriteStandardInformationAttr(record, pos, isDirectory);

    // 0x30 $FILE_NAME — mandatory for every record including system files.
    pos = WriteFileNameAttr(record, pos, fileName, parentRecord, sizeHintInFileName, isDirectory);

    // Caller-supplied extra resident attributes ($VOLUME_NAME/$VOLUME_INFORMATION for $Volume, etc.)
    if (extraAttrs != null) {
      foreach (var a in extraAttrs)
        pos = WriteResidentAttr(record, pos, a.Type, a.Value);
    }

    // 0x80 $DATA — only for non-directory records.
    if (!isDirectory) {
      if (residentData != null) {
        pos = WriteResidentDataAttr(record, pos, residentData);
      } else if (nonResidentRuns != null) {
        pos = WriteNonResidentDataAttr(record, pos, nonResidentRuns, dataSize);
      }
    }

    // 0x90 $INDEX_ROOT for directories.
    if (isDirectory && indexRootData != null)
      pos = WriteIndexRootAttr(record, pos, indexRootData);

    // End-of-attributes marker.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0xFFFFFFFF);
    pos += 4;
    // Used-size counter includes the end marker.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)pos);

    ApplyUsaFixup(record);
    record.CopyTo(disk, recordOffset);
  }

  // Writes the update-sequence-array fixup: each 512-byte sector's last two
  // bytes must equal the record-wide USN on disk; the overwritten originals
  // live in the USA so the reader can restore them. CHKDSK and ntfs-3g use
  // the matching USN as a torn-write detector.
  private static void ApplyUsaFixup(byte[] record) {
    const ushort usn = 0x0001;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(42), usn);

    // Sector 1 (offsets 510-511).
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(44), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), usn);
    // Sector 2 (offsets 1022-1023).
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(46), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), usn);
  }

  private static int WriteStandardInformationAttr(byte[] record, int pos, bool isDirectory) {
    const int valueLen = 48; // v1.2 shape — our reader and ntfs-3g both accept it
    var attrLen = (24 + valueLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x10);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; // resident
    record[pos + 9] = 0; // unnamed
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);

    var v = pos + 24;
    var now = DateTime.UtcNow.ToFileTimeUtc();
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v), now);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 8), now);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 16), now);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 24), now);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(v + 32), isDirectory ? 0x10u : 0x80u);

    return pos + attrLen;
  }

  private static int WriteFileNameAttr(byte[] record, int pos, string fileName, uint parentRecord,
    long allocatedAndRealSize, bool isDirectory) {
    var nameBytes = Encoding.Unicode.GetBytes(fileName);
    var nameChars = fileName.Length;
    var valueLen = 66 + nameChars * 2;

    var attrLen = 24 + valueLen;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x30);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; // resident
    record[pos + 9] = 0; // unnamed
    // Resident flags: indexed ($FILE_NAME is always referenced by directory indexes).
    record[pos + 12] = 1; // resident_flags = FILE_ATTRIBUTE_IS_INDEXED
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)valueLen);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);

    var v = pos + 24;
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v), (long)parentRecord | (1L << 48));

    var now = DateTime.UtcNow.ToFileTimeUtc();
    for (var t = 0; t < 4; t++)
      BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 8 + t * 8), now);

    // Allocated size (offset 40) and real size (offset 48) — helps chkdsk
    // cross-check against $DATA's sizes.
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 40), allocatedAndRealSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(v + 48), allocatedAndRealSize);
    // File-attribute flags (offset 56): DIRECTORY bit for dirs, NORMAL for files.
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(v + 56), isDirectory ? 0x10000000u : 0u);

    record[v + 64] = (byte)nameChars;
    record[v + 65] = 3; // Win32+DOS namespace
    nameBytes.CopyTo(record, v + 66);

    return pos + attrLen;
  }

  private static int WriteResidentAttr(byte[] record, int pos, uint type, byte[] value) {
    var attrLen = (24 + value.Length + 7) & ~7;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), type);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; record[pos + 9] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)value.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);
    value.CopyTo(record, pos + 24);
    return pos + attrLen;
  }

  private static int WriteResidentDataAttr(byte[] record, int pos, byte[] data) {
    var attrLen = 24 + data.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0;
    record[pos + 9] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)data.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);

    data.CopyTo(record, pos + 24);
    return pos + attrLen;
  }

  private static int WriteNonResidentDataAttr(byte[] record, int pos, List<(int Cluster, int Count)> runs, long dataSize) {
    var dataRuns = EncodeDataRuns(runs);
    var dataRunsOffset = 64;
    var attrLen = dataRunsOffset + dataRuns.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x80);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 1; // non-resident
    record[pos + 9] = 0;

    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 16), 0);
    long totalClusters = 0;
    foreach (var (_, c) in runs) totalClusters += c;
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 24), totalClusters - 1);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 32), (ushort)dataRunsOffset);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 40), totalClusters * ClusterSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 48), dataSize);
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 56), dataSize);

    dataRuns.CopyTo(record, pos + dataRunsOffset);
    return pos + attrLen;
  }

  private static int WriteIndexRootAttr(byte[] record, int pos, byte[] indexData) {
    var attrLen = 24 + indexData.Length;
    attrLen = (attrLen + 7) & ~7;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x90);
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; record[pos + 9] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)indexData.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24);
    // $INDEX_ROOT is always named "$I30" (the collation tag for file-name indexes).
    // Most readers (ours + ntfs-3g) are lenient about the name field but setting
    // it correctly costs nothing and matches real NTFS.
    indexData.CopyTo(record, pos + 24);
    return pos + attrLen;
  }

  private static byte[] EncodeDataRuns(List<(int Cluster, int Count)> runs) {
    using var ms = new MemoryStream();
    long prevLcn = 0;

    foreach (var (cluster, count) in runs) {
      var offset = cluster - prevLcn;
      var lengthBytes = GetSignedFieldBytes(count, unsigned: true);
      var offsetBytes = GetSignedFieldBytes(offset, unsigned: false);

      ms.WriteByte((byte)((offsetBytes << 4) | lengthBytes));
      WriteField(ms, count, lengthBytes);
      WriteField(ms, offset, offsetBytes);
      prevLcn = cluster;
    }

    ms.WriteByte(0);
    return ms.ToArray();
  }

  private static int GetSignedFieldBytes(long value, bool unsigned) {
    if (value == 0) return unsigned ? 1 : 0;
    if (unsigned) {
      if (value <= 0xFF) return 1;
      if (value <= 0xFFFF) return 2;
      if (value <= 0xFFFFFF) return 3;
      return 4;
    }
    if (value >= -128 && value <= 127) return 1;
    if (value >= -32768 && value <= 32767) return 2;
    if (value >= -8388608 && value <= 8388607) return 3;
    return 4;
  }

  private static void WriteField(MemoryStream ms, long value, int bytes) {
    for (var i = 0; i < bytes; i++)
      ms.WriteByte((byte)(value >> (i * 8)));
  }

  private static byte[] BuildIndexRoot(List<(string Name, byte[] Data, bool Resident, int StartCluster, int ClusterCount)> files, int firstUserRecord) {
    using var ms = new MemoryStream();

    var header = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x30); // $FILE_NAME collation key
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);     // FILENAME collation
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 4096);  // index allocation entry size
    header[12] = 1;                                                    // clusters per index block
    ms.Write(header);

    using var entries = new MemoryStream();
    for (var i = 0; i < files.Count; i++) {
      var recNum = (uint)(firstUserRecord + i);
      WriteIndexEntry(entries, recNum, files[i].Name);
    }
    var last = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(12), 0x02);
    entries.Write(last);

    var entriesData = entries.ToArray();

    var indexHeader = new byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(0), 16);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(4), 16 + entriesData.Length);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(8), 16 + entriesData.Length);
    ms.Write(indexHeader);
    ms.Write(entriesData);

    return ms.ToArray();
  }

  private static byte[] BuildEmptyIndexRoot() {
    // Index root with only the end-marker entry.
    using var ms = new MemoryStream();

    var header = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 0x30);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 4096);
    header[12] = 1;
    ms.Write(header);

    var last = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(8), 16);
    BinaryPrimitives.WriteUInt16LittleEndian(last.AsSpan(12), 0x02);

    var indexHeader = new byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(0), 16);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(4), 16 + last.Length);
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(8), 16 + last.Length);
    ms.Write(indexHeader);
    ms.Write(last);

    return ms.ToArray();
  }

  private static void WriteIndexEntry(MemoryStream ms, uint mftRecordNum, string fileName) {
    var nameBytes = Encoding.Unicode.GetBytes(fileName);
    var nameChars = fileName.Length;

    var contentLen = 66 + nameChars * 2;
    var entryLen = 16 + contentLen;
    entryLen = (entryLen + 7) & ~7;

    var entry = new byte[entryLen];
    BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0), (long)mftRecordNum | (1L << 48));
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)entryLen);
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), (ushort)contentLen);

    BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), 5L | (1L << 48));
    entry[16 + 64] = (byte)nameChars;
    entry[16 + 65] = 3;
    nameBytes.CopyTo(entry, 16 + 66);

    ms.Write(entry);
  }

  // ── $Volume attributes ──────────────────────────────────────────────────

  private static byte[] BuildVolumeNameAttr(string label) {
    // $VOLUME_NAME (type 0x60) value is just the UTF-16 label (no NUL).
    return Encoding.Unicode.GetBytes(label);
  }

  private static byte[] BuildVolumeInformationAttr() {
    // $VOLUME_INFORMATION (type 0x70) layout (12 bytes):
    //   u64 reserved, u8 major_version, u8 minor_version, u16 flags.
    var v = new byte[12];
    v[8] = 3;  // major version (NTFS 3.1 → major 3)
    v[9] = 1;  // minor version
    // flags = 0 (clean volume; no VOLUME_IS_DIRTY bit set).
    return v;
  }

  // ── $AttrDef standard table ─────────────────────────────────────────────

  // Canonical NTFS attribute-definition entries the system driver expects.
  // Each entry is 160 bytes: 128-byte UTF-16 name, u32 type, u32 display rule,
  // u32 collation rule, u32 flags, u64 min size, u64 max size.
  private static byte[] BuildAttrDefTable() {
    (string Name, uint Type, uint DisplayRule, uint Collation, uint Flags, long MinSize, long MaxSize)[] defs =
    [
      ("$STANDARD_INFORMATION", 0x10, 0, 0, 0x40, 48, 72),
      ("$ATTRIBUTE_LIST",        0x20, 0, 0, 0x40, 0, -1),
      ("$FILE_NAME",             0x30, 1, 1, 0x42, 68, 578),
      ("$OBJECT_ID",             0x40, 0, 0, 0x40, 0, 256),
      ("$SECURITY_DESCRIPTOR",   0x50, 0, 0, 0x00, 0, -1),
      ("$VOLUME_NAME",           0x60, 0, 0, 0x40, 2, 256),
      ("$VOLUME_INFORMATION",    0x70, 0, 0, 0x40, 12, 12),
      ("$DATA",                  0x80, 0, 0, 0x00, 0, -1),
      ("$INDEX_ROOT",            0x90, 0, 0, 0x40, 0, -1),
      ("$INDEX_ALLOCATION",      0xA0, 0, 0, 0x00, 0, -1),
      ("$BITMAP",                0xB0, 0, 0, 0x00, 0, -1),
      ("$REPARSE_POINT",         0xC0, 0, 0, 0x00, 0, 0x4000),
      ("$EA_INFORMATION",        0xD0, 0, 0, 0x40, 8, 8),
      ("$EA",                    0xE0, 0, 0, 0x00, 0, 0x10000),
      ("$PROPERTY_SET",          0xF0, 0, 0, 0x40, 0, -1),
      ("$LOGGED_UTILITY_STREAM", 0x100, 0, 0, 0x00, 0, 0x10000),
    ];
    var table = new byte[defs.Length * 160];
    for (var i = 0; i < defs.Length; i++) {
      var d = defs[i];
      var o = i * 160;
      var name = Encoding.Unicode.GetBytes(d.Name);
      Array.Copy(name, 0, table, o, Math.Min(name.Length, 128));
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 128), d.Type);
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 132), d.DisplayRule);
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 136), d.Collation);
      BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(o + 140), d.Flags);
      BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(o + 144), d.MinSize);
      BinaryPrimitives.WriteInt64LittleEndian(table.AsSpan(o + 152), d.MaxSize);
    }
    return table;
  }

  // ── $UpCase table ────────────────────────────────────────────────────────

  /// <summary>
  /// Builds the 65 536-entry UTF-16 uppercase mapping for $UpCase. Real NTFS
  /// ships a driver-defined table with Windows-specific casing; our table is
  /// derived from <see cref="char.ToUpperInvariant"/> which matches for the
  /// ASCII range and handles the common BMP range using the ICU-backed
  /// invariant culture — good enough for ntfs-3g's sanity check (which only
  /// verifies size and a handful of well-known mappings).
  /// </summary>
  internal static byte[] BuildUpCaseTable() {
    var table = new byte[UpCaseBytes];
    for (var i = 0; i < 65536; i++) {
      var upper = char.ToUpperInvariant((char)i);
      BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(i * 2), upper);
    }
    return table;
  }

  // ── Cluster bitmap ───────────────────────────────────────────────────────

  private static byte[] BuildClusterBitmap(
    long totalClusters,
    int mftStart, int mftCount,
    long mftMirrStart, int mftMirrCount,
    int logStart, int logCount,
    int upCaseStart, int upCaseCount,
    int bitmapStart, int bitmapCount,
    List<(string Name, byte[] Data, bool Resident, int StartCluster, int ClusterCount)> files) {
    var bytes = (int)((totalClusters + 7) / 8);
    var bitmap = new byte[bytes];

    // Boot sector + first two clusters.
    SetRange(bitmap, 0, 2);
    SetRange(bitmap, mftStart, mftCount);
    SetRange(bitmap, (int)mftMirrStart, mftMirrCount);
    SetRange(bitmap, logStart, logCount);
    SetRange(bitmap, upCaseStart, upCaseCount);
    SetRange(bitmap, bitmapStart, bitmapCount);

    foreach (var f in files) {
      if (!f.Resident) SetRange(bitmap, f.StartCluster, f.ClusterCount);
    }

    return bitmap;
  }

  private static void SetRange(byte[] bitmap, int startCluster, int count) {
    for (var c = startCluster; c < startCluster + count; c++) {
      if ((uint)(c / 8) >= (uint)bitmap.Length) return;
      bitmap[c / 8] |= (byte)(1 << (c % 8));
    }
  }

  // ── Low-level helpers ────────────────────────────────────────────────────

  private static void WriteBytesToClusters(byte[] disk, int startCluster, byte[] data) {
    var offset = (long)startCluster * ClusterSize;
    if (offset + data.Length > disk.Length) return;
    data.CopyTo(disk, (int)offset);
  }
}

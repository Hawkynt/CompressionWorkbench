#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ntfs;

/// <summary>
/// Builds minimal NTFS filesystem images. Creates a 4MB image with boot sector,
/// MFT (records 0-15), root directory, and user files. Small files (&lt;700 bytes)
/// use resident $DATA; larger files use non-resident cluster runs.
/// </summary>
public sealed class NtfsWriter {
  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the NTFS image.</summary>
  public void AddFile(string name, byte[] data) => _files.Add((name, data));

  /// <summary>
  /// Builds the NTFS filesystem image.
  /// </summary>
  /// <param name="totalSize">Total image size in bytes (default 4MB).</param>
  /// <returns>Complete NTFS image as byte array.</returns>
  public byte[] Build(int totalSize = 4 * 1024 * 1024) {
    const int bytesPerSector = 512;
    const int sectorsPerCluster = 8;
    const int clusterSize = bytesPerSector * sectorsPerCluster; // 4096
    const int mftRecordSize = 1024;
    const int mftRecordCount = 16; // reserved records 0-15
    const int residentThreshold = 700;

    var disk = new byte[totalSize];
    var totalSectors = totalSize / bytesPerSector;

    // --- Boot sector (sector 0) ---
    disk[0] = 0xEB; disk[1] = 0x52; disk[2] = 0x90; // Jump
    Encoding.ASCII.GetBytes("NTFS    ").CopyTo(disk, 3); // OEM ID
    BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(11), bytesPerSector); // bytes per sector
    disk[13] = sectorsPerCluster; // sectors per cluster
    disk[21] = 0xF8; // media descriptor
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(40), totalSectors - 1); // total sectors
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(48), 2); // MFT cluster number
    BinaryPrimitives.WriteInt64LittleEndian(disk.AsSpan(56), (totalSectors / 2) / sectorsPerCluster); // MFT mirror cluster
    disk[64] = unchecked((byte)-10); // clusters per MFT record: -10 means 2^10 = 1024
    disk[68] = 4; // clusters per index block
    disk[510] = 0x55; disk[511] = 0xAA; // boot signature

    // --- MFT at cluster 2 ---
    var mftOffset = 2 * clusterSize; // cluster 2
    var totalMftRecords = mftRecordCount + _files.Count; // reserved + user files
    var mftTotalBytes = totalMftRecords * mftRecordSize;
    var mftClusters = (mftTotalBytes + clusterSize - 1) / clusterSize;

    // First available cluster for user data: after MFT
    var nextCluster = 2 + mftClusters;

    // --- Bitmap at cluster (nextCluster) ---
    var bitmapCluster = nextCluster;
    nextCluster++;

    // Collect user file data run info for non-resident files
    var fileInfos = new List<(string Name, byte[] Data, bool Resident, int StartCluster, int ClusterCount)>();
    foreach (var (name, data) in _files) {
      if (data.Length <= residentThreshold) {
        fileInfos.Add((name, data, true, 0, 0));
      } else {
        var clusters = (data.Length + clusterSize - 1) / clusterSize;
        fileInfos.Add((name, data, false, nextCluster, clusters));
        nextCluster += clusters;
      }
    }

    // --- Write MFT records ---

    // Record 0: $MFT
    WriteMftRecord(disk, mftOffset, 0, "$MFT", 5, false,
      residentData: null, nonResidentRuns: [(2, mftClusters)], dataSize: mftTotalBytes);

    // Records 1-4, 6-15: mark as unused (leave as zero — no "FILE" signature)

    // Record 5: root directory "."
    var rootIndexEntries = BuildIndexRoot(fileInfos, mftRecordCount);
    WriteMftRecord(disk, mftOffset, 5, ".", 5, true,
      residentData: null, nonResidentRuns: null, dataSize: 0,
      indexRootData: rootIndexEntries);

    // User file records starting at record 16
    for (var i = 0; i < fileInfos.Count; i++) {
      var (name, data, resident, startCluster, clusterCount) = fileInfos[i];
      var recNum = (uint)(mftRecordCount + i);

      if (resident) {
        WriteMftRecord(disk, mftOffset, recNum, name, 5, false,
          residentData: data, nonResidentRuns: null, dataSize: data.Length);
      } else {
        WriteMftRecord(disk, mftOffset, recNum, name, 5, false,
          residentData: null, nonResidentRuns: [(startCluster, clusterCount)], dataSize: data.Length);

        // Write file data to clusters
        var clusterOffset = startCluster * clusterSize;
        if (clusterOffset + data.Length <= disk.Length)
          data.CopyTo(disk, clusterOffset);
      }
    }

    // --- Write bitmap ---
    var bitmapOffset = bitmapCluster * clusterSize;
    var totalClusters = totalSize / clusterSize;
    var bitmapBytes = (totalClusters + 7) / 8;
    // Mark used clusters
    for (var c = 0; c < nextCluster && c / 8 < clusterSize; c++)
      disk[bitmapOffset + c / 8] |= (byte)(1 << (c % 8));

    return disk;
  }

  private static void WriteMftRecord(byte[] disk, int mftBaseOffset, uint recordNum,
    string fileName, uint parentRecord, bool isDirectory,
    byte[]? residentData, List<(int Cluster, int Count)>? nonResidentRuns, long dataSize,
    byte[]? indexRootData = null) {
    const int recordSize = 1024;

    // Expand MFT if needed
    var recordOffset = mftBaseOffset + (int)recordNum * recordSize;
    if (recordOffset + recordSize > disk.Length) return;

    var record = new byte[recordSize];

    // MFT record header
    record[0] = (byte)'F'; record[1] = (byte)'I'; record[2] = (byte)'L'; record[3] = (byte)'E';

    // Update sequence: offset 42, count 3 (1 USN + 2 sector fixups for 1024-byte record)
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 42); // update sequence offset
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);  // update sequence count (1 + 2 sectors)

    // Sequence number
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);

    // First attribute offset (after header + update sequence: 42 + 6 = 48)
    var attrStart = 48;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), (ushort)attrStart);

    // Flags
    ushort flags = 0x01; // in use
    if (isDirectory) flags |= 0x02;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), flags);

    // Allocated size
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), (uint)recordSize);

    var pos = attrStart;

    // --- $FILE_NAME attribute (0x30) ---
    pos = WriteFileNameAttr(record, pos, fileName, parentRecord);

    // --- $DATA attribute (0x80) ---
    if (!isDirectory) {
      if (residentData != null) {
        pos = WriteResidentDataAttr(record, pos, residentData);
      } else if (nonResidentRuns != null) {
        pos = WriteNonResidentDataAttr(record, pos, nonResidentRuns, dataSize);
      }
    }

    // --- $INDEX_ROOT attribute (0x90) for directories ---
    if (isDirectory && indexRootData != null)
      pos = WriteIndexRootAttr(record, pos, indexRootData);

    // End marker
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0xFFFFFFFF);
    pos += 4;

    // Used size
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)pos);

    // --- Fixup array ---
    // USN value = 0x0001
    var usn = (ushort)0x0001;
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(42), usn);

    // Save original bytes at sector boundaries, replace with USN
    // Sector 1 end: offset 510-511
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(44), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(510)));
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), usn);

    // Sector 2 end: offset 1022-1023
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(46), BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(1022)));
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), usn);

    record.CopyTo(disk, recordOffset);
  }

  private static int WriteFileNameAttr(byte[] record, int pos, string fileName, uint parentRecord) {
    // $FILE_NAME is always resident
    var nameBytes = Encoding.Unicode.GetBytes(fileName);
    var nameChars = fileName.Length;
    var valueLen = 66 + nameChars * 2; // $FILE_NAME structure size

    var attrLen = 24 + valueLen; // resident attr header (24) + value
    attrLen = (attrLen + 7) & ~7; // align to 8 bytes

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x30); // type
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen); // length
    record[pos + 8] = 0; // resident
    record[pos + 9] = 0; // name length (unnamed)

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)valueLen); // value length
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24); // value offset

    var valueStart = pos + 24;

    // Parent directory reference (48-bit record number + 16-bit sequence)
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(valueStart), (long)parentRecord | (1L << 48));

    // Timestamps (creation, modification, MFT modification, access) — 8 bytes each at offsets 8, 16, 24, 32
    var now = DateTime.UtcNow.ToFileTimeUtc();
    for (var t = 0; t < 4; t++)
      BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(valueStart + 8 + t * 8), now);

    // Allocated size (offset 40) and real size (offset 48)
    // These are set to 0 for the $FILE_NAME attribute (not authoritative for data size)

    // Flags at offset 56 (4 bytes) — 0
    // Reparse value at offset 60 (4 bytes) — 0

    // Name length and namespace
    record[valueStart + 64] = (byte)nameChars;
    record[valueStart + 65] = 3; // Win32+DOS namespace

    // File name
    nameBytes.CopyTo(record, valueStart + 66);

    return pos + attrLen;
  }

  private static int WriteResidentDataAttr(byte[] record, int pos, byte[] data) {
    var attrLen = 24 + data.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x80); // type $DATA
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; // resident
    record[pos + 9] = 0; // name length

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)data.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24); // value offset

    data.CopyTo(record, pos + 24);

    return pos + attrLen;
  }

  private static int WriteNonResidentDataAttr(byte[] record, int pos, List<(int Cluster, int Count)> runs, long dataSize) {
    // Encode data runs
    var dataRuns = EncodeDataRuns(runs);

    // Non-resident attribute header is 64 bytes min + data runs
    var dataRunsOffset = 64;
    var attrLen = dataRunsOffset + dataRuns.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x80); // type $DATA
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 1; // non-resident
    record[pos + 9] = 0; // name length

    // VCN start/end
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 16), 0); // starting VCN
    long totalClusters = 0;
    foreach (var (_, count) in runs) totalClusters += count;
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 24), totalClusters - 1); // ending VCN

    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 32), (ushort)dataRunsOffset); // data runs offset
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 40), totalClusters * 4096); // allocated size
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 48), dataSize); // real size
    BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(pos + 56), dataSize); // initialized size

    dataRuns.CopyTo(record, pos + dataRunsOffset);

    return pos + attrLen;
  }

  private static int WriteIndexRootAttr(byte[] record, int pos, byte[] indexData) {
    var attrLen = 24 + indexData.Length;
    attrLen = (attrLen + 7) & ~7;

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos), 0x90); // type $INDEX_ROOT
    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 4), (uint)attrLen);
    record[pos + 8] = 0; // resident
    record[pos + 9] = 0; // name length

    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(pos + 16), (uint)indexData.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(pos + 20), 24); // value offset

    indexData.CopyTo(record, pos + 24);

    return pos + attrLen;
  }

  private static byte[] EncodeDataRuns(List<(int Cluster, int Count)> runs) {
    using var ms = new MemoryStream();
    long prevLcn = 0;

    foreach (var (cluster, count) in runs) {
      var offset = (long)cluster - prevLcn;

      var lengthBytes = GetSignedFieldBytes(count, unsigned: true);
      var offsetBytes = GetSignedFieldBytes(offset, unsigned: false);

      var header = (byte)((offsetBytes << 4) | lengthBytes);
      ms.WriteByte(header);

      WriteField(ms, count, lengthBytes);
      WriteField(ms, offset, offsetBytes);

      prevLcn = cluster;
    }

    ms.WriteByte(0); // end
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
    // Signed
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
    // Build index root with index entries referencing user file MFT records
    using var ms = new MemoryStream();

    // Index root header (16 bytes)
    var headerBuf = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(0), 0x30); // indexed attribute type ($FILE_NAME)
    BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(4), 1); // collation rule (FILENAME)
    BinaryPrimitives.WriteUInt32LittleEndian(headerBuf.AsSpan(8), 4096); // index alloc entry size
    headerBuf[12] = 1; // clusters per index block
    ms.Write(headerBuf);

    // Build index entries into a temp buffer to calculate sizes
    using var entriesMs = new MemoryStream();
    for (var i = 0; i < files.Count; i++) {
      var recNum = (uint)(firstUserRecord + i);
      WriteIndexEntry(entriesMs, recNum, files[i].Name);
    }

    // Write last entry (end marker)
    var lastEntry = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(lastEntry.AsSpan(8), 16); // entry length
    BinaryPrimitives.WriteUInt16LittleEndian(lastEntry.AsSpan(12), 0x02); // last entry flag
    entriesMs.Write(lastEntry);

    var entriesData = entriesMs.ToArray();

    // Index header (16 bytes): entries offset, total size, allocated size, flags
    var indexHeader = new byte[16];
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(0), 16); // entries offset (relative to index header start)
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(4), 16 + entriesData.Length); // total size
    BinaryPrimitives.WriteInt32LittleEndian(indexHeader.AsSpan(8), 16 + entriesData.Length); // allocated size
    // flags = 0 (small index, fits in root)
    ms.Write(indexHeader);

    ms.Write(entriesData);

    return ms.ToArray();
  }

  private static void WriteIndexEntry(MemoryStream ms, uint mftRecordNum, string fileName) {
    var nameBytes = Encoding.Unicode.GetBytes(fileName);
    var nameChars = fileName.Length;

    // Index entry: MFT ref (8), entry length (2), content length (2), flags (4)
    // then $FILE_NAME-like content
    var contentLen = 66 + nameChars * 2;
    var entryLen = 16 + contentLen;
    entryLen = (entryLen + 7) & ~7;

    var entry = new byte[entryLen];
    BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(0), (long)mftRecordNum | (1L << 48)); // MFT reference
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(8), (ushort)entryLen); // entry length
    BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(10), (ushort)contentLen); // content length
    // flags at offset 12 = 0

    // Minimal $FILE_NAME at offset 16
    BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), 5L | (1L << 48)); // parent = root (record 5)
    entry[16 + 64] = (byte)nameChars;
    entry[16 + 65] = 3; // Win32+DOS
    nameBytes.CopyTo(entry, 16 + 66);

    ms.Write(entry);
  }
}

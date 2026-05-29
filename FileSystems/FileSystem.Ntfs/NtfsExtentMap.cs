#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Ntfs;

/// <summary>
/// Walks an NTFS image and yields its actual on-disk byte layout — the boot
/// sector, the $MFT itself (record 0's $DATA runs), the 16 reserved system
/// files (records 0-15), and every regular file's $DATA attribute data runs.
/// Each non-resident $DATA's run-list is decoded; resident $DATA bytes
/// surface as a single MetadataReserved tile inside the MFT record. The 16
/// reserved system records (e.g. $MFTMirr, $LogFile, $Bitmap, $Boot,
/// $UpCase) are flagged as MetadataReserved.
/// <para>
/// Streaming: reads only the boot sector + MFT records on demand via a
/// <see cref="SectorCache"/>. A 50 TB NTFS image with a large $MFT keeps
/// memory bounded to ~256 MB regardless of image size.
/// </para>
/// </summary>
public static class NtfsExtentMap {

  private const int Reserved = 16;

  /// <summary>
  /// Single-pass walker. Reads the boot sector, locates $MFT, parses each
  /// MFT record's $DATA attribute, then yields one extent per data run.
  /// Adjacent runs (LCN N..M and LCN M+1..) are coalesced.
  /// </summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.Length < 512) yield break;

    // Read just the boot sector (first 512 bytes) — never load the whole image.
    var boot = new byte[512];
    image.Position = 0;
    image.ReadExactly(boot);

    if (boot[0] != 0xEB || boot[1] != 0x52 || boot[2] != 0x90) yield break;
    if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ") yield break;
    if (boot[510] != 0x55 || boot[511] != 0xAA) yield break;

    var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    if (bytesPerSector == 0) bytesPerSector = 512;
    int sectorsPerCluster = boot[13];
    if (sectorsPerCluster == 0) sectorsPerCluster = 8;
    var clusterSize = bytesPerSector * sectorsPerCluster;
    var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(48));
    var clustersPerRecord = (sbyte)boot[64];
    var mftRecordSize = clustersPerRecord < 0
      ? 1 << (-clustersPerRecord)
      : clustersPerRecord * clusterSize;
    if (mftRecordSize <= 0 || mftRecordSize > 65536) yield break;

    // Boot sector + reserved sectors before $MFT.
    var mftOffset = mftCluster * clusterSize;
    if (mftOffset <= 0 || mftOffset >= image.Length) yield break;
    yield return new DefragBlockInfo(0, Math.Min(clusterSize, image.Length),
      DefragBlockKind.MetadataReserved, FileName: "NTFS boot sector");

    // All subsequent MFT reads flow through this cache so a fragmented MFT
    // doesn't fault us into reading random sectors twice.
    using var cache = new SectorCache(image);
    var recordBuf = ArrayPool<byte>.Shared.Rent(mftRecordSize);
    try {

      // Read record 0 ($MFT) to discover the MFT extent and record count.
      var rec0 = ReadRecord(cache, mftOffset, mftRecordSize, recordBuf);
      if (rec0 == null) yield break;

      var maxRecords = Reserved;
      long totalMftBytes = 0;
      if (!rec0.IsResident && rec0.DataRuns is { Count: > 0 }) {
        foreach (var run in rec0.DataRuns) totalMftBytes += run.ClusterCount * clusterSize;
        var bounded = (int)(totalMftBytes / mftRecordSize);
        if (bounded > maxRecords) maxRecords = bounded;

        // Yield every $MFT data run as MetadataReserved (the MFT itself).
        foreach (var run in rec0.DataRuns) {
          var off = run.Lcn * clusterSize;
          var len = run.ClusterCount * clusterSize;
          if (off + len > image.Length) len = Math.Max(0, image.Length - off);
          if (len > 0)
            yield return new DefragBlockInfo(off, len, DefragBlockKind.MetadataReserved,
              FileName: "$MFT");
        }
      } else if (rec0.DataSize > 0) {
        var bounded = (int)(rec0.DataSize / mftRecordSize);
        if (bounded > maxRecords) maxRecords = bounded;
      }
      var mftAreaSize = image.Length - mftOffset;
      var maxFromImage = (int)(mftAreaSize / mftRecordSize);
      if (maxRecords > maxFromImage) maxRecords = maxFromImage;

      // Iterate MFT records 1..N, classifying each.
      for (var i = 1; i < maxRecords; i++) {
        var recOff = mftOffset + (long)i * mftRecordSize;
        if (recOff + mftRecordSize > image.Length) break;
        var rec = ReadRecord(cache, recOff, mftRecordSize, recordBuf);
        if (rec == null) continue;

        // Records 1-15 are reserved system files: $MFTMirr, $LogFile, $Volume,
        // $AttrDef, root ., $Bitmap, $Boot, $BadClus, $Secure, $UpCase, $Extend
        // and 4 more reserved slots. Flag their data as MetadataReserved.
        var isSystem = i < Reserved;
        var label = isSystem ? SystemFileName(i) : (rec.FileName ?? $"mft#{i}");
        var kind = isSystem ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used;

        if (rec.IsResident) {
          // Resident data lives inside the MFT record itself — we already
          // covered the MFT extent above. No new region to emit.
          continue;
        }

        if (rec.DataRuns == null || rec.DataRuns.Count == 0) continue;

        // Coalesce adjacent runs for compact emission.
        long? runStart = null;
        long runLen = 0;
        foreach (var run in rec.DataRuns) {
          var off = run.Lcn * clusterSize;
          var len = run.ClusterCount * clusterSize;
          if (off + len > image.Length) len = Math.Max(0, image.Length - off);
          if (len <= 0) continue;
          if (runStart is { } rs && rs + runLen == off) {
            runLen += len;
          } else {
            if (runStart is { } prev)
              yield return new DefragBlockInfo(prev, runLen, kind, FileName: label);
            runStart = off;
            runLen = len;
          }
        }
        if (runStart is { } finalOff)
          yield return new DefragBlockInfo(finalOff, runLen, kind, FileName: label);
      }
    } finally {
      ArrayPool<byte>.Shared.Return(recordBuf);
    }
  }

  private static string SystemFileName(int i) => i switch {
    0 => "$MFT", 1 => "$MFTMirr", 2 => "$LogFile", 3 => "$Volume",
    4 => "$AttrDef", 5 => "root .", 6 => "$Bitmap", 7 => "$Boot",
    8 => "$BadClus", 9 => "$Secure", 10 => "$UpCase", 11 => "$Extend",
    _ => $"$reserved{i}",
  };

  private sealed class Rec {
    public string? FileName;
    public bool IsResident;
    public long DataSize;
    public List<DataRun>? DataRuns;
  }

  private sealed class DataRun {
    public long Lcn;
    public long ClusterCount;
  }

  /// <summary>
  /// Reads a single MFT record via the sector cache and parses its file-name
  /// + $DATA attributes. Applies the NTFS Update Sequence Array fixup so
  /// per-sector sentinels are replaced with the real bytes before parsing.
  /// </summary>
  private static Rec? ReadRecord(SectorCache cache, long offset, int recordSize, byte[] scratch) {
    if (offset < 0 || offset + recordSize > cache.Length) return null;
    cache.Read(offset, scratch.AsSpan(0, recordSize));
    if (scratch[0] != 'F' || scratch[1] != 'I' || scratch[2] != 'L' || scratch[3] != 'E') return null;

    // Work on a sized, fixup-applied copy so subsequent reads via the cache
    // see the original on-disk bytes (the cache holds raw sector contents).
    var record = scratch.AsSpan(0, recordSize).ToArray();
    ApplyFixup(record);

    var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
    if ((flags & 0x01) == 0) return null;
    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));

    var rec = new Rec();
    var attrPos = (int)firstAttrOffset;
    while (attrPos + 4 <= usedSize && attrPos + 4 <= record.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;
      var attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > record.Length) break;
      var nonResident = record[attrPos + 8];
      var nameLen = record[attrPos + 9];

      string? attrName = null;
      if (nameLen > 0) {
        var nameOff = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 10));
        if (attrPos + nameOff + nameLen * 2 <= record.Length)
          attrName = Encoding.Unicode.GetString(record, attrPos + nameOff, nameLen * 2);
      }

      switch (attrType) {
        case 0x30: // $FILE_NAME
          if (nonResident == 0) ParseFileName(record, attrPos, rec);
          break;
        case 0x80: // $DATA
          if (string.IsNullOrEmpty(attrName)) ParseDataAttr(record, attrPos, nonResident, rec);
          break;
      }

      attrPos += (int)attrLen;
    }
    return rec;
  }

  private static void ApplyFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaOffset + usaCount * 2 > record.Length || usaCount < 2) return;
    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;
      var actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd));
      if (actual != usn) continue;
      record.AsSpan(usaOffset + i * 2, 2).CopyTo(record.AsSpan(sectorEnd));
    }
  }

  private static void ParseFileName(byte[] record, int attrPos, Rec rec) {
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 20));
    var dataStart = attrPos + valueOffset;
    if (dataStart + 66 > record.Length) return;
    var nameLength = record[dataStart + 64];
    var nameSpace = record[dataStart + 65];
    if (dataStart + 66 + nameLength * 2 > record.Length) return;
    if (nameSpace == 2 && rec.FileName != null) return; // skip DOS-only
    rec.FileName = Encoding.Unicode.GetString(record, dataStart + 66, nameLength * 2);
  }

  private static void ParseDataAttr(byte[] record, int attrPos, byte nonResident, Rec rec) {
    if (nonResident == 0) {
      rec.IsResident = true;
      var valueLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 16));
      rec.DataSize = valueLen;
      return;
    }
    rec.IsResident = false;
    if (attrPos + 56 <= record.Length)
      rec.DataSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attrPos + 48));
    if (attrPos + 34 <= record.Length) {
      var dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 32));
      rec.DataRuns = ParseDataRuns(record, attrPos + dataRunsOffset);
    }
  }

  private static List<DataRun> ParseDataRuns(byte[] record, int offset) {
    var runs = new List<DataRun>();
    long previousLcn = 0;
    while (offset < record.Length) {
      var header = record[offset];
      if (header == 0) break;
      var lengthBytes = header & 0x0F;
      var offsetBytes = (header >> 4) & 0x0F;
      offset++;
      if (offset + lengthBytes + offsetBytes > record.Length) break;

      long length = 0;
      for (var i = 0; i < lengthBytes; i++)
        length |= (long)record[offset + i] << (i * 8);
      offset += lengthBytes;

      long clusterOffset = 0;
      if (offsetBytes > 0) {
        for (var i = 0; i < offsetBytes; i++)
          clusterOffset |= (long)record[offset + i] << (i * 8);
        if ((record[offset + offsetBytes - 1] & 0x80) != 0) {
          for (var i = offsetBytes; i < 8; i++)
            clusterOffset |= (long)0xFF << (i * 8);
        }
        offset += offsetBytes;
      }
      var lcn = previousLcn + clusterOffset;
      runs.Add(new DataRun { Lcn = lcn, ClusterCount = length });
      previousLcn = lcn;
    }
    return runs;
  }
}

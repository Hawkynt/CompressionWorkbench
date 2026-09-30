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
    if (image.Length < 512) return [];

    // Read just the boot sector (first 512 bytes) — never load the whole image.
    var boot = new byte[512];
    image.Position = 0;
    image.ReadExactly(boot);

    if (boot[0] != 0xEB || boot[1] != 0x52 || boot[2] != 0x90) return [];
    if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ") return [];
    if (boot[510] != 0x55 || boot[511] != 0xAA) return [];

    var bytesPerSector = (int)BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11));
    if (bytesPerSector == 0) bytesPerSector = 512;
    int sectorsPerCluster = boot[13];
    if (sectorsPerCluster == 0) sectorsPerCluster = 8;
    var clusterSize = bytesPerSector * sectorsPerCluster;
    var totalSectors = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(40));
    var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(48));
    var clustersPerRecord = (sbyte)boot[64];
    var mftRecordSize = clustersPerRecord < 0
      ? 1 << (-clustersPerRecord)
      : clustersPerRecord * clusterSize;
    if (mftRecordSize <= 0 || mftRecordSize > 65536) return [];

    var mftOffset = mftCluster * clusterSize;
    if (mftOffset <= 0 || mftOffset >= image.Length) return [];

    var result = new List<DefragBlockInfo> {
      new(0, Math.Min(clusterSize, image.Length), DefragBlockKind.MetadataReserved, FileName: "NTFS boot sector"),
    };
    List<DataRun>? bitmapRuns = null;
    long bitmapBytes = 0;

    // All MFT reads flow through this cache so a fragmented MFT doesn't fault us
    // into reading random sectors twice.
    using (var cache = new SectorCache(image)) {
      var recordBuf = ArrayPool<byte>.Shared.Rent(mftRecordSize);
      try {
        // Read record 0 ($MFT) to discover the MFT extent and record count.
        var rec0 = ReadRecord(cache, mftOffset, mftRecordSize, recordBuf);
        if (rec0 == null) return [];

        var maxRecords = Reserved;
        if (!rec0.IsResident && rec0.DataRuns is { Count: > 0 }) {
          long totalMftBytes = 0;
          foreach (var run in rec0.DataRuns) totalMftBytes += run.ClusterCount * clusterSize;
          var bounded = (int)(totalMftBytes / mftRecordSize);
          if (bounded > maxRecords) maxRecords = bounded;
        } else if (rec0.DataSize > 0) {
          var bounded = (int)(rec0.DataSize / mftRecordSize);
          if (bounded > maxRecords) maxRecords = bounded;
        }
        var mftAreaSize = image.Length - mftOffset;
        var maxFromImage = (int)(mftAreaSize / mftRecordSize);
        if (maxRecords > maxFromImage) maxRecords = maxFromImage;

        // Record 0 is the MFT itself: its $DATA runs are the table, and every other
        // non-resident attribute it carries ($MFT:$BITMAP) is metadata too.
        EmitRecord(result, rec0, 0, image.Length, clusterSize);

        for (var i = 1; i < maxRecords; i++) {
          var recOff = mftOffset + (long)i * mftRecordSize;
          if (recOff + mftRecordSize > image.Length) break;
          var rec = ReadRecord(cache, recOff, mftRecordSize, recordBuf);
          if (rec == null) continue;
          if (i == 6 && !rec.IsResident && rec.DataRuns is { Count: > 0 }) {
            bitmapRuns = rec.DataRuns;
            bitmapBytes = rec.DataSize;
          }
          EmitRecord(result, rec, i, image.Length, clusterSize);
        }
      } finally {
        ArrayPool<byte>.Shared.Return(recordBuf);
      }
    }

    // Fail closed. An attribute this walk does not decode — a run moved into an
    // extension record it could not read, a stream type a later NTFS added — still
    // has its clusters marked in $Bitmap. Whatever the volume says is allocated and
    // nothing above claimed is reported as reserved, so a consumer that treats gaps
    // as free space never zeroes, or moves a file over, bytes the volume is using.
    if (bitmapRuns != null)
      AddUnclaimedAllocated(image, result, bitmapRuns, bitmapBytes, clusterSize);

    // The copy of the boot sector sits in the last sector of the partition, outside
    // the cluster range the volume describes, and nothing in the MFT points at it.
    // Everything from the end of the last whole cluster onwards is kept as it is.
    var volumeEnd = totalSectors > 0
      ? Math.Min(image.Length, totalSectors / sectorsPerCluster * clusterSize)
      : image.Length;
    if (volumeEnd < image.Length)
      result.Add(new DefragBlockInfo(volumeEnd, image.Length - volumeEnd, DefragBlockKind.MetadataReserved,
        FileName: "NTFS backup boot sector"));
    return result;
  }

  /// <summary>
  /// Emits every non-resident attribute of one MFT record. A regular file's unnamed
  /// <c>$DATA</c> is the only thing the block mover relinks, so it alone is reported
  /// as the file's own data; directory indexes, named streams, attribute lists,
  /// reparse data and every system file are reserved metadata.
  /// </summary>
  private static void EmitRecord(List<DefragBlockInfo> result, Rec rec, int recordNumber, long imageLength,
      int clusterSize) {
    var isSystem = recordNumber < Reserved;
    var label = isSystem ? SystemFileName(recordNumber, rec.FileName) : (rec.FileName ?? $"mft#{recordNumber}");
    foreach (var attr in rec.NonResident) {
      var isFileData = attr.Type == 0x80 && string.IsNullOrEmpty(attr.Name);
      var kind = isFileData && !isSystem && !rec.IsExtension ? DefragBlockKind.Used : DefragBlockKind.MetadataReserved;
      var name = isFileData ? label : $"{label}:{AttributeLabel(attr.Type, attr.Name)}";
      long? runStart = null;
      long runLen = 0;
      foreach (var run in attr.Runs) {
        if (run.Sparse) continue;             // a hole occupies no cluster
        var off = run.Lcn * clusterSize;
        var len = run.ClusterCount * clusterSize;
        if (off < 0 || off >= imageLength) continue;
        if (off + len > imageLength) len = imageLength - off;
        if (len <= 0) continue;
        if (runStart is { } rs && rs + runLen == off) {
          runLen += len;
        } else {
          if (runStart is { } prev)
            result.Add(new DefragBlockInfo(prev, runLen, kind, FileName: name));
          runStart = off;
          runLen = len;
        }
      }
      if (runStart is { } finalOff)
        result.Add(new DefragBlockInfo(finalOff, runLen, kind, FileName: name));
    }
  }

  private static string AttributeLabel(uint type, string? name) {
    var typeName = type switch {
      0x20 => "$ATTRIBUTE_LIST", 0x80 => "$DATA", 0xA0 => "$INDEX_ALLOCATION", 0xB0 => "$BITMAP",
      0xC0 => "$REPARSE_POINT", 0xD0 => "$EA_INFORMATION", 0xE0 => "$EA", 0x100 => "$LOGGED_UTILITY_STREAM",
      _ => $"0x{type:X}",
    };
    return string.IsNullOrEmpty(name) ? typeName : $"{typeName}:{name}";
  }

  /// <summary>
  /// Adds a reserved region for every run of clusters <c>$Bitmap</c> marks as in use
  /// that no emitted extent covers.
  /// </summary>
  private static void AddUnclaimedAllocated(Stream image, List<DefragBlockInfo> result, List<DataRun> bitmapRuns,
      long bitmapBytes, int clusterSize) {
    var claimed = result.Select(e => (Start: e.Offset, End: e.Offset + e.Length)).OrderBy(r => r.Start).ToList();
    var extra = new List<DefragBlockInfo>();
    var buffer = new byte[64 * 1024];
    long bitmapPos = 0;          // byte index into the bitmap stream
    long unclaimedStart = -1;    // first cluster of the open unclaimed run
    var claimIndex = 0;
    var finished = false;

    void Close(long endCluster) {
      if (unclaimedStart < 0) return;
      var off = unclaimedStart * clusterSize;
      var len = Math.Min(image.Length, endCluster * clusterSize) - off;
      if (len > 0)
        extra.Add(new DefragBlockInfo(off, len, DefragBlockKind.MetadataReserved, FileName: "allocated (unattributed)"));
      unclaimedStart = -1;
    }

    bool IsClaimed(long byteOffset) {
      while (claimIndex < claimed.Count && claimed[claimIndex].End <= byteOffset) ++claimIndex;
      for (var j = claimIndex; j < claimed.Count && claimed[j].Start <= byteOffset; ++j)
        if (claimed[j].End > byteOffset) return true;
      return false;
    }

    foreach (var run in bitmapRuns) {
      if (finished) break;
      var runBytes = run.ClusterCount * clusterSize;
      if (run.Sparse) { bitmapPos += runBytes; continue; }
      for (long done = 0; done < runBytes && bitmapPos < bitmapBytes && !finished;) {
        var chunk = (int)Math.Min(buffer.Length, Math.Min(runBytes - done, bitmapBytes - bitmapPos));
        var src = run.Lcn * clusterSize + done;
        if (src < 0 || src + chunk > image.Length) { finished = true; break; }
        image.Position = src;
        image.ReadExactly(buffer, 0, chunk);
        for (var b = 0; b < chunk && !finished; ++b) {
          var bits = buffer[b];
          for (var bit = 0; bit < 8; ++bit) {
            var cluster = (bitmapPos + b) * 8 + bit;
            var offset = cluster * clusterSize;
            if (offset >= image.Length) { Close(cluster); finished = true; break; }
            if ((bits & (1 << bit)) != 0 && !IsClaimed(offset)) {
              if (unclaimedStart < 0) unclaimedStart = cluster;
            } else {
              Close(cluster);
            }
          }
        }
        done += chunk;
        bitmapPos += chunk;
      }
    }
    Close(bitmapPos * 8);
    result.AddRange(extra);
  }

  /// <summary>
  /// What to call one of the reserved records, preferring what the record calls itself.
  /// </summary>
  /// <remarks>
  /// Two of these slots hold different files depending on the volume's version: record 9 is
  /// <c>$Quota</c> on NTFS 1.2 and <c>$Secure</c> from 3.0, and record 11 is unused before 3.0 and
  /// <c>$Extend</c> from it. Naming them by record number alone therefore labels a genuine NT 4
  /// volume's <c>$Quota</c> as <c>$Secure</c> — a map that says the wrong thing about a disk this
  /// package can now read.
  /// <para/>
  /// The record's own <c>$FILE_NAME</c> settles it. Deriving the name from the volume's declared
  /// version instead — <see cref="NtfsVersions.Record9Name" /> already answers exactly this
  /// question — would mean believing the stamp over the metadata, and a volume where those two
  /// disagree is a case this package reports rather than assumes away
  /// (<c>NtfsReader.VersionInconsistencies</c>). What the record calls itself is the evidence; the
  /// stamp is a claim about it.
  /// <para/>
  /// The table stays as the fallback for a record carrying no name, and record 5 keeps its curated
  /// label: the root directory names itself <c>.</c>, which is accurate and useless in a list of
  /// regions.
  /// </remarks>
  private static string SystemFileName(int i, string? recorded) {
    if (i is 9 or 11 && !string.IsNullOrEmpty(recorded))
      return recorded;

    return i switch {
      0 => "$MFT", 1 => "$MFTMirr", 2 => "$LogFile", 3 => "$Volume",
      4 => "$AttrDef", 5 => "root .", 6 => "$Bitmap", 7 => "$Boot",
      8 => "$BadClus", 9 => "$Secure", 10 => "$UpCase", 11 => "$Extend",
      _ => $"$reserved{i}",
    };
  }

  private sealed class Rec {
    public string? FileName;
    public bool IsResident;
    public long DataSize;
    public List<DataRun>? DataRuns;
    public bool IsExtension;
    public List<NonResidentAttr> NonResident { get; } = [];
  }

  private sealed record NonResidentAttr(uint Type, string? Name, List<DataRun> Runs);

  private sealed class DataRun {
    public long Lcn;
    public long ClusterCount;
    public bool Sparse;
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

    var rec = new Rec {
      // An extension record carries attributes its base record's $ATTRIBUTE_LIST
      // moved out; it is not a file of its own.
      IsExtension = (BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(32)) & 0x0000FFFFFFFFFFFF) != 0,
    };
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
      if (nonResident != 0 && attrPos + 34 <= record.Length) {
        var runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 32));
        rec.NonResident.Add(new NonResidentAttr(attrType, attrName,
          ParseDataRuns(record, attrPos + runsOffset, Math.Min(record.Length, attrPos + (int)attrLen))));
      }

      attrPos += (int)attrLen;
    }
    return rec;
  }

  private static void ApplyFixup(byte[] record) {
    if (!NtfsRecordLayout.TryReadUpdateSequence(record, out var usaOffset, out var usaCount)) return;
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
      rec.DataRuns = ParseDataRuns(record, attrPos + dataRunsOffset, record.Length);
    }
  }

  private static List<DataRun> ParseDataRuns(byte[] record, int offset, int limit) {
    var runs = new List<DataRun>();
    long previousLcn = 0;
    while (offset < limit) {
      var header = record[offset];
      if (header == 0) break;
      var lengthBytes = header & 0x0F;
      var offsetBytes = (header >> 4) & 0x0F;
      offset++;
      if (offset + lengthBytes + offsetBytes > limit) break;

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
      // A run with no offset field is a hole (sparse or compressed tail): it
      // occupies no cluster and does not move the running LCN.
      if (offsetBytes == 0) {
        runs.Add(new DataRun { Lcn = -1, ClusterCount = length, Sparse = true });
        continue;
      }
      var lcn = previousLcn + clusterOffset;
      runs.Add(new DataRun { Lcn = lcn, ClusterCount = length });
      previousLcn = lcn;
    }
    return runs;
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Reads NTFS filesystem images. Parses boot sector, MFT records,
/// attributes ($FILE_NAME, $DATA), and supports both resident and
/// non-resident data extraction with data run decoding.
/// </summary>
public sealed class NtfsReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<NtfsEntry> _entries = [];

  public IReadOnlyList<NtfsEntry> Entries => _entries;

  // Boot sector fields
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _clusterSize;
  private long _mftCluster;
  private int _mftRecordSize;

  // Parsed MFT records: record number -> parsed info
  private readonly Dictionary<uint, MftRecord> _mftRecords = [];

  public NtfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < 512)
      throw new InvalidDataException("NTFS: image too small.");

    // Validate boot sector jump
    if (_data[0] != 0xEB || _data[1] != 0x52 || _data[2] != 0x90)
      throw new InvalidDataException("NTFS: invalid boot jump.");

    // Validate OEM ID
    var oem = Encoding.ASCII.GetString(_data, 3, 8);
    if (oem != "NTFS    ")
      throw new InvalidDataException("NTFS: invalid OEM ID.");

    // Validate boot signature
    if (_data[510] != 0x55 || _data[511] != 0xAA)
      throw new InvalidDataException("NTFS: invalid boot signature.");

    _bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(11));
    if (_bytesPerSector == 0) _bytesPerSector = 512;
    _sectorsPerCluster = _data[13];
    if (_sectorsPerCluster == 0) _sectorsPerCluster = 8;
    _clusterSize = _bytesPerSector * _sectorsPerCluster;
    _mftCluster = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(48));

    // MFT record size from clusters-per-MFT-record field
    var clustersPerRecord = (sbyte)_data[64];
    _mftRecordSize = clustersPerRecord < 0
      ? 1 << (-clustersPerRecord)
      : clustersPerRecord * _clusterSize;

    // Read MFT records. Step 1: read record 0 ($MFT itself). Its $DATA attribute
    // describes the on-disk extent of the MFT, which bounds how many records we
    // should scan. Without that bound we would also pick up "FILE"-signatured
    // sectors belonging to $MFTMirr or other mirrored regions and mis-assign
    // them as phantom MFT entries.
    var mftOffset = _mftCluster * _clusterSize;
    var maxRecords = 16;

    if (mftOffset >= 0 && mftOffset + _mftRecordSize <= _data.Length) {
      var rec0 = ReadMftRecord(0, mftOffset);
      if (rec0 != null) {
        _mftRecords[0] = rec0;
        if (!rec0.IsResident && rec0.DataRuns is { Count: > 0 }) {
          long totalMftBytes = 0;
          foreach (var run in rec0.DataRuns) totalMftBytes += run.ClusterCount * _clusterSize;
          var bounded = (int)(totalMftBytes / _mftRecordSize);
          if (bounded > maxRecords) maxRecords = bounded;
        } else if (rec0.DataSize > 0) {
          var bounded = (int)(rec0.DataSize / _mftRecordSize);
          if (bounded > maxRecords) maxRecords = bounded;
        }
      }
    }

    // Hard ceiling: never scan beyond the image.
    var mftAreaSize = _data.Length - mftOffset;
    if (mftAreaSize > 0) {
      var maxFromImage = (int)(mftAreaSize / _mftRecordSize);
      if (maxRecords > maxFromImage) maxRecords = maxFromImage;
    }

    for (var i = 1; i < maxRecords; i++) {
      var recordOffset = (long)(mftOffset + i * _mftRecordSize);
      if (recordOffset + _mftRecordSize > _data.Length) break;

      var record = ReadMftRecord((uint)i, recordOffset);
      if (record != null)
        _mftRecords[(uint)i] = record;
    }

    // For every directory whose index spilled into $INDEX_ALLOCATION, read the
    // INDX blocks and fold their FILE_NAME references into the directory's index
    // entry set so large directories enumerate completely.
    foreach (var rec in _mftRecords.Values) {
      if (rec.IsDirectory && rec.IndexAllocationRuns is { Count: > 0 })
        CollectIndexAllocationRefs(rec);
    }

    // Enumerate files from root directory (record 5)
    EnumerateDirectory(5, "");
  }

  private MftRecord? ReadMftRecord(uint recordNum, long offset) {
    var span = _data.AsSpan((int)offset, _mftRecordSize);

    // Check "FILE" signature
    if (span[0] != (byte)'F' || span[1] != (byte)'I' || span[2] != (byte)'L' || span[3] != (byte)'E')
      return null;

    // Apply fixup array
    var record = span.ToArray();
    ApplyFixup(record);

    var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
    if ((flags & 0x01) == 0) return null; // not in use

    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));

    var mft = new MftRecord {
      RecordNumber = recordNum,
      IsDirectory = (flags & 0x02) != 0,
      Flags = flags,
    };

    // Parse attributes
    var attrPos = (int)firstAttrOffset;
    while (attrPos + 4 <= usedSize && attrPos + 4 <= record.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;

      var attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > record.Length) break;

      var nonResident = record[attrPos + 8];
      var nameLen = record[attrPos + 9];

      // Get attribute name (for named streams like ADS)
      string? attrName = null;
      if (nameLen > 0) {
        var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 10));
        if (attrPos + nameOffset + nameLen * 2 <= record.Length)
          attrName = Encoding.Unicode.GetString(record, attrPos + nameOffset, nameLen * 2);
      }

      switch (attrType) {
        case 0x30: // $FILE_NAME
          if (nonResident == 0)
            ParseFileName(record, attrPos, mft);
          break;
        case 0x80: // $DATA
          if (attrName == null || attrName.Length == 0) // default data stream only
            ParseDataAttribute(record, attrPos, nonResident, mft);
          break;
        case 0x90: // $INDEX_ROOT
          ParseIndexRoot(record, attrPos, mft);
          break;
        case 0xA0: // $INDEX_ALLOCATION — non-resident INDX blocks for large dirs
          if (nonResident != 0 && attrPos + 34 <= record.Length) {
            var dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 32));
            mft.IndexAllocationRuns = ParseDataRuns(record, attrPos + dataRunsOffset);
          }
          break;
      }

      attrPos += (int)attrLen;
    }

    return mft;
  }

  private static void ApplyFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));

    if (usaOffset + usaCount * 2 > record.Length || usaCount < 2) return;

    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));

    for (var i = 1; i < usaCount; i++) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;

      // Verify the last 2 bytes of each sector match the USN
      var actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd));
      if (actual != usn) continue; // skip if mismatch

      // Restore original bytes from the update sequence array
      var replacement = record.AsSpan(usaOffset + i * 2, 2);
      replacement.CopyTo(record.AsSpan(sectorEnd));
    }
  }

  private static void ParseFileName(byte[] record, int attrPos, MftRecord mft) {
    var valueLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 16));
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 20));
    var dataStart = attrPos + valueOffset;

    if (dataStart + 66 > record.Length) return;

    var parentRef = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(dataStart));
    var parentRecord = (uint)(parentRef & 0x0000FFFFFFFFFFFF);

    var nameLength = record[dataStart + 64];
    var nameSpace = record[dataStart + 65];

    if (dataStart + 66 + nameLength * 2 > record.Length) return;
    var name = Encoding.Unicode.GetString(record, dataStart + 66, nameLength * 2);

    // Prefer Win32 or Win32+DOS names; skip pure DOS names if we already have a name
    if (nameSpace == 2 && mft.FileName != null) return; // DOS-only namespace, skip

    mft.FileName = name;
    mft.ParentRecord = parentRecord;

    // Parse timestamps (creation time at offset 8, modification at offset 16)
    if (dataStart + 32 <= record.Length) {
      var modTicks = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(dataStart + 24));
      if (modTicks > 0) {
        try {
          mft.LastModified = DateTime.FromFileTimeUtc(modTicks);
        } catch { /* ignore invalid timestamps */ }
      }
    }

    // File size from $FILE_NAME (allocated and real size)
    if (dataStart + 56 <= record.Length)
      mft.FileNameSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(dataStart + 48));
  }

  private static void ParseDataAttribute(byte[] record, int attrPos, byte nonResident, MftRecord mft) {
    if (nonResident == 0) {
      // Resident data
      var valueLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 16));
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 20));
      var dataStart = attrPos + valueOffset;

      if (dataStart + valueLen <= record.Length) {
        mft.ResidentData = record.AsSpan(dataStart, (int)valueLen).ToArray();
        mft.DataSize = valueLen;
        mft.IsResident = true;
      }
    } else {
      // Non-resident data
      mft.IsResident = false;

      // Attribute flags (offset 12): 0x0001 = compressed. The compression-unit
      // size (offset 34) is the base-2 log of clusters per unit.
      var attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 12));
      mft.Compressed = (attrFlags & 0x0001) != 0;

      if (attrPos + 56 <= record.Length)
        mft.DataSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attrPos + 48));

      // Parse data runs
      if (attrPos + 34 <= record.Length) {
        if (mft.Compressed) {
          var unitLog2 = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 34));
          mft.CompressionUnitClusters = unitLog2 > 0 ? 1 << unitLog2 : 16;
        }
        var dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 32));
        mft.DataRuns = ParseDataRuns(record, attrPos + dataRunsOffset);
      }
    }
  }

  private static void ParseIndexRoot(byte[] record, int attrPos, MftRecord mft) {
    // Just flag that this record has an index root — directory listing from $FILE_NAME refs
    var valueLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 16));
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 20));
    var dataStart = attrPos + valueOffset;

    if (dataStart + 16 > record.Length) return;

    // Index root header: attribute type (4), collation rule (4), index alloc entry size (4), clusters per index block (1)
    // Then index header: entries offset (4), total size (4), allocated size (4), flags (4)
    var entriesOffset = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(dataStart + 16));
    var totalSize = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(dataStart + 20));

    // Bytes per INDX block, advertised in the index-root header (offset 8). Used
    // to step through the $INDEX_ALLOCATION stream when the index is large.
    mft.IndexBlockSize = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(dataStart + 8));

    var indexStart = dataStart + 16 + entriesOffset;
    var indexEnd = dataStart + 16 + totalSize;

    mft.IndexEntryRefs = [];

    while (indexStart + 16 <= indexEnd && indexStart + 16 <= record.Length) {
      var mftRef = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(indexStart));
      var entryLen = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(indexStart + 8));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(indexStart + 12));

      if (entryLen < 16) break;

      // Real FILE_NAME entries (ref != 0) contribute a child; pure routing
      // pointers (ref 0) only steer the descent into a subnode.
      var refRecord = (uint)(mftRef & 0x0000FFFFFFFFFFFF);
      if (refRecord > 0)
        mft.IndexEntryRefs.Add(refRecord);

      if ((flags & 0x02) != 0) break; // last entry (may still carry a subnode VCN, read above)

      indexStart += entryLen;
    }
  }

  // Walks a directory's $INDEX_ALLOCATION: reads each referenced INDX block,
  // undoes its USA fixups and collects the MFT references of every FILE_NAME
  // entry it holds. A single B+tree level is produced by the writer, so the
  // subnode VCNs from the root point directly at leaf blocks; we additionally
  // read every block the allocation stream covers so no leaf is missed.
  private void CollectIndexAllocationRefs(MftRecord dir) {
    if (dir.IndexAllocationRuns == null || dir.IndexAllocationRuns.Count == 0) return;
    dir.IndexEntryRefs ??= [];

    var blockSize = dir.IndexBlockSize > 0 ? dir.IndexBlockSize : 4096;

    foreach (var run in dir.IndexAllocationRuns) {
      var runBytes = run.ClusterCount * _clusterSize;
      var blocksInRun = runBytes / blockSize;
      for (long b = 0; b < blocksInRun; b++) {
        var byteOffset = run.Lcn * _clusterSize + b * blockSize;
        if (byteOffset < 0 || byteOffset + blockSize > _data.Length) continue;
        ReadIndexBlock(byteOffset, blockSize, dir.IndexEntryRefs);
      }
    }
  }

  // Reads one INDX block: validates the magic, undoes USA fixups, then walks its
  // index entries adding each non-zero MFT reference to refs.
  private void ReadIndexBlock(long offset, int blockSize, List<uint> refs) {
    var block = _data.AsSpan((int)offset, blockSize).ToArray();
    if (block[0] != (byte)'I' || block[1] != (byte)'N' || block[2] != (byte)'D' || block[3] != (byte)'X')
      return;

    ApplyFixup(block);

    // Index sub-header lives right after the USA, 8-byte aligned. Its entries
    // offset is relative to the sub-header start.
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(6));
    var subHeaderOffset = (usaOffset + usaCount * 2 + 7) & ~7;
    if (subHeaderOffset + 16 > block.Length) return;

    var entriesOffset = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(subHeaderOffset));
    var indexContentSize = BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(subHeaderOffset + 4));

    var entryPos = subHeaderOffset + entriesOffset;
    var entryEnd = subHeaderOffset + indexContentSize;

    while (entryPos + 16 <= entryEnd && entryPos + 16 <= block.Length) {
      var mftRef = BinaryPrimitives.ReadInt64LittleEndian(block.AsSpan(entryPos));
      var entryLen = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(entryPos + 8));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(entryPos + 12));

      if (entryLen < 16) break;

      var refRecord = (uint)(mftRef & 0x0000FFFFFFFFFFFF);
      if (refRecord > 0)
        refs.Add(refRecord);

      if ((flags & 0x02) != 0) break; // last entry in this block

      entryPos += entryLen;
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

      // Read length (unsigned)
      long length = 0;
      for (var i = 0; i < lengthBytes; i++)
        length |= (long)record[offset + i] << (i * 8);
      offset += lengthBytes;

      // A run with no offset field (offsetBytes == 0) is sparse — a hole that
      // reads as zeros. The previous-LCN cursor is NOT advanced across it.
      if (offsetBytes == 0) {
        runs.Add(new DataRun { Lcn = 0, ClusterCount = length, Sparse = true });
        continue;
      }

      // Read offset (signed, relative)
      long clusterOffset = 0;
      for (var i = 0; i < offsetBytes; i++)
        clusterOffset |= (long)record[offset + i] << (i * 8);
      // Sign extend
      if ((record[offset + offsetBytes - 1] & 0x80) != 0) {
        for (var i = offsetBytes; i < 8; i++)
          clusterOffset |= (long)0xFF << (i * 8);
      }
      offset += offsetBytes;

      var lcn = previousLcn + clusterOffset;
      runs.Add(new DataRun { Lcn = lcn, ClusterCount = length });
      previousLcn = lcn;
    }

    return runs;
  }

  private void EnumerateDirectory(uint dirRecord, string path) {
    if (!_mftRecords.TryGetValue(dirRecord, out var dir)) return;

    // Collect all MFT records that reference this directory as parent
    var childRefs = new HashSet<uint>();

    // From index entries — skip system MFT records (0..15) which may appear in
    // root's INDEX_ROOT when the writer emits all 16 reserved system files.
    if (dir.IndexEntryRefs != null) {
      foreach (var r in dir.IndexEntryRefs)
        if (r > 15)
          childRefs.Add(r);
    }

    // Also scan all records for those with this parent
    foreach (var (recNum, rec) in _mftRecords) {
      if (recNum <= 15) continue; // skip system records
      if (rec.ParentRecord == dirRecord)
        childRefs.Add(recNum);
    }

    foreach (var childRecNum in childRefs) {
      if (!_mftRecords.TryGetValue(childRecNum, out var child)) continue;
      if (child.FileName == null) continue;

      var fullPath = string.IsNullOrEmpty(path) ? child.FileName : $"{path}/{child.FileName}";

      var size = child.DataSize;
      if (size == 0 && child.FileNameSize > 0)
        size = child.FileNameSize;

      _entries.Add(new NtfsEntry {
        Name = fullPath,
        Size = size,
        IsDirectory = child.IsDirectory,
        LastModified = child.LastModified,
        MftRecord = childRecNum,
      });

      if (child.IsDirectory)
        EnumerateDirectory(childRecNum, fullPath);
    }
  }

  /// <summary>Extracts a file's data from the NTFS image.</summary>
  public byte[] Extract(NtfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];

    if (!_mftRecords.TryGetValue(entry.MftRecord, out var mft))
      return [];

    if (mft.IsResident && mft.ResidentData != null)
      return mft.ResidentData;

    if (mft.DataRuns == null || mft.DataRuns.Count == 0)
      return [];

    if (mft.Compressed)
      return ExtractCompressed(mft);

    // Read data from non-resident runs
    using var ms = new MemoryStream();
    foreach (var run in mft.DataRuns) {
      var clusterOffset = run.Lcn * _clusterSize;
      var runBytes = (int)(run.ClusterCount * _clusterSize);

      if (run.Sparse) {
        // Hole: contributes zeros for the whole run span.
        ms.Write(new byte[runBytes]);
        continue;
      }

      if (clusterOffset + runBytes > _data.Length)
        runBytes = (int)Math.Max(0, _data.Length - clusterOffset);

      if (runBytes > 0)
        ms.Write(_data, (int)clusterOffset, runBytes);
    }

    var result = ms.ToArray();
    // Trim to actual file size
    if (mft.DataSize > 0 && result.Length > mft.DataSize)
      return result.AsSpan(0, (int)mft.DataSize).ToArray();
    return result;
  }

  // Extracts an LZNT1-compressed $DATA stream. The runs are walked VCN-by-VCN in
  // compression-unit-sized windows (CompressionUnitClusters clusters each). A
  // unit whose window contains a sparse tail was LZNT1-compressed: its real
  // clusters hold the compressed chunk stream, decompressed to fill the unit's
  // logical span. A fully-allocated unit (no sparse tail) was stored raw and is
  // copied straight through. The final partial unit may be shorter than a full
  // compression unit.
  private byte[] ExtractCompressed(MftRecord mft) {
    var unitClusters = mft.CompressionUnitClusters > 0 ? mft.CompressionUnitClusters : 16;
    var unitBytes = unitClusters * _clusterSize;

    // Flatten the runs into a per-VCN map of (Lcn or sparse) so we can slice
    // arbitrary unit windows regardless of how runs straddle unit boundaries.
    var runs = mft.DataRuns!;
    long totalVcns = 0;
    foreach (var r in runs) totalVcns += r.ClusterCount;

    using var output = new MemoryStream();
    long vcn = 0;
    var runIndex = 0;
    long runStartVcn = 0;

    while (vcn < totalVcns && output.Length < mft.DataSize) {
      var windowClusters = (int)Math.Min(unitClusters, totalVcns - vcn);

      // Gather this window's clusters: collect real cluster bytes and detect a
      // sparse tail. Walk the run list spanning [vcn, vcn+windowClusters).
      var realBytes = new MemoryStream();
      var hasSparse = false;
      var collected = 0;
      // Advance runIndex/runStartVcn to the run containing `vcn`.
      while (runIndex < runs.Count && runStartVcn + runs[runIndex].ClusterCount <= vcn) {
        runStartVcn += runs[runIndex].ClusterCount;
        runIndex++;
      }
      var scanIndex = runIndex;
      var scanStart = runStartVcn;
      while (collected < windowClusters && scanIndex < runs.Count) {
        var run = runs[scanIndex];
        var withinRunStart = Math.Max(0, vcn + collected - scanStart);
        var available = run.ClusterCount - withinRunStart;
        var take = (int)Math.Min(available, windowClusters - collected);
        if (run.Sparse) {
          hasSparse = true;
        } else {
          var byteOffset = (run.Lcn + withinRunStart) * _clusterSize;
          var byteLen = take * _clusterSize;
          if (byteOffset >= 0 && byteOffset + byteLen <= _data.Length)
            realBytes.Write(_data, (int)byteOffset, (int)byteLen);
        }
        collected += take;
        if (withinRunStart + take >= run.ClusterCount) {
          scanStart += run.ClusterCount;
          scanIndex++;
        }
      }

      var unitLogicalBytes = (int)Math.Min(unitBytes, mft.DataSize - output.Length);
      var raw = realBytes.ToArray();
      if (hasSparse) {
        // Compressed unit: decompress the real-cluster chunk stream.
        var decompressed = Lznt1.Decompress(raw, unitLogicalBytes);
        output.Write(decompressed, 0, Math.Min(decompressed.Length, unitLogicalBytes));
        // Pad with zeros if decompression produced fewer bytes than the unit's
        // logical span (defensive; should not happen for our own output).
        if (decompressed.Length < unitLogicalBytes)
          output.Write(new byte[unitLogicalBytes - decompressed.Length]);
      } else {
        // Raw unit: copy straight through, trimmed to the logical span.
        output.Write(raw, 0, Math.Min(raw.Length, unitLogicalBytes));
        if (raw.Length < unitLogicalBytes)
          output.Write(new byte[unitLogicalBytes - raw.Length]);
      }

      vcn += windowClusters;
    }

    var result = output.ToArray();
    if (mft.DataSize > 0 && result.Length > mft.DataSize)
      return result.AsSpan(0, (int)mft.DataSize).ToArray();
    return result;
  }

  public void Dispose() { }

  private sealed class MftRecord {
    public uint RecordNumber;
    public string? FileName;
    public uint ParentRecord;
    public bool IsDirectory;
    public ushort Flags;
    public DateTime? LastModified;
    public long FileNameSize;

    // Data attribute
    public bool IsResident;
    public byte[]? ResidentData;
    public long DataSize;
    public List<DataRun>? DataRuns;
    public bool Compressed;
    public int CompressionUnitClusters;

    // Index
    public List<uint>? IndexEntryRefs;
    public List<DataRun>? IndexAllocationRuns;
    public int IndexBlockSize;
  }

  private sealed class DataRun {
    public long Lcn;
    public long ClusterCount;
    public bool Sparse;
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Reads the native file-reference identity needed by the mounted NTFS view.
/// A reference is the 48-bit MFT segment number plus the 16-bit sequence number;
/// both $FILE_NAME parent references and directory-index child references are
/// validated before a namespace is published. The same FILE-record pass also
/// derives direct resident/non-resident data layouts for mounted positional reads.
/// </summary>
internal sealed class NtfsMountIdentityScanner {
  private const uint AttributeTypeData = 0x80;
  private const uint AttributeTypeFileName = 0x30;
  private const uint AttributeTypeIndexRoot = 0x90;
  private const uint AttributeTypeIndexAllocation = 0xA0;
  private const uint AttributeEnd = 0xFFFFFFFF;
  private const ushort AttributeFlagCompressed = 0x0001;
  private const ushort AttributeFlagEncrypted = 0x4000;

  private readonly Stream _image;
  private readonly NtfsDriverGeometry _geometry;
  private readonly int _clusterSize;
  private readonly List<MftRun> _mftRuns;
  private readonly long _mftDataSize;
  private readonly Dictionary<uint, RecordIdentity> _records = [];

  private NtfsMountIdentityScanner(Stream image, NtfsDriverGeometry geometry) {
    _image = image;
    _geometry = geometry;
    _clusterSize = geometry.ClusterSize;

    var record0 = ReadPhysicalRecord(checked(geometry.MftCluster * (long)_clusterSize));
    ApplyFixups(record0, "MFT record 0");
    ValidateFileRecord(record0, 0);
    (_mftRuns, _mftDataSize) = ParseMftDataMap(record0);
  }

  internal static NtfsMountIdentityMap Read(
      Stream image,
      NtfsDriverGeometry geometry,
      IReadOnlyList<NtfsEntry> decodedEntries) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(decodedEntries);

    var original = image.Position;
    try {
      var scanner = new NtfsMountIdentityScanner(image, geometry);
      return scanner.Build(decodedEntries);
    } finally {
      image.Position = original;
    }
  }

  private NtfsMountIdentityMap Build(IReadOnlyList<NtfsEntry> decodedEntries) {
    var root = ReadIdentity(5);
    var pathToRecord = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) {
      [string.Empty] = 5,
    };
    var directoryRecords = new HashSet<uint> { 5 };
    var identities = new List<NtfsMountedEntryIdentity>(decodedEntries.Count);

    foreach (var entry in decodedEntries
      .OrderBy(static entry => Depth(entry.Name))
      .ThenBy(static entry => Normalize(entry.Name), StringComparer.OrdinalIgnoreCase)) {
      if (entry.MftRecord <= 15)
        throw new InvalidDataException(
          $"NTFS user namespace unexpectedly exposed reserved MFT record {entry.MftRecord} as '{entry.Name}'.");

      var path = Normalize(entry.Name);
      if (path.Length == 0)
        throw new InvalidDataException("NTFS namespace exposed an empty path.");
      var slash = path.LastIndexOf('/');
      var parentPath = slash < 0 ? string.Empty : path[..slash];
      var leafName = slash < 0 ? path : path[(slash + 1)..];
      if (!pathToRecord.TryGetValue(parentPath, out var expectedParentRecord))
        throw new InvalidDataException($"NTFS entry '{path}' has no decoded parent '{parentPath}'.");

      var identity = ReadIdentity(entry.MftRecord);
      var fileName = FindMatchingFileName(identity, expectedParentRecord, leafName);
      var parentIdentity = ReadIdentity(fileName.ParentRecord);
      ValidateReferenceSequence(
        fileName.ParentRecord,
        fileName.ParentSequence,
        parentIdentity.Sequence,
        $"$FILE_NAME parent reference for '{path}'");

      NtfsMountedDataLayout? dataLayout = null;
      if (!entry.IsDirectory && !entry.IsSymlink)
        dataLayout = ParseDataLayout(identity.Record, entry.MftRecord, entry.Size);

      identities.Add(new NtfsMountedEntryIdentity(
        entry,
        identity.Sequence,
        identity.HardLinkCount,
        dataLayout));
      if (entry.IsDirectory) {
        pathToRecord[path] = entry.MftRecord;
        directoryRecords.Add(entry.MftRecord);
      }
    }

    // A directory index stores complete NTFS file references, not merely record
    // numbers. Validate the sequence component as well; otherwise an index entry
    // left behind after MFT-slot reuse could silently resolve to an unrelated file.
    foreach (var directoryRecord in directoryRecords)
      ValidateDirectoryIndexReferences(directoryRecord);

    return new NtfsMountIdentityMap(root.Sequence, root.HardLinkCount, identities.ToArray());
  }

  private static FileNameReference FindMatchingFileName(
      RecordIdentity identity,
      uint expectedParentRecord,
      string leafName) {
    var matches = identity.FileNames
      .Where(fileName => fileName.ParentRecord == expectedParentRecord)
      .Where(fileName => string.Equals(fileName.Name, leafName, StringComparison.OrdinalIgnoreCase))
      .ToArray();
    if (matches.Length == 0)
      throw new InvalidDataException(
        $"NTFS MFT record {identity.RecordNumber} has no $FILE_NAME matching decoded alias '{leafName}' in parent {expectedParentRecord}.");

    return matches.FirstOrDefault(static fileName => fileName.Namespace != 2, matches[0]);
  }

  private RecordIdentity ReadIdentity(uint recordNumber) {
    if (_records.TryGetValue(recordNumber, out var cached))
      return cached;

    var record = ReadMappedRecord(recordNumber);
    ApplyFixups(record, $"MFT record {recordNumber}");
    ValidateFileRecord(record, recordNumber);

    var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
    if ((flags & 0x0001) == 0)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} is referenced by the live namespace but is not in use.");

    var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(16));
    var hardLinks = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(18));
    var fileNames = ParseFileNames(record, recordNumber);
    var result = new RecordIdentity(recordNumber, sequence, hardLinks, fileNames, record);
    _records.Add(recordNumber, result);
    return result;
  }

  private void ValidateDirectoryIndexReferences(uint directoryRecordNumber) {
    var record = ReadMappedRecord(directoryRecordNumber);
    ApplyFixups(record, $"directory MFT record {directoryRecordNumber}");
    ValidateFileRecord(record, directoryRecordNumber);

    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
    ValidateAttributeBounds(record, directoryRecordNumber, firstAttribute, used);

    List<MftRun>? indexAllocationRuns = null;
    var indexBlockSize = 0;
    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
      if (type == AttributeEnd) break;
      var length = ReadAttributeLength(record, directoryRecordNumber, position, used);
      var nonResident = record[position + 8] != 0;
      var nameLength = record[position + 9];
      var name = ReadAttributeName(record, position, length, nameLength);

      if (type == AttributeTypeIndexRoot && !nonResident && IsI30(name)) {
        var (valueOffset, valueLength) = ReadResidentValueBounds(record, directoryRecordNumber, position, length);
        var value = record.AsSpan(position + valueOffset, valueLength);
        if (value.Length < 32)
          throw new InvalidDataException($"NTFS directory {directoryRecordNumber} has a truncated $I30 $INDEX_ROOT.");
        indexBlockSize = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(8, 4));
        ValidateIndexEntries(
          value,
          indexHeaderOffset: 16,
          $"directory {directoryRecordNumber} resident $I30 index");
      } else if (type == AttributeTypeIndexAllocation && nonResident && IsI30(name)) {
        if (length < 64)
          throw new InvalidDataException($"NTFS directory {directoryRecordNumber} has a truncated $I30 $INDEX_ALLOCATION attribute.");
        var mappingOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 32));
        if (mappingOffset < 64 || mappingOffset >= length)
          throw new InvalidDataException($"NTFS directory {directoryRecordNumber} has invalid $I30 mapping-pairs offset.");
        indexAllocationRuns = ParseRuns(record.AsSpan(position + mappingOffset, length - mappingOffset));
      }

      position += length;
    }

    if (indexAllocationRuns is null || indexAllocationRuns.Count == 0)
      return;
    if (indexBlockSize <= 0 || (indexBlockSize & (indexBlockSize - 1)) != 0 || indexBlockSize > 1 << 20)
      throw new InvalidDataException(
        $"NTFS directory {directoryRecordNumber} has invalid $I30 index block size {indexBlockSize}.");

    foreach (var run in indexAllocationRuns) {
      if (run.Sparse)
        throw new InvalidDataException($"NTFS directory {directoryRecordNumber} has a sparse $I30 $INDEX_ALLOCATION run.");
      var runBytes = checked(run.ClusterCount * (long)_clusterSize);
      if (runBytes % indexBlockSize != 0)
        throw new InvalidDataException(
          $"NTFS directory {directoryRecordNumber} $I30 run is not an integral number of index blocks.");

      var blockCount = checked((int)(runBytes / indexBlockSize));
      for (var blockIndex = 0; blockIndex < blockCount; ++blockIndex) {
        var physical = checked(run.Lcn * (long)_clusterSize + (long)blockIndex * indexBlockSize);
        var block = new byte[indexBlockSize];
        ReadExactlyAt(physical, block);
        if (!block.AsSpan(0, 4).SequenceEqual("INDX"u8))
          throw new InvalidDataException(
            $"NTFS directory {directoryRecordNumber} $I30 block {blockIndex} has invalid INDX signature.");
        ApplyFixups(block, $"directory {directoryRecordNumber} INDX block {blockIndex}");
        ValidateIndexEntries(
          block,
          indexHeaderOffset: 24,
          $"directory {directoryRecordNumber} INDX block {blockIndex}");
      }
    }
  }

  private void ValidateIndexEntries(ReadOnlySpan<byte> buffer, int indexHeaderOffset, string context) {
    if (indexHeaderOffset < 0 || indexHeaderOffset > buffer.Length - 16)
      throw new InvalidDataException($"NTFS {context} has a truncated INDEX_HEADER.");
    var entriesOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset, 4));
    var indexLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset + 4, 4));
    var allocatedLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset + 8, 4));
    if (indexLength < entriesOffset || allocatedLength < indexLength)
      throw new InvalidDataException($"NTFS {context} has inconsistent INDEX_HEADER lengths.");

    var start64 = (long)indexHeaderOffset + entriesOffset;
    var end64 = (long)indexHeaderOffset + indexLength;
    if (start64 < 0 || end64 < start64 || end64 > buffer.Length)
      throw new InvalidDataException($"NTFS {context} index entries lie outside the containing attribute/block.");

    var position = checked((int)start64);
    var end = checked((int)end64);
    var sawLast = false;
    while (position < end) {
      if (position > end - 16)
        throw new InvalidDataException($"NTFS {context} ends with a truncated index entry.");
      var rawReference = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(position, 8));
      var entryLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position + 8, 2));
      var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position + 10, 2));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position + 12, 2));
      if (entryLength < 16 || (entryLength & 7) != 0 || position > end - entryLength)
        throw new InvalidDataException($"NTFS {context} contains an invalid index-entry length {entryLength}.");
      if (keyLength > entryLength - 16)
        throw new InvalidDataException($"NTFS {context} contains an index key longer than its entry.");

      var isLast = (flags & 0x0002) != 0;
      if (rawReference != 0 && !isLast) {
        var record64 = rawReference & 0x0000FFFFFFFFFFFFUL;
        if (record64 > uint.MaxValue)
          throw new NotSupportedException(
            $"NTFS {context} references MFT segment {record64}, beyond the mounted reader's 32-bit record range.");
        var sequence = checked((ushort)(rawReference >> 48));
        var live = ReadIdentity((uint)record64);
        ValidateReferenceSequence(
          (uint)record64,
          sequence,
          live.Sequence,
          $"{context} child reference");
      }

      position += entryLength;
      if (isLast) {
        sawLast = true;
        if (position != end && buffer[position..end].IndexOfAnyExcept((byte)0) >= 0)
          throw new InvalidDataException($"NTFS {context} has non-zero data after its terminal index entry.");
        break;
      }
    }

    if (!sawLast)
      throw new InvalidDataException($"NTFS {context} has no terminal index entry.");
  }

  private static void ValidateReferenceSequence(
      uint recordNumber,
      ushort referencedSequence,
      ushort liveSequence,
      string context) {
    // Sequence zero appears in some old/synthetic structures and means that no
    // generation check is available. A non-zero value is an actual stale-ref guard.
    if (referencedSequence != 0 && referencedSequence != liveSequence)
      throw new InvalidDataException(
        $"NTFS {context} references MFT {recordNumber} sequence {referencedSequence}, " +
        $"but the live FILE record has sequence {liveSequence}; the file reference is stale.");
  }

  private byte[] ReadMappedRecord(uint recordNumber) {
    var logicalOffset = checked((long)recordNumber * _geometry.MftRecordSize);
    if (logicalOffset < 0 || logicalOffset > _mftDataSize - _geometry.MftRecordSize)
      throw new InvalidDataException(
        $"NTFS MFT record {recordNumber} lies outside the live $MFT data stream ({_mftDataSize:N0} bytes).");

    var result = new byte[_geometry.MftRecordSize];
    var destination = result.AsSpan();
    var logical = logicalOffset;
    var copied = 0;

    foreach (var run in _mftRuns) {
      var runStart = checked(run.Vcn * (long)_clusterSize);
      var runLength = checked(run.ClusterCount * (long)_clusterSize);
      var runEnd = checked(runStart + runLength);
      if (logical >= runEnd) continue;
      if (logical < runStart) break;
      if (run.Sparse)
        throw new InvalidDataException("NTFS $MFT contains a sparse data run, which cannot hold live FILE records.");

      var withinRun = logical - runStart;
      var take = checked((int)Math.Min(destination.Length - copied, runLength - withinRun));
      var physical = checked(run.Lcn * (long)_clusterSize + withinRun);
      ReadExactlyAt(physical, destination.Slice(copied, take));
      logical += take;
      copied += take;
      if (copied == destination.Length) return result;
    }

    throw new InvalidDataException($"NTFS $MFT data runs do not map all bytes of record {recordNumber}.");
  }

  private byte[] ReadPhysicalRecord(long physicalOffset) {
    var result = new byte[_geometry.MftRecordSize];
    ReadExactlyAt(physicalOffset, result);
    return result;
  }

  private void ReadExactlyAt(long offset, Span<byte> destination) {
    if (offset < 0 || offset > _image.Length - destination.Length)
      throw new InvalidDataException("NTFS metadata read lies outside the backing image.");
    _image.Position = offset;
    _image.ReadExactly(destination);
  }

  private void ApplyFixups(byte[] record, string context) {
    if (record.Length < 48)
      throw new InvalidDataException($"NTFS {context} is shorter than its fixed multi-sector header.");
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    var expectedSectors = checked(record.Length / _geometry.BytesPerSector);
    if (record.Length % _geometry.BytesPerSector != 0 || usaCount != expectedSectors + 1)
      throw new InvalidDataException(
        $"NTFS {context} has USA count {usaCount}, expected {expectedSectors + 1} for {_geometry.BytesPerSector}-byte sectors.");
    if (usaOffset < 8 || usaOffset > record.Length - checked(usaCount * 2))
      throw new InvalidDataException($"NTFS {context} update-sequence array lies outside the record.");

    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
    for (var sector = 0; sector < expectedSectors; ++sector) {
      var trailer = checked((sector + 1) * _geometry.BytesPerSector - 2);
      var actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(trailer));
      if (actual != usn)
        throw new InvalidDataException(
          $"NTFS {context} update-sequence mismatch in sector {sector}: found 0x{actual:X4}, expected 0x{usn:X4}.");
      record.AsSpan(usaOffset + (sector + 1) * 2, 2).CopyTo(record.AsSpan(trailer, 2));
    }
  }

  private static void ValidateFileRecord(byte[] record, uint expectedRecordNumber) {
    if (!record.AsSpan(0, 4).SequenceEqual("FILE"u8))
      throw new InvalidDataException($"NTFS MFT record {expectedRecordNumber} has no FILE signature.");
    var recordedNumber = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(44));
    if (recordedNumber != 0 && recordedNumber != expectedRecordNumber)
      throw new InvalidDataException(
        $"NTFS FILE header says MFT record {recordedNumber}, but the $MFT mapping selected record {expectedRecordNumber}.");
  }

  private static (List<MftRun> Runs, long DataSize) ParseMftDataMap(byte[] record0) {
    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(24)));
    ValidateAttributeBounds(record0, 0, firstAttribute, used);

    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(position));
      if (type == AttributeEnd) break;
      var length = ReadAttributeLength(record0, 0, position, used);
      var nonResident = record0[position + 8] != 0;
      var nameLength = record0[position + 9];
      if (type == AttributeTypeData && nameLength == 0) {
        if (!nonResident || length < 64)
          throw new NotSupportedException("NTFS mounted identity requires a non-resident unnamed $MFT::$DATA attribute.");
        var mappingOffset = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(position + 32));
        if (mappingOffset < 64 || mappingOffset >= length)
          throw new InvalidDataException("NTFS $MFT::$DATA mapping-pairs offset is invalid.");
        var dataSize = BinaryPrimitives.ReadInt64LittleEndian(record0.AsSpan(position + 48));
        if (dataSize <= 0)
          throw new InvalidDataException("NTFS $MFT::$DATA has a non-positive logical size.");
        var runs = ParseRuns(record0.AsSpan(position + mappingOffset, length - mappingOffset));
        if (runs.Count == 0)
          throw new InvalidDataException("NTFS $MFT::$DATA has no mapping pairs.");
        return (runs, dataSize);
      }
      position += length;
    }

    throw new NotSupportedException("NTFS mounted identity could not find the unnamed $MFT::$DATA attribute in record 0.");
  }

  private NtfsMountedDataLayout ParseDataLayout(byte[] record, uint recordNumber, long decodedSize) {
    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
    ValidateAttributeBounds(record, recordNumber, firstAttribute, used);

    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
      if (type == AttributeEnd) break;
      var length = ReadAttributeLength(record, recordNumber, position, used);
      var nameLength = record[position + 9];
      var name = ReadAttributeName(record, position, length, nameLength);
      if (type != AttributeTypeData || name is not null) {
        position += length;
        continue;
      }

      var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 12));
      if ((flags & AttributeFlagEncrypted) != 0)
        throw new NotSupportedException($"NTFS MFT record {recordNumber} has encrypted unnamed $DATA; mounted plaintext reads are not available.");

      var nonResident = record[position + 8] != 0;
      if (!nonResident) {
        if ((flags & AttributeFlagCompressed) != 0)
          throw new InvalidDataException($"NTFS MFT record {recordNumber} marks resident $DATA as compressed.");
        var (valueOffset, valueLength) = ReadResidentValueBounds(record, recordNumber, position, length);
        if (decodedSize != valueLength)
          throw new InvalidDataException(
            $"NTFS MFT record {recordNumber} resident $DATA length {valueLength} disagrees with decoded size {decodedSize}.");
        var resident = record.AsSpan(position + valueOffset, valueLength).ToArray();
        return new NtfsMountedDataLayout(valueLength, valueLength, resident, [], Compressed: false);
      }

      if (length < 64)
        throw new InvalidDataException($"NTFS MFT record {recordNumber} has a truncated non-resident $DATA header.");
      var mappingOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 32));
      if (mappingOffset < 64 || mappingOffset >= length)
        throw new InvalidDataException($"NTFS MFT record {recordNumber} has an invalid $DATA mapping-pairs offset.");
      var dataLength = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(position + 48));
      var initializedLength = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(position + 56));
      if (dataLength < 0 || initializedLength < 0 || initializedLength > dataLength)
        throw new InvalidDataException($"NTFS MFT record {recordNumber} has invalid data/initialized lengths.");
      if (dataLength != decodedSize)
        throw new InvalidDataException(
          $"NTFS MFT record {recordNumber} $DATA length {dataLength} disagrees with decoded size {decodedSize}.");

      var runs = ParseRuns(record.AsSpan(position + mappingOffset, length - mappingOffset));
      var mappedClusters = runs.Count == 0 ? 0L : checked(runs[^1].Vcn + runs[^1].ClusterCount);
      var mappedBytes = checked(mappedClusters * (long)_clusterSize);
      if (dataLength > mappedBytes)
        throw new InvalidDataException(
          $"NTFS MFT record {recordNumber} maps only {mappedBytes} bytes for a {dataLength}-byte stream.");

      foreach (var run in runs) {
        if (run.Sparse) continue;
        var physical = checked(run.Lcn * (long)_clusterSize);
        var byteLength = checked(run.ClusterCount * (long)_clusterSize);
        if (physical < 0 || physical > _image.Length - byteLength)
          throw new InvalidDataException(
            $"NTFS MFT record {recordNumber} has a $DATA run outside the backing image.");
      }

      return new NtfsMountedDataLayout(
        dataLength,
        initializedLength,
        ResidentData: null,
        runs.Select(static run => new NtfsMountedDataRun(run.Vcn, run.Lcn, run.ClusterCount, run.Sparse)).ToArray(),
        Compressed: (flags & AttributeFlagCompressed) != 0);
    }

    if (decodedSize == 0)
      return new NtfsMountedDataLayout(0, 0, [], [], Compressed: false);
    throw new NotSupportedException(
      $"NTFS MFT record {recordNumber} has no unnamed base-record $DATA attribute; attribute-list continuation is not yet available to the mounted reader.");
  }

  private static List<MftRun> ParseRuns(ReadOnlySpan<byte> mappingPairs) {
    var runs = new List<MftRun>();
    long previousLcn = 0;
    long vcn = 0;
    var position = 0;

    while (position < mappingPairs.Length) {
      var header = mappingPairs[position++];
      if (header == 0) break;
      var lengthBytes = header & 0x0F;
      var offsetBytes = header >> 4;
      if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || position > mappingPairs.Length - lengthBytes - offsetBytes)
        throw new InvalidDataException("NTFS mapping pairs are malformed.");

      var count = ReadUnsigned(mappingPairs.Slice(position, lengthBytes));
      position += lengthBytes;
      if (count == 0 || count > long.MaxValue)
        throw new InvalidDataException("NTFS mapping pairs contain an invalid zero/oversized run length.");
      var clusterCount = checked((long)count);

      if (offsetBytes == 0) {
        runs.Add(new MftRun(vcn, 0, clusterCount, Sparse: true));
      } else {
        var delta = ReadSigned(mappingPairs.Slice(position, offsetBytes));
        position += offsetBytes;
        previousLcn = checked(previousLcn + delta);
        if (previousLcn < 0)
          throw new InvalidDataException("NTFS mapping pairs resolve to a negative LCN.");
        runs.Add(new MftRun(vcn, previousLcn, clusterCount, Sparse: false));
      }
      vcn = checked(vcn + clusterCount);
    }

    return runs;
  }

  private static FileNameReference[] ParseFileNames(byte[] record, uint recordNumber) {
    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
    ValidateAttributeBounds(record, recordNumber, firstAttribute, used);

    var result = new List<FileNameReference>();
    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
      if (type == AttributeEnd) break;
      var length = ReadAttributeLength(record, recordNumber, position, used);

      if (type == AttributeTypeFileName) {
        if (record[position + 8] != 0)
          throw new InvalidDataException($"NTFS MFT record {recordNumber} has a non-resident $FILE_NAME attribute.");
        var (valueOffset, valueLength) = ReadResidentValueBounds(record, recordNumber, position, length);
        if (valueLength < 66)
          throw new InvalidDataException($"NTFS MFT record {recordNumber} has a truncated $FILE_NAME value.");
        var value = record.AsSpan(position + valueOffset, valueLength);
        var rawParent = BinaryPrimitives.ReadUInt64LittleEndian(value);
        var parentRecord64 = rawParent & 0x0000FFFFFFFFFFFFUL;
        if (parentRecord64 > uint.MaxValue)
          throw new NotSupportedException(
            $"NTFS MFT record {recordNumber} references parent segment {parentRecord64}, beyond the current 32-bit MFT reader range.");
        var parentSequence = checked((ushort)(rawParent >> 48));
        var nameLength = value[64];
        var nameSpace = value[65];
        if (nameSpace > 3 || 66 + nameLength * 2 > value.Length)
          throw new InvalidDataException($"NTFS MFT record {recordNumber} has malformed $FILE_NAME text.");
        var name = Encoding.Unicode.GetString(value.Slice(66, nameLength * 2));
        result.Add(new FileNameReference((uint)parentRecord64, parentSequence, nameSpace, name));
      }

      position += length;
    }

    return result.ToArray();
  }

  private static void ValidateAttributeBounds(byte[] record, uint recordNumber, ushort firstAttribute, int used) {
    if (firstAttribute < 24 || used < firstAttribute || used > record.Length)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has invalid attribute bounds.");
  }

  private static int ReadAttributeLength(byte[] record, uint recordNumber, int position, int used) {
    var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4)));
    if (length < 24 || position > used - length)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} contains a malformed attribute record.");
    return length;
  }

  private static (int ValueOffset, int ValueLength) ReadResidentValueBounds(
      byte[] record,
      uint recordNumber,
      int position,
      int attributeLength) {
    var valueLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 16)));
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 20));
    if (valueOffset < 24 || valueLength < 0 || valueOffset > attributeLength - valueLength)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has malformed resident attribute bounds.");
    return (valueOffset, valueLength);
  }

  private static string? ReadAttributeName(byte[] record, int position, int attributeLength, byte nameLength) {
    if (nameLength == 0) return null;
    var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 10));
    var byteLength = checked(nameLength * 2);
    if (nameOffset < 16 || nameOffset > attributeLength - byteLength)
      throw new InvalidDataException("NTFS attribute name lies outside its record.");
    return Encoding.Unicode.GetString(record, position + nameOffset, byteLength);
  }

  private static bool IsI30(string? name) => string.Equals(name, "$I30", StringComparison.Ordinal);

  private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes) {
    ulong value = 0;
    for (var i = 0; i < bytes.Length; ++i)
      value |= (ulong)bytes[i] << (i * 8);
    return value;
  }

  private static long ReadSigned(ReadOnlySpan<byte> bytes) {
    var value = ReadUnsigned(bytes);
    if (bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
      value |= ulong.MaxValue << (bytes.Length * 8);
    return unchecked((long)value);
  }

  private static int Depth(string path) => Normalize(path).Count(static c => c == '/');
  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

  private readonly record struct MftRun(long Vcn, long Lcn, long ClusterCount, bool Sparse);
  private sealed record RecordIdentity(
    uint RecordNumber,
    ushort Sequence,
    ushort HardLinkCount,
    FileNameReference[] FileNames,
    byte[] Record);
  private readonly record struct FileNameReference(
    uint ParentRecord,
    ushort ParentSequence,
    byte Namespace,
    string Name);
}

internal sealed record NtfsMountIdentityMap(
  ushort RootSequence,
  ushort RootHardLinkCount,
  NtfsMountedEntryIdentity[] Entries);

internal readonly record struct NtfsMountedEntryIdentity(
  NtfsEntry Entry,
  ushort Sequence,
  ushort HardLinkCount,
  NtfsMountedDataLayout? DataLayout);
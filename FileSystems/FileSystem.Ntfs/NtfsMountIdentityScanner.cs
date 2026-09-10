#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Reads the parts of FILE records that define an NTFS file reference without
/// duplicating the general namespace/data decoder. A file reference is the
/// 48-bit MFT segment number plus the 16-bit sequence number; the latter is the
/// stale-reference guard and therefore belongs in <c>FilesystemNodeId.Generation</c>.
/// </summary>
internal sealed class NtfsMountIdentityScanner {
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
    ApplyFixups(record0);
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
      if (fileName.ParentSequence != 0 && fileName.ParentSequence != parentIdentity.Sequence)
        throw new InvalidDataException(
          $"NTFS $FILE_NAME for '{path}' references parent MFT {fileName.ParentRecord} sequence {fileName.ParentSequence}, " +
          $"but the live parent record has sequence {parentIdentity.Sequence}; the file reference is stale.");

      identities.Add(new NtfsMountedEntryIdentity(entry, identity.Sequence, identity.HardLinkCount));
      if (entry.IsDirectory)
        pathToRecord[path] = entry.MftRecord;
    }

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

    // Namespace 2 is the DOS-only 8.3 alias. Prefer the Win32/POSIX-visible
    // namespace when both forms happen to compare equal after case folding.
    return matches.FirstOrDefault(static fileName => fileName.Namespace != 2, matches[0]);
  }

  private RecordIdentity ReadIdentity(uint recordNumber) {
    if (_records.TryGetValue(recordNumber, out var cached))
      return cached;

    var record = ReadMappedRecord(recordNumber);
    ApplyFixups(record);
    ValidateFileRecord(record, recordNumber);

    var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(22));
    if ((flags & 0x0001) == 0)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} is referenced by the live namespace but is not in use.");

    var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(16));
    var hardLinks = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(18));
    var fileNames = ParseFileNames(record, recordNumber);
    var result = new RecordIdentity(recordNumber, sequence, hardLinks, fileNames);
    _records.Add(recordNumber, result);
    return result;
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
      if (logical >= runEnd)
        continue;
      if (logical < runStart)
        break;
      if (run.Sparse)
        throw new InvalidDataException("NTFS $MFT contains a sparse data run, which cannot hold live FILE records.");

      var withinRun = logical - runStart;
      var take = checked((int)Math.Min(destination.Length - copied, runLength - withinRun));
      var physical = checked(run.Lcn * (long)_clusterSize + withinRun);
      ReadExactlyAt(physical, destination.Slice(copied, take));
      logical += take;
      copied += take;
      if (copied == destination.Length)
        return result;
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

  private void ApplyFixups(byte[] record) {
    if (record.Length < 48)
      throw new InvalidDataException("NTFS FILE record is shorter than its fixed header.");

    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    var expectedSectors = checked(record.Length / _geometry.BytesPerSector);
    if (record.Length % _geometry.BytesPerSector != 0 || usaCount != expectedSectors + 1)
      throw new InvalidDataException(
        $"NTFS FILE record has USA count {usaCount}, expected {expectedSectors + 1} for {_geometry.BytesPerSector}-byte sectors.");
    if (usaOffset < 8 || usaOffset > record.Length - checked(usaCount * 2))
      throw new InvalidDataException("NTFS FILE record update-sequence array lies outside the record.");

    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));
    for (var sector = 0; sector < expectedSectors; ++sector) {
      var trailer = checked((sector + 1) * _geometry.BytesPerSector - 2);
      var actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(trailer));
      if (actual != usn)
        throw new InvalidDataException(
          $"NTFS FILE record update-sequence mismatch in sector {sector}: found 0x{actual:X4}, expected 0x{usn:X4}.");
      record.AsSpan(usaOffset + (sector + 1) * 2, 2).CopyTo(record.AsSpan(trailer, 2));
    }
  }

  private static void ValidateFileRecord(byte[] record, uint expectedRecordNumber) {
    if (!record.AsSpan(0, 4).SequenceEqual("FILE"u8))
      throw new InvalidDataException($"NTFS MFT record {expectedRecordNumber} has no FILE signature.");

    // NTFS 3.1 stores the low 32 bits of the record number at offset 44. NTFS
    // 3.0 may leave the field zero, so only a non-zero value is authoritative.
    var recordedNumber = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(44));
    if (recordedNumber != 0 && recordedNumber != expectedRecordNumber)
      throw new InvalidDataException(
        $"NTFS FILE header says MFT record {recordedNumber}, but the $MFT mapping selected record {expectedRecordNumber}.");
  }

  private static (List<MftRun> Runs, long DataSize) ParseMftDataMap(byte[] record0) {
    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(24)));
    if (firstAttribute < 24 || used < firstAttribute || used > record0.Length)
      throw new InvalidDataException("NTFS $MFT FILE record has invalid attribute bounds.");

    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(position));
      if (type == 0xFFFFFFFF)
        break;
      var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(position + 4)));
      if (length < 24 || position > used - length)
        throw new InvalidDataException("NTFS $MFT contains a malformed attribute record.");

      var nonResident = record0[position + 8] != 0;
      var nameLength = record0[position + 9];
      if (type == 0x80 && nameLength == 0) {
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

  private static List<MftRun> ParseRuns(ReadOnlySpan<byte> mappingPairs) {
    var runs = new List<MftRun>();
    long previousLcn = 0;
    long vcn = 0;
    var position = 0;

    while (position < mappingPairs.Length) {
      var header = mappingPairs[position++];
      if (header == 0)
        break;
      var lengthBytes = header & 0x0F;
      var offsetBytes = header >> 4;
      if (lengthBytes is < 1 or > 8 || offsetBytes > 8 || position > mappingPairs.Length - lengthBytes - offsetBytes)
        throw new InvalidDataException("NTFS $MFT::$DATA contains malformed mapping pairs.");

      var count = ReadUnsigned(mappingPairs.Slice(position, lengthBytes));
      position += lengthBytes;
      if (count == 0 || count > long.MaxValue)
        throw new InvalidDataException("NTFS $MFT::$DATA contains an invalid zero/oversized run length.");
      var clusterCount = checked((long)count);

      if (offsetBytes == 0) {
        runs.Add(new MftRun(vcn, 0, clusterCount, Sparse: true));
      } else {
        var delta = ReadSigned(mappingPairs.Slice(position, offsetBytes));
        position += offsetBytes;
        previousLcn = checked(previousLcn + delta);
        if (previousLcn < 0)
          throw new InvalidDataException("NTFS $MFT::$DATA mapping pairs resolve to a negative LCN.");
        runs.Add(new MftRun(vcn, previousLcn, clusterCount, Sparse: false));
      }
      vcn = checked(vcn + clusterCount);
    }

    return runs;
  }

  private static FileNameReference[] ParseFileNames(byte[] record, uint recordNumber) {
    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
    if (firstAttribute < 24 || used < firstAttribute || used > record.Length)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has invalid attribute bounds.");

    var result = new List<FileNameReference>();
    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
      if (type == 0xFFFFFFFF)
        break;
      var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4)));
      if (length < 24 || position > used - length)
        throw new InvalidDataException($"NTFS MFT record {recordNumber} contains a malformed attribute record.");

      if (type == 0x30) {
        if (record[position + 8] != 0)
          throw new InvalidDataException($"NTFS MFT record {recordNumber} has a non-resident $FILE_NAME attribute.");
        var valueLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 16)));
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 20));
        if (valueLength < 66 || valueOffset < 24 || valueOffset > length - valueLength)
          throw new InvalidDataException($"NTFS MFT record {recordNumber} has malformed $FILE_NAME bounds.");

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
    FileNameReference[] FileNames);
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
  ushort HardLinkCount);

#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Reads the native file-reference identity the mounted NTFS view publishes.
/// A file reference is the 48-bit MFT segment number plus the 16-bit sequence
/// number; the sequence is the stale-reference guard and therefore belongs in
/// <c>FilesystemNodeId.Generation</c>. Both directions of a reference are
/// validated before a namespace is published: the $FILE_NAME parent reference
/// every record carries, and the child references the parent's $I30 index
/// carries back.
/// </summary>
internal sealed class NtfsMountIdentityScanner {
  private const uint AttributeTypeData = 0x80;
  private const uint AttributeTypeFileName = 0x30;
  private const uint AttributeTypeIndexRoot = 0x90;
  private const uint AttributeTypeIndexAllocation = 0xA0;
  private const uint AttributeTypeBitmap = 0xB0;
  private const uint AttributeEnd = 0xFFFFFFFF;

  /// <summary>Attribute-header flags: LZNT1 compression, and EFS encryption.</summary>
  private const ushort AttributeFlagCompressed = 0x0001;
  private const ushort AttributeFlagEncrypted = 0x4000;

  /// <summary>Bit 1 of an index entry's flags: the terminal entry, which carries no key.</summary>
  private const ushort IndexEntryIsLast = 0x0002;

  /// <summary>The largest $I30 index block the mounted reader will buffer.</summary>
  private const int MaxIndexBlockSize = 1 << 20;

  /// <summary>The largest $I30 free-block map the mounted reader will buffer - eight million blocks.</summary>
  private const long MaxIndexBitmapSize = 1 << 20;

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
    var directoryRecords = new List<uint> { 5 };
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

      // A symlink's content is its target text, which the reader synthesises
      // rather than reading from $DATA, and a directory has no $DATA at all.
      var dataLayout = entry.IsDirectory || entry.IsSymlink
        ? null
        : TryParseDataLayout(identity.Record, entry.MftRecord, entry.Size);

      identities.Add(new NtfsMountedEntryIdentity(entry, identity.Sequence, identity.HardLinkCount, dataLayout));
      if (entry.IsDirectory) {
        pathToRecord[path] = entry.MftRecord;
        directoryRecords.Add(entry.MftRecord);
      }
    }

    // A $I30 entry stores a complete file reference, not merely a record number.
    // The $FILE_NAME check above only proves the child agrees about its parent;
    // an index entry that outlived the file it names points the other way, and
    // after MFT-slot reuse it resolves to an unrelated live file. Checking the
    // sequence component is what makes that detectable at all.
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

    // Namespace 2 is the DOS-only 8.3 alias. Prefer the Win32/POSIX-visible
    // namespace when both forms happen to compare equal after case folding.
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

  /// <summary>
  /// Validates every file reference a directory's $I30 index publishes, in the
  /// resident $INDEX_ROOT and in each allocated $INDEX_ALLOCATION block.
  /// </summary>
  private void ValidateDirectoryIndexReferences(uint directoryRecordNumber) {
    var record = ReadIdentity(directoryRecordNumber).Record;

    List<MftRun>? indexAllocationRuns = null;
    long indexAllocationLength = 0;
    byte[]? indexBitmap = null;
    var indexBlockSize = 0;

    foreach (var attribute in EnumerateAttributes(record, directoryRecordNumber)) {
      var name = ReadAttributeName(record, directoryRecordNumber, attribute);
      if (!IsI30(name)) continue;

      switch (attribute.Type) {
        case AttributeTypeIndexRoot when !attribute.NonResident: {
          var value = ReadResidentValue(record, directoryRecordNumber, attribute);
          if (value.Length < 32)
            throw new InvalidDataException($"NTFS directory {directoryRecordNumber} has a truncated $I30 $INDEX_ROOT.");
          indexBlockSize = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(8, 4));
          ValidateIndexEntries(value, indexHeaderOffset: 16, $"directory {directoryRecordNumber} resident $I30 index");
          break;
        }
        case AttributeTypeIndexAllocation when attribute.NonResident:
          (indexAllocationRuns, indexAllocationLength) =
            ReadNonResidentMap(record, directoryRecordNumber, attribute, "$I30 $INDEX_ALLOCATION");
          break;
        case AttributeTypeBitmap:
          indexBitmap = attribute.NonResident
            ? ReadNonResidentBitmap(record, directoryRecordNumber, attribute)
            : ReadResidentValue(record, directoryRecordNumber, attribute).ToArray();
          break;
      }
    }

    if (indexAllocationRuns is null || indexAllocationLength == 0)
      return;
    if (indexBlockSize <= 0 || (indexBlockSize & (indexBlockSize - 1)) != 0 || indexBlockSize > MaxIndexBlockSize)
      throw new InvalidDataException(
        $"NTFS directory {directoryRecordNumber} has invalid $I30 index block size {indexBlockSize}.");
    var block = new byte[indexBlockSize];
    // A trailing partial block cannot hold a whole index node, so whatever is
    // there is not part of the index and is not walked.
    var blockCount = indexAllocationLength / indexBlockSize;
    for (long blockIndex = 0; blockIndex < blockCount; ++blockIndex) {
      // A block the $I30 $BITMAP marks free may still hold the index entries it
      // had before it was released. Reading one back would resurrect exactly the
      // stale references this pass exists to reject, so free blocks are skipped.
      if (!IsIndexBlockAllocated(indexBitmap, blockIndex))
        continue;
      if (!TryReadFromRuns(indexAllocationRuns, checked(blockIndex * indexBlockSize), block))
        throw new InvalidDataException(
          $"NTFS directory {directoryRecordNumber} $I30 index block {blockIndex} is marked allocated but is not mapped to clusters.");
      if (!block.AsSpan(0, 4).SequenceEqual("INDX"u8))
        throw new InvalidDataException(
          $"NTFS directory {directoryRecordNumber} $I30 index block {blockIndex} is marked allocated but has no INDX signature.");

      var context = $"directory {directoryRecordNumber} $I30 index block {blockIndex}";
      ApplyFixups(block, context);
      ValidateIndexEntries(block, indexHeaderOffset: 24, context);
    }
  }

  /// <summary>
  /// A directory's $I30 $BITMAP where it outgrew its FILE record.
  /// </summary>
  /// <returns>
  /// The bitmap, or <see langword="null"/> where it cannot be read as one piece
  /// — a continuation fragment, or one large enough to be worth refusing. Every
  /// index block then counts as live, which is the cautious answer.
  /// </returns>
  private byte[]? ReadNonResidentBitmap(byte[] record, uint recordNumber, AttributeSpan attribute) {
    if (attribute.Length < 64) return null;
    if (BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attribute.Position + 16)) != 0) return null;

    var (runs, length) = ReadNonResidentMap(record, recordNumber, attribute, "$I30 $BITMAP");
    if (length is <= 0 or > MaxIndexBitmapSize) return null;
    var bitmap = new byte[checked((int)length)];
    return TryReadFromRuns(runs, 0, bitmap) ? bitmap : null;
  }

  /// <summary>
  /// Whether an index block is in use. A directory whose $I30 $BITMAP could not
  /// be read publishes no free list, so every mapped block counts as live.
  /// </summary>
  private static bool IsIndexBlockAllocated(byte[]? bitmap, long blockIndex) {
    if (bitmap is null) return true;
    var byteIndex = blockIndex >> 3;
    return byteIndex >= bitmap.LongLength || (bitmap[byteIndex] & (1 << (int)(blockIndex & 7))) != 0;
  }

  /// <summary>
  /// Walks one INDEX_HEADER's entry stream and checks every child file reference
  /// against the live FILE record it names.
  /// </summary>
  /// <param name="buffer">The $INDEX_ROOT value or the fixed-up INDX block.</param>
  /// <param name="indexHeaderOffset">
  /// Where the INDEX_HEADER starts inside <paramref name="buffer"/>: 16 past an
  /// $INDEX_ROOT value's own header, and the fixed offset 24 in an INDX block.
  /// Its entries offset and index length are relative to that start.
  /// </param>
  /// <param name="context">Names the index in any diagnostic this raises.</param>
  private void ValidateIndexEntries(ReadOnlySpan<byte> buffer, int indexHeaderOffset, string context) {
    if (indexHeaderOffset > buffer.Length - 16)
      throw new InvalidDataException($"NTFS {context} has a truncated INDEX_HEADER.");
    var entriesOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset, 4));
    var indexLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset + 4, 4));
    var allocatedLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset + 8, 4));
    if (entriesOffset < 16 || indexLength < entriesOffset || allocatedLength < indexLength)
      throw new InvalidDataException($"NTFS {context} has inconsistent INDEX_HEADER lengths.");

    var end64 = (long)indexHeaderOffset + indexLength;
    if (end64 > buffer.Length)
      throw new InvalidDataException($"NTFS {context} index entries lie outside the containing attribute/block.");

    var position = checked((int)(indexHeaderOffset + entriesOffset));
    var end = checked((int)end64);
    while (true) {
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

      // The terminal entry carries a subnode pointer at most, never a key, so
      // its reference field is not a file reference and is not checked.
      var isLast = (flags & IndexEntryIsLast) != 0;
      if (isLast) return;

      if (rawReference != 0) {
        var segment = rawReference & 0x0000FFFFFFFFFFFFUL;
        if (segment > uint.MaxValue)
          throw new NotSupportedException(
            $"NTFS {context} references MFT segment {segment}, beyond the mounted reader's 32-bit record range.");
        var live = ReadIdentity((uint)segment);
        ValidateReferenceSequence(
          (uint)segment,
          checked((ushort)(rawReference >> 48)),
          live.Sequence,
          $"{context} child reference");
      }

      position += entryLength;
    }
  }

  /// <summary>
  /// Compares a reference's sequence component with the live FILE record's.
  /// </summary>
  /// <remarks>
  /// Sequence zero means the structure predates the generation guard or was
  /// written without one; there is then nothing to compare and the reference is
  /// taken at face value. Any other value is an assertion about which incarnation
  /// of the MFT slot was meant, and a mismatch is a stale reference.
  /// </remarks>
  private static void ValidateReferenceSequence(
      uint recordNumber,
      ushort referencedSequence,
      ushort liveSequence,
      string context) {
    if (referencedSequence != 0 && referencedSequence != liveSequence)
      throw new InvalidDataException(
        $"NTFS {context} names MFT {recordNumber} sequence {referencedSequence}, " +
        $"but the live FILE record has sequence {liveSequence}; the file reference is stale.");
  }

  private byte[] ReadMappedRecord(uint recordNumber) {
    var logicalOffset = checked((long)recordNumber * _geometry.MftRecordSize);
    if (logicalOffset < 0 || logicalOffset > _mftDataSize - _geometry.MftRecordSize)
      throw new InvalidDataException(
        $"NTFS MFT record {recordNumber} lies outside the live $MFT data stream ({_mftDataSize:N0} bytes).");

    var result = new byte[_geometry.MftRecordSize];
    if (!TryReadFromRuns(_mftRuns, logicalOffset, result))
      throw new InvalidDataException($"NTFS $MFT data runs do not map all bytes of record {recordNumber}.");
    return result;
  }

  /// <summary>
  /// Fills <paramref name="destination"/> from the stream the runs describe,
  /// starting at <paramref name="logicalOffset"/> bytes into it.
  /// </summary>
  /// <returns>
  /// False when the range is not backed by allocated clusters — a hole, or past
  /// what the run list maps. Metadata streams have neither, so for them a false
  /// return is a damaged image; an index allocation legitimately has both.
  /// </returns>
  private bool TryReadFromRuns(IReadOnlyList<MftRun> runs, long logicalOffset, Span<byte> destination) {
    var logical = logicalOffset;
    var copied = 0;

    foreach (var run in runs) {
      var runStart = checked(run.Vcn * (long)_clusterSize);
      var runLength = checked(run.ClusterCount * (long)_clusterSize);
      var runEnd = checked(runStart + runLength);
      if (logical >= runEnd) continue;
      if (logical < runStart || run.Sparse) return false;

      var withinRun = logical - runStart;
      var take = checked((int)Math.Min(destination.Length - copied, runLength - withinRun));
      ReadExactlyAt(checked(run.Lcn * (long)_clusterSize + withinRun), destination.Slice(copied, take));
      logical += take;
      copied += take;
      if (copied == destination.Length) return true;
    }

    return false;
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
    // An array that started inside the record's own fixed header would have the
    // fixup overwrite the very fields that say how to read it: a FILE record's
    // fields reach 42 even in the pre-3.1 layout, an INDX record's reach 40.
    var minimumUsaOffset = record.AsSpan(0, 4).SequenceEqual("FILE"u8)
      ? NtfsRecordLayout.LegacyFileHeaderSize
      : NtfsRecordLayout.IndexHeaderSize;
    var expectedSectors = checked(record.Length / _geometry.BytesPerSector);
    if (record.Length % _geometry.BytesPerSector != 0 || usaCount != expectedSectors + 1)
      throw new InvalidDataException(
        $"NTFS {context} has USA count {usaCount}, expected {expectedSectors + 1} for {_geometry.BytesPerSector}-byte sectors.");
    if (usaOffset < minimumUsaOffset || usaOffset > record.Length - checked(usaCount * 2))
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

    // The low 32 bits of the record number sit at offset 44 — but only in the
    // NTFS 3.1 FILE header, which the update-sequence array follows at 0x30. The
    // pre-3.1 header ends at 0x2A and puts the USA there instead, so in one of
    // those, offset 44 is the saved trailer of a sector and reading it as a
    // record number compares against whatever two bytes that sector ended with.
    // Where the USA starts is what tells the two layouts apart.
    if (!NtfsRecordLayout.HasRecordNumberField(record)) return;

    // NTFS 3.1 may still leave the field zero, so only a non-zero value is authoritative.
    var recordedNumber = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(NtfsRecordLayout.RecordNumberOffset));
    if (recordedNumber != 0 && recordedNumber != expectedRecordNumber)
      throw new InvalidDataException(
        $"NTFS FILE header says MFT record {recordedNumber}, but the $MFT mapping selected record {expectedRecordNumber}.");
  }

  private static (List<MftRun> Runs, long DataSize) ParseMftDataMap(byte[] record0) {
    foreach (var attribute in EnumerateAttributes(record0, 0)) {
      if (attribute.Type != AttributeTypeData || attribute.NameLength != 0) continue;
      if (!attribute.NonResident)
        throw new NotSupportedException("NTFS mounted identity requires a non-resident unnamed $MFT::$DATA attribute.");

      var (runs, dataSize) = ReadNonResidentMap(record0, 0, attribute, "$MFT::$DATA");
      if (dataSize <= 0)
        throw new InvalidDataException("NTFS $MFT::$DATA has a non-positive logical size.");
      if (runs.Count == 0)
        throw new InvalidDataException("NTFS $MFT::$DATA has no mapping pairs.");
      return (runs, dataSize);
    }

    throw new NotSupportedException("NTFS mounted identity could not find the unnamed $MFT::$DATA attribute in record 0.");
  }

  /// <summary>
  /// Derives the direct read layout of a file's unnamed $DATA stream.
  /// </summary>
  /// <returns>
  /// The layout, or <see langword="null"/> where the stream is valid NTFS the
  /// direct reader does not cover — LZNT1-compressed or encrypted data, a $DATA
  /// continued through an $ATTRIBUTE_LIST, or a length the decoded namespace
  /// disagrees with. Those keep the decoded-stream fallback. Damage inside a
  /// $DATA attribute is not one of those cases and fails the probe.
  /// </returns>
  private NtfsMountedDataLayout? TryParseDataLayout(byte[] record, uint recordNumber, long decodedSize) {
    foreach (var attribute in EnumerateAttributes(record, recordNumber)) {
      if (attribute.Type != AttributeTypeData || attribute.NameLength != 0) continue;

      var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attribute.Position + 12));
      if ((flags & (AttributeFlagCompressed | AttributeFlagEncrypted)) != 0) return null;

      if (!attribute.NonResident) {
        var resident = ReadResidentValue(record, recordNumber, attribute);
        return resident.Length == decodedSize
          ? new NtfsMountedDataLayout(resident.Length, resident.Length, resident.ToArray(), [])
          : null;
      }

      // A fragment that does not start the stream belongs to an $ATTRIBUTE_LIST
      // continuation record, which the mounted reader cannot follow yet.
      if (attribute.Length < 64 || BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attribute.Position + 16)) != 0)
        return null;

      var (runs, dataLength) = ReadNonResidentMap(record, recordNumber, attribute, "$DATA");
      var initializedLength = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attribute.Position + 56));
      if (initializedLength < 0)
        throw new InvalidDataException(
          $"NTFS MFT record {recordNumber} has a negative initialized $DATA length of {initializedLength}.");
      // Writers that round the initialized length up to the allocation claim
      // more than the stream holds. Capping it says the whole stream was
      // written, which is what reading every mapped byte already assumed.
      initializedLength = Math.Min(initializedLength, dataLength);
      if (dataLength != decodedSize) return null;

      var mappedBytes = runs.Count == 0 ? 0L : checked((runs[^1].Vcn + runs[^1].ClusterCount) * (long)_clusterSize);
      if (dataLength > mappedBytes)
        throw new InvalidDataException(
          $"NTFS MFT record {recordNumber} maps only {mappedBytes:N0} bytes for a {dataLength:N0}-byte $DATA stream.");

      foreach (var run in runs) {
        if (run.Sparse) continue;
        var physical = checked(run.Lcn * (long)_clusterSize);
        if (physical < 0 || physical > _image.Length - checked(run.ClusterCount * (long)_clusterSize))
          throw new InvalidDataException($"NTFS MFT record {recordNumber} has a $DATA run outside the backing image.");
      }

      return new NtfsMountedDataLayout(
        dataLength,
        initializedLength,
        ResidentData: null,
        runs.Select(static run => new NtfsMountedDataRun(run.Vcn, run.Lcn, run.ClusterCount, run.Sparse)).ToArray());
    }

    // A record with no unnamed $DATA at all holds an empty file — unless the
    // namespace says otherwise, in which case the stream lives in an
    // $ATTRIBUTE_LIST continuation record.
    return decodedSize == 0 ? new NtfsMountedDataLayout(0, 0, [], []) : null;
  }

  /// <summary>
  /// Reads a non-resident attribute's VCN→LCN map and its logical length.
  /// </summary>
  private static (List<MftRun> Runs, long DataSize) ReadNonResidentMap(
      byte[] record,
      uint recordNumber,
      AttributeSpan attribute,
      string what) {
    if (attribute.Length < 64)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has a truncated non-resident {what} header.");
    var startingVcn = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attribute.Position + 16));
    if (startingVcn != 0)
      throw new NotSupportedException(
        $"NTFS MFT record {recordNumber} {what} starts at VCN {startingVcn}; $ATTRIBUTE_LIST continuation records are not available to the mounted reader.");
    var mappingOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attribute.Position + 32));
    if (mappingOffset < 64 || mappingOffset >= attribute.Length)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has an invalid {what} mapping-pairs offset.");
    var dataSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(attribute.Position + 48));
    if (dataSize < 0)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has a negative {what} logical size.");

    var runs = ParseRuns(record.AsSpan(attribute.Position + mappingOffset, attribute.Length - mappingOffset));
    return (runs, dataSize);
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
    var result = new List<FileNameReference>();
    foreach (var attribute in EnumerateAttributes(record, recordNumber)) {
      if (attribute.Type != AttributeTypeFileName) continue;
      if (attribute.NonResident)
        throw new InvalidDataException($"NTFS MFT record {recordNumber} has a non-resident $FILE_NAME attribute.");

      var value = ReadResidentValue(record, recordNumber, attribute);
      if (value.Length < 66)
        throw new InvalidDataException($"NTFS MFT record {recordNumber} has a truncated $FILE_NAME value.");
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

    return result.ToArray();
  }

  /// <summary>
  /// Walks a FILE record's attribute list once, bounds-checked, up to the
  /// end-of-attributes marker.
  /// </summary>
  private static IEnumerable<AttributeSpan> EnumerateAttributes(byte[] record, uint recordNumber) {
    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
    if (firstAttribute < 24 || used < firstAttribute || used > record.Length)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has invalid attribute bounds.");

    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
      if (type == AttributeEnd) yield break;
      var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4)));
      if (length < 24 || position > used - length)
        throw new InvalidDataException($"NTFS MFT record {recordNumber} contains a malformed attribute record.");

      yield return new AttributeSpan(type, position, length, record[position + 8] != 0, record[position + 9]);
      position += length;
    }
  }

  private static ReadOnlySpan<byte> ReadResidentValue(byte[] record, uint recordNumber, AttributeSpan attribute) {
    var valueLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attribute.Position + 16)));
    var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attribute.Position + 20));
    if (valueOffset < 24 || valueLength < 0 || valueOffset > attribute.Length - valueLength)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has malformed resident attribute bounds.");
    return record.AsSpan(attribute.Position + valueOffset, valueLength);
  }

  private static string? ReadAttributeName(byte[] record, uint recordNumber, AttributeSpan attribute) {
    if (attribute.NameLength == 0) return null;
    var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attribute.Position + 10));
    var byteLength = attribute.NameLength * 2;
    if (nameOffset < 16 || nameOffset > attribute.Length - byteLength)
      throw new InvalidDataException($"NTFS MFT record {recordNumber} has an attribute name outside its record.");
    return Encoding.Unicode.GetString(record, attribute.Position + nameOffset, byteLength);
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
  private readonly record struct AttributeSpan(uint Type, int Position, int Length, bool NonResident, byte NameLength);
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

using System.Buffers.Binary;
using System.Text;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class NtfsFilesystemDriverTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void RegistryUsesNativeNtfsSidecar() {
    var coverage = FormatRegistry.GetFilesystemDriverCoverage("Ntfs");
    Assert.That(coverage.Binding, Is.EqualTo(FilesystemDriverBindingKind.SidecarNative));
    Assert.That(coverage.HasExtentMap, Is.True);
    Assert.That(coverage.HasBlockMover, Is.True);
  }

  [Test]
  public void NativeSessionUsesCompleteMftFileReferenceAndSupportsPositionalReads() {
    var payload = Enumerable.Range(0, 200 * 1024).Select(i => (byte)(i * 29 + 7)).ToArray();
    var writer = new NtfsWriter();
    writer.AddFile("dir/data.bin", payload);
    var image = writer.Build(8 * 1024 * 1024);

    using var stream = new MemoryStream(image, writable: false);
    var profile = FormatRegistry.ProbeFilesystem("Ntfs", stream);
    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
    Assert.That(profile.CanMountWritable, Is.False);

    stream.Position = 0;
    using var session = FormatRegistry.OpenFilesystem(
      "Ntfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var dir = session.Lookup(session.RootNodeId, "dir");
    Assert.That(dir, Is.Not.Null);
    var file = session.Lookup(dir!.Value, "data.bin");
    Assert.That(file, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(file!.Value.Value, Is.GreaterThan(15), "user object id should be its non-reserved MFT record");
      Assert.That(session.RootNodeId.Generation, Is.EqualTo(ReadMftSequence(image, session.RootNodeId.Value)));
      Assert.That(dir.Value.Generation, Is.EqualTo(ReadMftSequence(image, dir.Value.Value)));
      Assert.That(file.Value.Generation, Is.EqualTo(ReadMftSequence(image, file.Value.Value)));
      Assert.That(file.Value.Generation, Is.Not.Zero,
        "the MFT sequence number is the stale-file-reference generation, not an optional decoration");
    });

    using var handle = session.OpenFile(file!.Value, FileAccess.Read);
    var slice = new byte[2049];
    var read = handle.Read(73_333, slice);
    Assert.That(read, Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(73_333, slice.Length).ToArray()));
  }

  [Test]
  public void ProbeRejectsStaleFileNameParentReference() {
    var writer = new NtfsWriter();
    writer.AddFile("dir/data.bin", "payload"u8.ToArray());
    var image = writer.Build(8 * 1024 * 1024);

    var parentReferenceOffset = FindFileNameParentReference(image, "data.bin");
    var originalSequence = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(parentReferenceOffset + 6, 2));
    var staleSequence = originalSequence == ushort.MaxValue ? (ushort)1 : (ushort)(originalSequence + 1);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(parentReferenceOffset + 6, 2), staleSequence);

    using var stream = new MemoryStream(image, writable: false);
    var profile = new NtfsFilesystemDriverAdapter().ProbeFilesystem(stream);

    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.Limitations.Any(static text => text.Contains("stale", StringComparison.OrdinalIgnoreCase)), Is.True,
        string.Join("; ", profile.Limitations));
    });
  }

  [Test]
  public void ProbeRejectsStaleResidentIndexChildReference() {
    // Given a directory whose $I30 index still fits in its resident $INDEX_ROOT,
    // when one child reference names a generation the live FILE record does not
    // have, then the volume must not mount: after MFT-slot reuse that entry
    // resolves to an unrelated file.
    var image = BuildImage(writer => writer.AddFile("x.txt", "payload"u8.ToArray()));
    var child = FindMftRecordByFileName(image, "x.txt");
    var entry = FindIndexEntry(image, child);
    Assert.That(entry.InIndexBlock, Is.False, "a one-file root index stays resident");

    BumpSequence(image, entry.Offset);

    AssertRejected(image, "stale", "index");
  }

  [Test]
  public void ProbeRejectsStaleIndexAllocationChildReference() {
    // Same defect one level down: the entry lives in an INDX block of a spilled
    // $INDEX_ALLOCATION rather than in the resident root.
    var image = BuildLargeDirectoryImage(out var childNames);
    var child = FindMftRecordByFileName(image, childNames[^1]);
    var entry = FindIndexEntry(image, child);
    Assert.That(entry.InIndexBlock, Is.True, "a 200-entry directory spills into $INDEX_ALLOCATION");

    BumpSequence(image, entry.Offset);

    AssertRejected(image, "stale", "index");
  }

  [Test]
  public void ProbeAcceptsIndexChildReferenceWithoutSequence() {
    // Sequence zero is the absence of a generation, not a wrong one: there is
    // nothing to compare against and the reference is taken at face value.
    var image = BuildImage(writer => writer.AddFile("x.txt", "payload"u8.ToArray()));
    var child = FindMftRecordByFileName(image, "x.txt");
    var entry = FindIndexEntry(image, child);
    AssertNotSectorTrailer(entry.Offset + 6);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry.Offset + 6, 2), 0);

    using var stream = new MemoryStream(image, writable: false);
    var profile = new NtfsFilesystemDriverAdapter().ProbeFilesystem(stream);

    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
  }

  [Test]
  public void ProbeRejectsIndexReferenceToUnusedMftRecord() {
    // An index entry naming a record whose in-use flag is clear is the same
    // stale reference seen from the other side.
    var image = BuildImage(writer => writer.AddFile("x.txt", "payload"u8.ToArray()));
    var child = FindMftRecordByFileName(image, "x.txt");
    var flagsOffset = MftRecordOffset(image, child) + 22;
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(flagsOffset, 2));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(flagsOffset, 2), (ushort)(flags & ~0x0001));

    AssertRejected(image, "not in use");
  }

  [TestCase((ushort)0, TestName = "ProbeRejectsIndexEntryShorterThanItsHeader")]
  [TestCase((ushort)20, TestName = "ProbeRejectsMisalignedIndexEntryLength")]
  public void ProbeRejectsMalformedIndexEntryLength(ushort entryLength) {
    // Boundaries of the entry-length field: below the 16-byte header, and a
    // value that is not the 8-byte multiple every index entry must be.
    var image = BuildImage(writer => writer.AddFile("x.txt", "payload"u8.ToArray()));
    var child = FindMftRecordByFileName(image, "x.txt");
    var entry = FindIndexEntry(image, child);
    AssertNotSectorTrailer(entry.Offset + 8);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entry.Offset + 8, 2), entryLength);

    AssertRejected(image, "index-entry length");
  }

  [Test]
  public void ProbeRejectsIndexEntriesReachingBeyondTheirIndexHeader() {
    // The INDEX_HEADER's index length bounds the entry stream. A value past the
    // end of the containing attribute would let the walk read neighbouring bytes
    // as index entries.
    var image = BuildImage(writer => writer.AddFile("x.txt", "payload"u8.ToArray()));
    var headerOffset = FindResidentIndexHeader(image, directoryRecord: 5);
    AssertNotSectorTrailer(headerOffset + 8);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerOffset + 4, 4), 0x0000FFFF);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerOffset + 8, 4), 0x0000FFFF);

    AssertRejected(image, "outside");
  }

  [Test]
  public void ProbeIgnoresStaleReferencesInFreeIndexBlocks() {
    // A released INDX block keeps the entries it had. The $I30 $BITMAP is what
    // says it is no longer part of the index, so its contents must not be read
    // back — otherwise every deletion would look like a stale reference.
    var image = BuildLargeDirectoryImage(out var childNames);
    var child = FindMftRecordByFileName(image, childNames[^1]);
    var entry = FindIndexEntry(image, child);
    Assert.That(entry.InIndexBlock, Is.True);

    var blockIndex = IndexBlockVcnOrder(image, entry.BlockOffset);
    ClearIndexBitmapBit(image, blockIndex);
    BumpSequence(image, entry.Offset);

    using var stream = new MemoryStream(image, writable: false);
    var profile = new NtfsFilesystemDriverAdapter().ProbeFilesystem(stream);

    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
  }

  [Test]
  public void LargeDirectoryWithSpilledIndexMountsAndEnumerates() {
    // The happy path of the same walk: a directory whose index lives in INDX
    // blocks still mounts and still lists every child.
    var image = BuildLargeDirectoryImage(out var childNames);

    using var stream = new MemoryStream(image, writable: false);
    using var session = new NtfsFilesystemDriverAdapter().OpenFilesystem(
      stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    var directory = session.Lookup(session.RootNodeId, "dir");
    Assert.That(directory, Is.Not.Null);
    var listed = session.Enumerate(directory!.Value).Select(static e => e.Name).ToHashSet(StringComparer.Ordinal);
    Assert.That(listed, Is.SupersetOf(childNames));
  }

  [Test]
  public void MountReadsNoRecordNumberFromAPre31FileHeader() {
    // A pre-3.1 FILE header ends at 0x2A and puts its update-sequence array
    // there, so offset 44 holds a saved sector trailer rather than the record
    // number. Reading it as one compares against whatever two bytes that sector
    // happened to end with, and any record whose text reaches the end of its
    // first sector then looks like it belongs to a different MFT slot. The
    // volume is created as 3.0 on purpose — the writer's default is 3.1, whose
    // records do name themselves at 44.
    var image = BuildImage(writer => {
      writer.SetNtfsMinorVersion(0);
      writer.AddFile("x.txt", "payload"u8.ToArray());
    });
    var recordOffset = MftRecordOffset(image, FindMftRecordByFileName(image, "x.txt"));

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(recordOffset + 4, 2)), Is.LessThan(48),
      "a 3.0 volume must emit the pre-3.1 FILE header this guards against");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(recordOffset + 24, 4)), Is.LessThan(510u),
      "the sector trailer the fixup restores lies past the record's used bytes");

    // The first saved trailer, which shares offset 44 with the 3.1 record-number field.
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(recordOffset + 44, 2), 0xBEEF);

    using var stream = new MemoryStream(image, writable: false);
    var profile = new NtfsFilesystemDriverAdapter().ProbeFilesystem(stream);

    Assert.That(profile.CanMount, Is.True, string.Join("; ", profile.Limitations));
  }

  [Test]
  public void MountRefusesA31FileHeaderThatNamesTheWrongRecord() {
    // The other half of the same rule: on a 3.1 volume offset 44 IS the record
    // number, so one that disagrees with the slot the $MFT mapping reached means
    // the mapping and the record describe different files. That check was dead
    // while the writer emitted pre-3.1 headers under a 3.1 volume version.
    var image = BuildImage(writer => writer.AddFile("x.txt", "payload"u8.ToArray()));
    var recordNumber = FindMftRecordByFileName(image, "x.txt");
    var recordOffset = MftRecordOffset(image, recordNumber);

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(recordOffset + 4, 2)), Is.EqualTo(48),
      "the writer's default volume version emits the NTFS 3.1 FILE header");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(recordOffset + 44, 4)), Is.EqualTo(recordNumber),
      "precondition: the record names itself before we corrupt it");

    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(recordOffset + 44, 4), recordNumber + 1);

    using var stream = new MemoryStream(image, writable: false);
    var profile = new NtfsFilesystemDriverAdapter().ProbeFilesystem(stream);

    Assert.That(profile.CanMount, Is.False,
      "a record that names a different MFT slot must not be published as a mountable namespace");
  }

  [Test]
  public void OrdinaryDataOpensThroughABoundedPositionalHandle() {
    // Resident and ordinary non-resident $DATA both have a proven cluster map,
    // so neither needs the whole decoded stream materialised to be read.
    var payload = Enumerable.Range(0, 200 * 1024).Select(static i => (byte)(i * 29 + 7)).ToArray();
    var image = BuildImage(writer => {
      writer.AddFile("dir/data.bin", payload);
      writer.AddFile("dir/small.txt", "resident payload"u8.ToArray());
      writer.AddFile("dir/empty.bin", []);
    });

    using var stream = new MemoryStream(image, writable: false);
    using var session = new NtfsFilesystemDriverAdapter().OpenFilesystem(
      stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var directory = session.Lookup(session.RootNodeId, "dir")!.Value;

    using var large = session.OpenFile(session.Lookup(directory, "data.bin")!.Value, FileAccess.Read);
    using var small = session.OpenFile(session.Lookup(directory, "small.txt")!.Value, FileAccess.Read);
    using var empty = session.OpenFile(session.Lookup(directory, "empty.bin")!.Value, FileAccess.Read);

    Assert.Multiple(() => {
      Assert.That(large, Is.InstanceOf<NtfsDirectReadOnlyFileHandle>());
      Assert.That(small, Is.InstanceOf<NtfsDirectReadOnlyFileHandle>());
      Assert.That(empty, Is.InstanceOf<NtfsDirectReadOnlyFileHandle>());
      Assert.That(large.Length, Is.EqualTo(payload.Length));
      Assert.That(empty.Length, Is.Zero);
    });

    var whole = new byte[payload.Length];
    Assert.That(large.Read(0, whole), Is.EqualTo(payload.Length));
    Assert.That(whole, Is.EqualTo(payload));

    // A short read in the middle costs its own range and nothing more.
    var slice = new byte[2049];
    Assert.That(large.Read(73_333, slice), Is.EqualTo(slice.Length));
    Assert.That(slice, Is.EqualTo(payload.AsSpan(73_333, slice.Length).ToArray()));

    var resident = new byte[16];
    Assert.That(small.Read(0, resident), Is.EqualTo(16));
    Assert.That(resident, Is.EqualTo("resident payload"u8.ToArray()));
  }

  [Test]
  public void CompressedDataKeepsTheDecodedStreamFallback() {
    // LZNT1 units cannot be addressed by a cluster run, so a compressed file
    // still spools the reader's decoded stream — and must still read correctly.
    var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compressible payload. ", 3000)));
    var image = BuildImage(writer => {
      writer.SetCompression(true);
      writer.AddFile("repeating.txt", payload);
    });

    using var stream = new MemoryStream(image, writable: false);
    using var session = new NtfsFilesystemDriverAdapter().OpenFilesystem(
      stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    using var handle = session.OpenFile(session.Lookup(session.RootNodeId, "repeating.txt")!.Value, FileAccess.Read);

    Assert.That(handle, Is.InstanceOf<SpoolingReadOnlyFileHandle>());
    Assert.That(handle.Length, Is.EqualTo(payload.Length));
    var whole = new byte[payload.Length];
    Assert.That(handle.Read(0, whole), Is.EqualTo(payload.Length));
    Assert.That(whole, Is.EqualTo(payload));
  }

  [Test]
  public void ReadinessClaimsNativeStableIdentityOnlyAfterSequenceValidation() {
    var writer = new NtfsWriter();
    writer.AddFile("x.txt", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: false);

    var report = new NtfsFilesystemDriverAdapter().DescribeFilesystemDriverReadiness(
      stream,
      FilesystemDriverTarget.ReadOnly);

    Assert.Multiple(() => {
      Assert.That(report.Derivable, Is.True, string.Join("; ", report.Blockers));
      Assert.That(report.AvailableLayers.HasFlag(FilesystemDriverReadinessLayer.NativeStableNodeIds), Is.True);
    });
  }

  [Test]
  public void WritableMountStaysFailClosed() {
    var writer = new NtfsWriter();
    writer.AddFile("x.txt", "x"u8.ToArray());
    using var stream = new MemoryStream(writer.Build(), writable: true);

    Assert.Throws<NotSupportedException>(() =>
      FormatRegistry.OpenFilesystem(
        "Ntfs", stream, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true)));
  }

  private static ushort ReadMftSequence(byte[] image, ulong recordNumber) {
    var (mftOffset, recordSize, _) = ReadWriterGeometry(image);
    var offset = checked(mftOffset + (long)recordNumber * recordSize);
    if (offset < 0 || offset > image.LongLength - recordSize)
      throw new InvalidDataException($"MFT record {recordNumber} is outside the writer image.");
    return BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(checked((int)offset + 16), 2));
  }

  private static int FindFileNameParentReference(byte[] image, string leafName) {
    var (mftOffset, recordSize, bytesPerSector) = ReadWriterGeometry(image);
    var maxRecords = Math.Min(128, checked((image.Length - (int)mftOffset) / recordSize));
    for (var recordNumber = 16; recordNumber < maxRecords; ++recordNumber) {
      var physical = checked((int)(mftOffset + (long)recordNumber * recordSize));
      var record = image.AsSpan(physical, recordSize).ToArray();
      if (!record.AsSpan(0, 4).SequenceEqual("FILE"u8))
        continue;
      RestoreUsa(record, bytesPerSector);

      var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
      var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
      if (firstAttribute < 24 || used > record.Length)
        continue;

      for (var position = (int)firstAttribute; position <= used - 8;) {
        var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
        if (type == 0xFFFFFFFF)
          break;
        var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4)));
        if (length < 24 || position > used - length)
          break;

        if (type == 0x30 && record[position + 8] == 0) {
          var valueLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 16)));
          var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 20));
          if (valueLength >= 66 && valueOffset >= 24 && valueOffset <= length - valueLength) {
            var value = record.AsSpan(position + valueOffset, valueLength);
            var nameLength = value[64];
            if (66 + nameLength * 2 <= value.Length) {
              var name = Encoding.Unicode.GetString(value.Slice(66, nameLength * 2));
              if (string.Equals(name, leafName, StringComparison.OrdinalIgnoreCase))
                return checked(physical + position + valueOffset);
            }
          }
        }

        position += length;
      }
    }

    throw new InvalidDataException($"No resident $FILE_NAME named '{leafName}' was found in the writer MFT.");
  }

  // ── image fixtures ──────────────────────────────────────────────────────

  private static byte[] BuildImage(Action<NtfsWriter> populate) {
    var writer = new NtfsWriter();
    populate(writer);
    return writer.Build(8 * 1024 * 1024);
  }

  /// <summary>
  /// A directory with enough children that its $I30 index cannot stay resident
  /// and spills into $INDEX_ALLOCATION blocks tracked by a $BITMAP.
  /// </summary>
  private static byte[] BuildLargeDirectoryImage(out string[] childNames) {
    var names = Enumerable.Range(0, 200).Select(static i => $"file{i:D4}.bin").ToArray();
    childNames = names;
    return BuildImage(writer => {
      foreach (var name in names)
        writer.AddFile("dir/" + name, Encoding.ASCII.GetBytes(name));
    });
  }

  private static void AssertRejected(byte[] image, params string[] fragments) {
    using var stream = new MemoryStream(image, writable: false);
    var profile = new NtfsFilesystemDriverAdapter().ProbeFilesystem(stream);
    var reported = string.Join("; ", profile.Limitations);

    Assert.That(profile.CanMount, Is.False, reported);
    foreach (var fragment in fragments)
      Assert.That(
        profile.Limitations.Any(text => text.Contains(fragment, StringComparison.OrdinalIgnoreCase)),
        Is.True,
        $"expected '{fragment}' in: {reported}");
  }

  // ── image surgery ───────────────────────────────────────────────────────

  /// <summary>
  /// Advances the sequence component of the file reference at
  /// <paramref name="referenceOffset"/> so it no longer names the live record.
  /// </summary>
  private static void BumpSequence(byte[] image, int referenceOffset) {
    var field = referenceOffset + 6;
    AssertNotSectorTrailer(field);
    var live = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(field, 2));
    Assert.That(live, Is.Not.Zero, "the writer stamps a real generation into every file reference");
    BinaryPrimitives.WriteUInt16LittleEndian(
      image.AsSpan(field, 2),
      live == ushort.MaxValue ? (ushort)1 : (ushort)(live + 1));
  }

  /// <summary>
  /// Guards the fixtures against writing into the two bytes of a sector that the
  /// update-sequence array owns; such a write is undone when fixups are applied.
  /// </summary>
  private static void AssertNotSectorTrailer(int offset)
    => Assert.That(offset % 512, Is.LessThanOrEqualTo(508),
      "fixture would overwrite an update-sequence slot instead of the field it targets");

  private static void ClearIndexBitmapBit(byte[] image, long blockIndex) {
    var directory = FindMftRecordByFileName(image, "dir");
    var recordOffset = MftRecordOffset(image, directory);
    var record = RestoredRecord(image, recordOffset);
    foreach (var attribute in EnumerateAttributes(record)) {
      if (attribute.Type != 0xB0 || attribute.NonResident || AttributeName(record, attribute) != "$I30") continue;
      var (valueOffset, valueLength) = ResidentValueBounds(record, attribute);
      var bit = recordOffset + attribute.Position + valueOffset + checked((int)(blockIndex >> 3));
      Assert.That(blockIndex >> 3, Is.LessThan(valueLength));
      AssertNotSectorTrailer(bit);
      image[bit] &= (byte)~(1 << (int)(blockIndex & 7));
      return;
    }

    throw new InvalidDataException("The large-directory fixture has no resident $I30 $BITMAP.");
  }

  // ── image navigation ────────────────────────────────────────────────────

  private static int MftRecordOffset(byte[] image, uint recordNumber) {
    var (mftOffset, recordSize, _) = ReadWriterGeometry(image);
    return checked((int)(mftOffset + recordNumber * (long)recordSize));
  }

  private static uint FindMftRecordByFileName(byte[] image, string leafName) {
    var (mftOffset, recordSize, _) = ReadWriterGeometry(image);
    var maxRecords = checked((int)((image.LongLength - mftOffset) / recordSize));
    for (var recordNumber = 16; recordNumber < maxRecords; ++recordNumber) {
      var physical = checked((int)(mftOffset + (long)recordNumber * recordSize));
      if (!image.AsSpan(physical, 4).SequenceEqual("FILE"u8)) continue;
      if (RecordHasFileName(RestoredRecord(image, physical), leafName)) return (uint)recordNumber;
    }

    throw new InvalidDataException($"No MFT record with $FILE_NAME '{leafName}' was found.");
  }

  private static bool RecordHasFileName(byte[] record, string leafName) {
    foreach (var attribute in EnumerateAttributes(record)) {
      if (attribute.Type != 0x30 || attribute.NonResident) continue;
      var (valueOffset, valueLength) = ResidentValueBounds(record, attribute);
      if (valueLength < 66) continue;
      var value = record.AsSpan(attribute.Position + valueOffset, valueLength);
      var nameLength = value[64];
      if (66 + nameLength * 2 > value.Length) continue;
      if (string.Equals(Encoding.Unicode.GetString(value.Slice(66, nameLength * 2)), leafName, StringComparison.OrdinalIgnoreCase))
        return true;
    }

    return false;
  }

  /// <summary>Where a directory's resident $I30 INDEX_HEADER starts in the image.</summary>
  private static int FindResidentIndexHeader(byte[] image, uint directoryRecord) {
    var recordOffset = MftRecordOffset(image, directoryRecord);
    var record = RestoredRecord(image, recordOffset);
    foreach (var attribute in EnumerateAttributes(record)) {
      if (attribute.Type != 0x90 || attribute.NonResident || AttributeName(record, attribute) != "$I30") continue;
      var (valueOffset, _) = ResidentValueBounds(record, attribute);
      return recordOffset + attribute.Position + valueOffset + 16;
    }

    throw new InvalidDataException($"MFT record {directoryRecord} has no resident $I30 $INDEX_ROOT.");
  }

  private readonly record struct IndexEntryLocation(int Offset, bool InIndexBlock, int BlockOffset);

  /// <summary>
  /// Locates one $I30 index entry naming <paramref name="childRecord"/>, in a
  /// resident $INDEX_ROOT or in an INDX block, whichever holds it.
  /// </summary>
  private static IndexEntryLocation FindIndexEntry(byte[] image, uint childRecord) {
    var (mftOffset, recordSize, _) = ReadWriterGeometry(image);
    var maxRecords = checked((int)((image.LongLength - mftOffset) / recordSize));
    for (var recordNumber = 5; recordNumber < maxRecords; ++recordNumber) {
      var physical = checked((int)(mftOffset + (long)recordNumber * recordSize));
      if (!image.AsSpan(physical, 4).SequenceEqual("FILE"u8)) continue;
      var record = RestoredRecord(image, physical);
      foreach (var attribute in EnumerateAttributes(record)) {
        if (attribute.Type != 0x90 || attribute.NonResident || AttributeName(record, attribute) != "$I30") continue;
        var (valueOffset, valueLength) = ResidentValueBounds(record, attribute);
        var valueStart = attribute.Position + valueOffset;
        var entry = FindIndexEntryIn(record.AsSpan(valueStart, valueLength), indexHeaderOffset: 16, childRecord);
        if (entry >= 0) return new IndexEntryLocation(physical + valueStart + entry, false, -1);
      }
    }

    foreach (var blockOffset in FindIndexBlocks(image)) {
      var block = RestoredRecord(image, blockOffset, IndexBlockSize(image, blockOffset));
      var entry = FindIndexEntryIn(block, indexHeaderOffset: 24, childRecord);
      if (entry >= 0) return new IndexEntryLocation(blockOffset + entry, true, blockOffset);
    }

    throw new InvalidDataException($"No $I30 index entry names MFT record {childRecord}.");
  }

  private static int FindIndexEntryIn(ReadOnlySpan<byte> buffer, int indexHeaderOffset, uint childRecord) {
    var entriesOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset, 4));
    var indexLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(indexHeaderOffset + 4, 4));
    var position = checked((int)(indexHeaderOffset + entriesOffset));
    var end = Math.Min(buffer.Length, checked((int)(indexHeaderOffset + indexLength)));

    while (position <= end - 16) {
      var reference = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(position, 8));
      var entryLength = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position + 8, 2));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(position + 12, 2));
      if (entryLength < 16) break;
      if ((flags & 0x0002) == 0 && (reference & 0x0000FFFFFFFFFFFFUL) == childRecord) return position;
      if ((flags & 0x0002) != 0) break;
      position += entryLength;
    }

    return -1;
  }

  private static IEnumerable<int> FindIndexBlocks(byte[] image) {
    for (var offset = 0; offset + 512 <= image.Length; offset += 512)
      if (image.AsSpan(offset, 4).SequenceEqual("INDX"u8))
        yield return offset;
  }

  /// <summary>An INDX block's size, from the allocated length its INDEX_HEADER advertises.</summary>
  private static int IndexBlockSize(byte[] image, int blockOffset)
    => checked(BinaryPrimitives.ReadInt32LittleEndian(image.AsSpan(blockOffset + 24 + 8, 4)) + 24);

  /// <summary>Which block of a $I30 allocation stream an INDX block is, by its VCN.</summary>
  private static long IndexBlockVcnOrder(byte[] image, int blockOffset) {
    var (_, _, bytesPerSector) = ReadWriterGeometry(image);
    var clusterSize = checked(bytesPerSector * image[13]);
    var vcn = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(blockOffset + 16, 8));
    return checked(vcn * clusterSize / IndexBlockSize(image, blockOffset));
  }

  // ── record decoding ─────────────────────────────────────────────────────

  private readonly record struct AttributeSpan(uint Type, int Position, int Length, bool NonResident, byte NameLength);

  private static IEnumerable<AttributeSpan> EnumerateAttributes(byte[] record) {
    var firstAttribute = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24)));
    if (firstAttribute < 24 || used < firstAttribute || used > record.Length) yield break;

    var position = (int)firstAttribute;
    while (position <= used - 8) {
      var type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position));
      if (type == 0xFFFFFFFF) yield break;
      var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4)));
      if (length < 24 || position > used - length) yield break;
      yield return new AttributeSpan(type, position, length, record[position + 8] != 0, record[position + 9]);
      position += length;
    }
  }

  private static (int ValueOffset, int ValueLength) ResidentValueBounds(byte[] record, AttributeSpan attribute) => (
    BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attribute.Position + 20)),
    checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attribute.Position + 16))));

  private static string? AttributeName(byte[] record, AttributeSpan attribute) {
    if (attribute.NameLength == 0) return null;
    var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attribute.Position + 10));
    return Encoding.Unicode.GetString(record, attribute.Position + nameOffset, attribute.NameLength * 2);
  }

  /// <summary>A copy of a FILE record or INDX block with its USA fixups undone.</summary>
  private static byte[] RestoredRecord(byte[] image, int offset, int length = 0) {
    if (length == 0) length = ReadWriterGeometry(image).RecordSize;
    var record = image.AsSpan(offset, length).ToArray();
    RestoreUsa(record, ReadWriterGeometry(image).BytesPerSector);
    return record;
  }

  private static (long MftOffset, int RecordSize, int BytesPerSector) ReadWriterGeometry(byte[] image) {
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11, 2));
    var sectorsPerCluster = image[13];
    var clusterSize = checked(bytesPerSector * sectorsPerCluster);
    var mftCluster = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(48, 8)));
    var encodedRecordSize = unchecked((sbyte)image[64]);
    var recordSize = encodedRecordSize < 0
      ? 1 << -encodedRecordSize
      : checked(encodedRecordSize * clusterSize);
    return (checked(mftCluster * clusterSize), recordSize, bytesPerSector);
  }

  private static void RestoreUsa(byte[] record, int bytesPerSector) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
    if (usaOffset + usaCount * 2 > record.Length)
      throw new InvalidDataException("Test fixture FILE record has invalid USA bounds.");
    for (var sector = 1; sector < usaCount; ++sector) {
      var trailer = checked(sector * bytesPerSector - 2);
      record.AsSpan(usaOffset + sector * 2, 2).CopyTo(record.AsSpan(trailer, 2));
    }
  }
}

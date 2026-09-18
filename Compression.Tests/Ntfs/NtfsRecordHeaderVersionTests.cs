#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// The FILE record header has two layouts and the volume version picks one.
/// </summary>
/// <remarks>
/// <para>
/// Up to NTFS 3.0 the fixed FILE header is 42 bytes and the update-sequence array
/// starts there; NTFS 3.1 appended a reserved <c>u16</c> at 42 and the record's own
/// MFT number as a <c>u32</c> at 44, moving the array to 48. A volume that stamps one
/// version into <c>$VOLUME_INFORMATION</c> and writes the other layout into its MFT is
/// the defect these tests exist to pin: the record number would land inside the
/// update-sequence array, where the fixup overwrites it before the record ever
/// reaches the disk.
/// </para>
/// <para>
/// Every reader here takes the array position from the record in hand, so these cases
/// also cover records that claim an array overlapping their own header or running past
/// their own end.
/// </para>
/// </remarks>
[TestFixture]
public class NtfsRecordHeaderVersionTests {

  private const int LegacyUsaOffset = 42;
  private const int ExtendedUsaOffset = 48;
  private const int BytesPerSector = 512;

  // ── Fixtures ────────────────────────────────────────────────────────────

  private static NtfsWriter Writer(NtfsVersion version, params (string Name, byte[] Data)[] files) {
    var w = new NtfsWriter("VERSIONED");
    w.SetNtfsVersion(version);
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w;
  }

  private static byte[] Build(NtfsVersion version, params (string Name, byte[] Data)[] files)
    => Writer(version, files).Build(16 * 1024 * 1024);

  private static byte[] BuildSized(NtfsVersion version, int totalSize, int clusterSize, int recordSize,
    params (string Name, byte[] Data)[] files)
    => Writer(version, files).Build(totalSize, clusterSize, recordSize);

  private static byte[] Payload(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  private static Dictionary<string, byte[]> ReadBack(byte[] image, int length = 0) {
    using var ms = new MemoryStream(image, 0, length == 0 ? image.Length : length, writable: false);
    var reader = new NtfsReader(ms);
    return reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, reader.Extract);
  }

  // Every MFT record slot the image actually holds, raw (fixup still applied).
  private static IEnumerable<(uint Number, byte[] Raw)> RawRecords(byte[] image) {
    var last = MftInspector.LastInUseRecord(image);
    for (uint rec = 0; rec <= last; ++rec)
      yield return (rec, MftInspector.ReadRawRecord(image, rec));
  }

  // ── The option drives the layout ────────────────────────────────────────

  [Test, Category("HappyPath")]
  [TestCase(NtfsVersion.V31, ExtendedUsaOffset)]
  [TestCase(NtfsVersion.V30, LegacyUsaOffset)]
  [TestCase(NtfsVersion.V12, LegacyUsaOffset)]
  public void Create_VolumeVersion_SelectsTheRecordHeaderLayout(NtfsVersion version, int expectedUsaOffset) {
    var image = Build(version, ("alpha.txt", "alpha"u8.ToArray()), ("beta.bin", Payload(9000, 3)));

    Assert.Multiple(() => {
      Assert.That(MftInspector.VolumeVersion(MftInspector.ReadRecord(image, 3)),
        Is.EqualTo((version.Major(), version.Minor())), "$VOLUME_INFORMATION must carry the version that was asked for");
      foreach (var (number, raw) in RawRecords(image))
        Assert.That(MftInspector.UpdateSequenceOffset(raw), Is.EqualTo(expectedUsaOffset),
          $"MFT record {number} must use the header layout the volume version declares");
    });
  }

  /// <summary>
  /// The assertion the pre-change writer fails: a 3.1 volume's record number has to be
  /// readable from the record <em>as it sits on disk</em>. Written at 44 under the
  /// pre-3.1 layout it lands in the second and third update-sequence slots, and the
  /// fixup overwrites it on the way out.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Create_Ntfs31_RecordNumberSurvivesTheUpdateSequenceFixup() {
    var image = Build(NtfsVersion.V31, ("alpha.txt", "alpha"u8.ToArray()), ("beta.bin", Payload(9000, 5)));

    Assert.Multiple(() => {
      foreach (var (number, raw) in RawRecords(image)) {
        Assert.That(MftInspector.RecordNumberField(raw), Is.EqualTo(number),
          $"MFT record {number} must name itself at offset 44, after the fixup has run");
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(42)), Is.Zero,
          $"MFT record {number} must leave the NTFS 3.1 reserved u16 at 42 zero");
      }
    });
  }

  /// <summary>
  /// The mirror case: on a pre-3.1 volume offsets 42..47 <em>are</em> the update-sequence
  /// array, so what sits there must be the update sequence number followed by the saved
  /// sector trailers — never a record number stamped over them.
  /// </summary>
  [Test, Category("HappyPath")]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_PreNtfs31_LeavesNothingStampedWhereTheUpdateSequenceArrayLives(NtfsVersion version) {
    var image = Build(version, ("alpha.txt", "alpha"u8.ToArray()), ("beta.bin", Payload(9000, 7)));

    Assert.Multiple(() => {
      foreach (var (number, raw) in RawRecords(image)) {
        var restored = MftInspector.ReadRecord(image, number);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(LegacyUsaOffset)), Is.EqualTo(1),
          $"MFT record {number} must hold its update sequence number at 42");

        // Slots 1..n hold the bytes the fixup lifted out of each sector's last two bytes.
        // Reading them back out of the restored record is the only independent way to say
        // that nothing else was written over them.
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(6));
        for (var sector = 1; sector < usaCount; ++sector) {
          var slot = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(LegacyUsaOffset + sector * 2));
          var trailer = BinaryPrimitives.ReadUInt16LittleEndian(restored.AsSpan(sector * BytesPerSector - 2));
          Assert.That(slot, Is.EqualTo(trailer),
            $"MFT record {number} slot {sector} must hold sector {sector - 1}'s saved trailer, not a record number");
        }
      }
    });
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_EveryVersion_RoundTripsThroughTheReader(NtfsVersion version) {
    var small = "resident content"u8.ToArray();
    var large = Payload(40_000, 11);
    var image = Build(version, ("small.txt", small), ("large.bin", large), ("dir/nested.txt", small));

    var files = ReadBack(image);
    Assert.Multiple(() => {
      Assert.That(files["small.txt"], Is.EqualTo(small));
      Assert.That(files["large.bin"], Is.EqualTo(large));
    });
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_EveryVersion_MountsThroughTheDriver(NtfsVersion version) {
    var payload = Payload(20_000, 13);
    var image = Build(version, ("mounted.bin", payload), ("tiny.txt", "t"u8.ToArray()));

    using var stream = new MemoryStream(image, writable: false);
    using var session = FormatRegistry.OpenFilesystem(
      "Ntfs", stream, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));

    // The mounted path is where the two layouts used to be confused: the identity
    // scanner reads offset 44 as a record number, which is only a record number in one
    // of them. Touching every node exercises that validation on both.
    var mounted = session.Enumerate(session.RootNodeId).Select(static e => e.Name).ToHashSet(StringComparer.Ordinal);
    Assert.That(mounted, Is.SupersetOf(new[] { "mounted.bin", "tiny.txt" }));
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31)]
  [TestCase(NtfsVersion.V30)]
  [TestCase(NtfsVersion.V12)]
  public void Create_EveryVersion_SurvivesTheFormatDescriptorOption(NtfsVersion version) {
    var descriptor = new NtfsFormatDescriptor();
    var text = version.ToVersionText();
    var option = descriptor.OptionsSchema.Single(o => o.Key == "NtfsVersion");

    var output = new MemoryStream();
    descriptor.Create(output, [], new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["NtfsVersion"] = text }
    });
    var image = output.ToArray();

    Assert.Multiple(() => {
      Assert.That(option.AllowedValues, Does.Contain(text));
      Assert.That(option.Description, Does.Not.Contain("Volume version stamped into $VOLUME_INFORMATION."),
        "the option no longer only stamps $VOLUME_INFORMATION");
      Assert.That(MftInspector.UpdateSequenceOffset(MftInspector.ReadRawRecord(image, 5)),
        Is.EqualTo(version.UsesExtendedRecordHeader() ? ExtendedUsaOffset : LegacyUsaOffset));
      Assert.That(MftInspector.VolumeVersion(MftInspector.ReadRecord(image, 3)),
        Is.EqualTo((version.Major(), version.Minor())));
    });
  }

  // ── Boundaries: the record size moves where the attributes start ────────

  /// <summary>
  /// The update-sequence array is one slot plus one per 512-byte sector, so a bigger
  /// record makes a longer array and pushes the first attribute further out — from a
  /// different starting offset under each layout. 2048- and 4096-byte records are where
  /// the two layouts stop agreeing on where the attributes begin.
  /// </summary>
  [Test, Category("Boundary")]
  [TestCase(NtfsVersion.V31, 1024)]
  [TestCase(NtfsVersion.V31, 2048)]
  [TestCase(NtfsVersion.V31, 4096)]
  [TestCase(NtfsVersion.V30, 1024)]
  [TestCase(NtfsVersion.V30, 2048)]
  [TestCase(NtfsVersion.V30, 4096)]
  [TestCase(NtfsVersion.V12, 1024)]
  [TestCase(NtfsVersion.V12, 2048)]
  [TestCase(NtfsVersion.V12, 4096)]
  public void Create_RecordSize_PlacesAttributesPastTheUpdateSequenceArray(NtfsVersion version, int recordSize) {
    var payload = Payload(30_000, recordSize);
    var image = BuildSized(version, 24 * 1024 * 1024, 4096, recordSize, ("geo.bin", payload));

    var usaOffset = version.UsesExtendedRecordHeader() ? ExtendedUsaOffset : LegacyUsaOffset;
    var usaCount = 1 + recordSize / BytesPerSector;
    var expectedAttributeStart = Math.Max(56, (usaOffset + 2 * usaCount + 7) & ~7);

    Assert.Multiple(() => {
      foreach (var (number, raw) in RawRecords(image)) {
        Assert.That(MftInspector.UpdateSequenceOffset(raw), Is.EqualTo(usaOffset));
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(6)), Is.EqualTo(usaCount),
          $"MFT record {number} must protect every sector it spans");
        var attributeStart = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(20));
        Assert.That(attributeStart, Is.EqualTo(expectedAttributeStart));
        Assert.That(attributeStart, Is.GreaterThanOrEqualTo(usaOffset + 2 * usaCount),
          $"MFT record {number} would start its attributes inside its own update-sequence array");
      }
      Assert.That(ReadBack(image)["geo.bin"], Is.EqualTo(payload));
    });
  }

  // ── In-place operations must keep the layout they found ─────────────────

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31, ExtendedUsaOffset)]
  [TestCase(NtfsVersion.V30, LegacyUsaOffset)]
  [TestCase(NtfsVersion.V12, LegacyUsaOffset)]
  public void InPlaceAdd_KeepsTheLayoutOfTheImageItModifies(NtfsVersion version, int expectedUsaOffset) {
    var seed = "SEED"u8.ToArray();
    var image = Build(version, ("seed.txt", seed));
    var added = Encoding.ASCII.GetBytes("ADDED-IN-PLACE");

    NtfsInPlaceAdder.AddFile(image, "added.txt", added);
    NtfsInPlaceAdder.AddFile(image, "sub/deep.txt", added);

    var addedRecord = MftInspector.FindRecordNumberByFileName(image, "added.txt");
    var directoryRecord = MftInspector.FindRecordNumberByFileName(image, "sub");

    Assert.Multiple(() => {
      foreach (var (number, raw) in RawRecords(image))
        Assert.That(MftInspector.UpdateSequenceOffset(raw), Is.EqualTo(expectedUsaOffset),
          $"in-place add turned MFT record {number} into the other layout");

      foreach (var record in new[] { addedRecord, directoryRecord }) {
        var raw = MftInspector.ReadRawRecord(image, record);
        if (expectedUsaOffset == ExtendedUsaOffset)
          Assert.That(MftInspector.RecordNumberField(raw), Is.EqualTo(record),
            "a record added to a 3.1 volume must name itself");
        else
          Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(LegacyUsaOffset)), Is.Not.Zero,
            "a record added to a pre-3.1 volume must hold its update sequence number at 42");
      }

      var files = ReadBack(image);
      Assert.That(files["seed.txt"], Is.EqualTo(seed));
      Assert.That(files["added.txt"], Is.EqualTo(added));
    });
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31, ExtendedUsaOffset)]
  [TestCase(NtfsVersion.V30, LegacyUsaOffset)]
  [TestCase(NtfsVersion.V12, LegacyUsaOffset)]
  public void Remove_KeepsTheLayoutOfTheImageItModifies(NtfsVersion version, int expectedUsaOffset) {
    var keep = Payload(9000, 17);
    var image = Build(version, ("keep.bin", keep), ("drop.bin", Payload(9000, 19)));

    NtfsRemover.Remove(image, "drop.bin");

    Assert.Multiple(() => {
      foreach (var (number, raw) in RawRecords(image))
        Assert.That(MftInspector.UpdateSequenceOffset(raw), Is.EqualTo(expectedUsaOffset),
          $"remove turned MFT record {number} into the other layout");
      var files = ReadBack(image);
      Assert.That(files.ContainsKey("drop.bin"), Is.False);
      Assert.That(files["keep.bin"], Is.EqualTo(keep));
    });
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31, ExtendedUsaOffset)]
  [TestCase(NtfsVersion.V30, LegacyUsaOffset)]
  [TestCase(NtfsVersion.V12, LegacyUsaOffset)]
  public void Shrink_KeepsTheLayoutOfTheImageItModifies(NtfsVersion version, int expectedUsaOffset) {
    var alpha = Payload(200_000, 23);
    var image = Writer(version, ("alpha.bin", alpha)).Build(24 * 1024 * 1024);

    var result = NtfsInPlaceShrinker.ShrinkToFit(image);
    Assert.That(result.WasReduced, Is.True);

    Assert.Multiple(() => {
      foreach (var (number, raw) in RawRecords(image))
        Assert.That(MftInspector.UpdateSequenceOffset(raw), Is.EqualTo(expectedUsaOffset),
          $"shrink turned MFT record {number} into the other layout");
      Assert.That(ReadBack(image, (int)result.NewSize)["alpha.bin"], Is.EqualTo(alpha));
    });
  }

  [Test, Category("RoundTrip")]
  [TestCase(NtfsVersion.V31, ExtendedUsaOffset)]
  [TestCase(NtfsVersion.V30, LegacyUsaOffset)]
  [TestCase(NtfsVersion.V12, LegacyUsaOffset)]
  public void BlockMover_KeepsTheLayoutOfTheImageItModifies(NtfsVersion version, int expectedUsaOffset) {
    var payload = Payload(20_000, 29);
    var image = Writer(version, ("moved.bin", payload)).Build(16 * 1024 * 1024);

    using var stream = new MemoryStream(image);
    var extent = NtfsExtentMap.Enumerate(stream).First(e => e.FileName == "moved.bin");

    const int clusterSize = 4096;
    var newOffset = (image.Length - 8 * clusterSize) / clusterSize * clusterSize;

    var mover = new NtfsBlockMover();
    mover.Init(image);
    mover.MoveExtent(stream, extent.Offset, newOffset, extent.Length, zeroSource: true);
    mover.UpdateAllocationAfterMove(stream, "moved.bin", extent.Offset, newOffset, extent.Length);

    Assert.Multiple(() => {
      foreach (var (number, raw) in RawRecords(image))
        Assert.That(MftInspector.UpdateSequenceOffset(raw), Is.EqualTo(expectedUsaOffset),
          $"the block mover turned MFT record {number} into the other layout");
      Assert.That(ReadBack(image)["moved.bin"], Is.EqualTo(payload));
    });
  }

  // ── Exceptional: a record that lies about where its array is ────────────

  [Test, Category("Exceptional")]
  public void TryReadUpdateSequence_AcceptsBothLayouts() {
    Assert.Multiple(() => {
      foreach (var offset in new[] { LegacyUsaOffset, ExtendedUsaOffset }) {
        var record = FileRecord(1024, offset, 3);
        Assert.That(NtfsRecordLayout.TryReadUpdateSequence(record, out var usaOffset, out var usaCount), Is.True);
        Assert.That(usaOffset, Is.EqualTo(offset));
        Assert.That(usaCount, Is.EqualTo(3));
      }
    });
  }

  [Test, Category("Exceptional")]
  [TestCase(0, 3, TestName = "array at the record's magic")]
  [TestCase(4, 3, TestName = "array over usa_ofs itself")]
  [TestCase(20, 3, TestName = "array over the attribute offset")]
  [TestCase(41, 3, TestName = "one byte inside the pre-3.1 header")]
  public void TryReadUpdateSequence_RefusesAnArrayInsideTheHeader(int usaOffset, int usaCount) {
    var record = FileRecord(1024, usaOffset, usaCount);
    Assert.That(NtfsRecordLayout.TryReadUpdateSequence(record, out _, out _), Is.False);
  }

  [Test, Category("Exceptional")]
  [TestCase(1020, 3, TestName = "array runs past the record end")]
  [TestCase(1024, 2, TestName = "array starts at the record end")]
  [TestCase(1019, 3, TestName = "array's last slot is one byte short")]
  public void TryReadUpdateSequence_RefusesAnArrayPastTheRecord(int usaOffset, int usaCount) {
    var record = FileRecord(1024, usaOffset, usaCount);
    Assert.That(NtfsRecordLayout.TryReadUpdateSequence(record, out _, out _), Is.False);
  }

  [Test, Category("Exceptional")]
  [TestCase(48, 0, TestName = "no slots at all")]
  [TestCase(48, 1, TestName = "the update sequence number and nothing else")]
  public void TryReadUpdateSequence_RefusesAnArrayWithNoProtectedSector(int usaOffset, int usaCount) {
    var record = FileRecord(1024, usaOffset, usaCount);
    Assert.That(NtfsRecordLayout.TryReadUpdateSequence(record, out _, out _), Is.False);
  }

  [Test, Category("Exceptional")]
  public void TryReadUpdateSequence_AllowsAnIndexBlockToStartItsArrayAtForty() {
    var block = FileRecord(4096, 40, 9);
    "INDX"u8.CopyTo(block);
    Assert.That(NtfsRecordLayout.TryReadUpdateSequence(block, out var usaOffset, out _), Is.True);
    Assert.That(usaOffset, Is.EqualTo(40));
  }

  /// <summary>
  /// A user record whose header claims an impossible array must cost that one record,
  /// not the walk: the reader has to refuse the fixup instead of writing through it.
  /// </summary>
  [Test, Category("Exceptional")]
  public void Reader_RecordWithAnImpossibleUpdateSequenceOffset_DoesNotCorruptTheWalk() {
    var keep = "keep me"u8.ToArray();
    var image = Build(NtfsVersion.V31, ("keep.txt", keep), ("broken.txt", "broken"u8.ToArray()));

    var broken = MftInspector.FindRecordNumberByFileName(image, "broken.txt");
    var recordOffset = (int)NtfsInPlaceAdder.MftRecordByteOffset(image, (int)broken);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(recordOffset + 4), 20); // inside its own header
    var headerBefore = image.AsSpan(recordOffset, 42).ToArray();

    Dictionary<string, byte[]>? files = null;
    Assert.DoesNotThrow(() => files = ReadBack(image), "one malformed record must not abort the MFT walk");

    Assert.Multiple(() => {
      Assert.That(files!["keep.txt"], Is.EqualTo(keep), "the healthy record must still read back");
      Assert.That(image.AsSpan(recordOffset, 42).SequenceEqual(headerBefore), Is.True,
        "the refused fixup must not have written into the record's own header");
    });
  }

  // Minimal FILE record shell: magic, the claimed array position and slot count.
  private static byte[] FileRecord(int size, int usaOffset, int usaCount) {
    var record = new byte[size];
    "FILE"u8.CopyTo(record);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)usaOffset);
    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)usaCount);
    return record;
  }
}

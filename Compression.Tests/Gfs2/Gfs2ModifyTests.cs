using Compression.Registry;
using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// Existing-image R/W coverage for the standalone GFS2 profile. Mutation is a
/// verified rebuild, but it must preserve the volume properties that are not
/// file payload: the caller's size floor and lock-table name.
/// </summary>
[TestFixture]
public class Gfs2ModifyTests {

  private const long ImageSize = 32L * 1024 * 1024;
  private const string LockTable = "cluster:workbench";

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31 + seed * 17 + i / 4096);
    return data;
  }

  private static MemoryStream Create(params (string Name, byte[] Data)[] files) {
    var descriptor = new Gfs2FormatDescriptor();
    var image = new MemoryStream();
    descriptor.Create(image,
      files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data)).ToArray(),
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          ["size"] = ImageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
          ["LockTable"] = LockTable,
        },
      });
    image.Position = 0;
    return image;
  }

  private static byte[] Read(Stream image, string name) {
    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    var entry = reader.Entries.Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    return reader.Extract(entry);
  }

  private static void AssertProfile(Stream image) {
    Assert.That(image.Length, Is.EqualTo(ImageSize), "CRUD must preserve the existing image size when it still fits.");
    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    Assert.Multiple(() => {
      Assert.That(reader.SuperblockValid, Is.True);
      Assert.That(reader.LockTable, Is.EqualTo(LockTable), "CRUD must preserve sb_locktable.");
    });
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AdvertisesRw() {
    var descriptor = new Gfs2FormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("RoundTrip")]
  public void AddReplaceRemove_PreservesPayloadsAndVolumeProfile() {
    var seed = Payload(1, 9_000); // indirect-tree file, not stuffed in the dinode
    var first = Payload(2, 6_000);
    var replacement = Payload(3, 17_000);
    using var image = Create(("SEED.BIN", seed));
    var descriptor = new Gfs2FormatDescriptor();
    var modifier = (IArchiveModifiable)descriptor;

    modifier.Add(image, [ArchiveInputInfo.InMemory("EXTRA.BIN", first)]);
    AssertProfile(image);
    Assert.That(Read(image, "SEED.BIN"), Is.EqualTo(seed));
    Assert.That(Read(image, "EXTRA.BIN"), Is.EqualTo(first));

    modifier.Add(image, [ArchiveInputInfo.InMemory("EXTRA.BIN", replacement)]);
    AssertProfile(image);
    Assert.That(Read(image, "SEED.BIN"), Is.EqualTo(seed));
    Assert.That(Read(image, "EXTRA.BIN"), Is.EqualTo(replacement));

    modifier.Remove(image, ["EXTRA.BIN"]);
    AssertProfile(image);
    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "SEED.BIN" }));
    Assert.That(reader.Extract(reader.Entries.Single()), Is.EqualTo(seed));
  }

  [Test, Category("Regression")]
  public void AddToEmptyVolume_DoesNotTurnDiagnosticEntriesIntoFiles() {
    using var image = Create();
    var descriptor = new Gfs2FormatDescriptor();
    var payload = Payload(4, 5_000);

    ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory("REAL.BIN", payload)]);

    AssertProfile(image);
    image.Position = 0;
    var names = descriptor.List(image, null).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert.Multiple(() => {
      Assert.That(names, Is.EquivalentTo(new[] { "REAL.BIN" }));
      Assert.That(names, Does.Not.Contain("FULL.gfs2"));
      Assert.That(names, Does.Not.Contain("metadata.ini"));
      Assert.That(names, Does.Not.Contain("superblock.bin"));
    });
    Assert.That(Read(image, "REAL.BIN"), Is.EqualTo(payload));
  }

  [Test, Category("Regression")]
  public void Add_PreservesUserDinodeAttributesAndVolumeUuid() {
    var uuid = Enumerable.Range(0, 16).Select(static value => (byte)value).ToArray();
    var metadata = new Gfs2InodeMetadata(
      0x81A0, 1201, 2202, 0,
      1_700_000_001, 123_456_789,
      1_700_000_002, 234_567_890,
      1_700_000_003, 345_678_901);
    var writer = new Gfs2Writer(ImageSize, uuid, lockTable: LockTable);
    var contents = Payload(5, 600);
    writer.AddFile("ATTR.BIN", contents, metadata);
    using var image = new MemoryStream();
    writer.Build(image);

    ((IArchiveModifiable)new Gfs2FormatDescriptor()).Add(image,
      [ArchiveInputInfo.InMemory("NEW.BIN", [1, 2, 3])]);

    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    var actual = reader.Entries.Single(static entry => entry.Name == "ATTR.BIN").Metadata;
    Assert.Multiple(() => {
      Assert.That(reader.Uuid, Is.EqualTo(uuid));
      Assert.That(actual, Is.EqualTo(metadata));
      Assert.That(reader.Extract(reader.Entries.Single(static entry => entry.Name == "ATTR.BIN")), Is.EqualTo(contents));
    });
  }
  private static Gfs2InodeMetadata Attributes(uint mode = 0x81A0, uint flags = 0, uint nanoseconds = 123_456_789)
    => new(mode, 1201, 2202, flags,
      1_700_000_001, nanoseconds,
      1_700_000_002, nanoseconds,
      1_700_000_003, nanoseconds);

  private static MemoryStream Build(Gfs2InodeMetadata? metadata, byte[]? uuid = null) {
    var writer = new Gfs2Writer(ImageSize, uuid, lockTable: LockTable);
    writer.AddFile("ATTR.BIN", Payload(5, 600), metadata);
    writer.AddFile("KEEP.BIN", Payload(6, 9_000));
    var image = new MemoryStream();
    writer.Build(image);
    image.Position = 0;
    return image;
  }

  private static Gfs2Entry EntryOf(Stream image, string name, out Gfs2Reader reader) {
    image.Position = 0;
    reader = new Gfs2Reader(image);
    return reader.Entries.Single(entry => entry.Name == name);
  }

  [Test, Category("HappyPath")]
  public void GivenNoMetadata_WhenWritten_ThenDinodeCarriesTheDefaultRootOwned0644File() {
    using var image = Build(null);
    var entry = EntryOf(image, "ATTR.BIN", out var reader);
    using (reader)
      Assert.Multiple(() => {
        Assert.That(entry.Metadata!.Mode, Is.EqualTo(0x81A4u));
        Assert.That(entry.Metadata.UserId, Is.Zero);
        Assert.That(entry.Metadata.GroupId, Is.Zero);
        Assert.That(entry.Metadata.Flags, Is.Zero);
        Assert.That(entry.Metadata.ModificationTimeNanoseconds, Is.Zero);
      });
  }

  [TestCase(0x10u)]
  [TestCase(0x20u)]
  [TestCase(0x40u)]
  [TestCase(0x80u)]
  [TestCase(0x100u)]
  [TestCase(Gfs2Writer.PreservableFileFlags)]
  [Category("HappyPath")]
  public void GivenPolicyFlags_WhenWritten_ThenReadBackUnchanged(uint flags) {
    var metadata = Attributes(flags: flags);
    using var image = Build(metadata);
    var entry = EntryOf(image, "ATTR.BIN", out var reader);
    using (reader)
      Assert.That(entry.Metadata, Is.EqualTo(metadata));
  }

  [TestCase(0x00000001u, TestName = "GivenJdataFlag_WhenWritten_ThenRefuses")]
  [TestCase(0x00000002u, TestName = "GivenExhashFlag_WhenWritten_ThenRefuses")]
  [TestCase(0x00000008u, TestName = "GivenEaIndirectFlag_WhenWritten_ThenRefuses")]
  [TestCase(0x00000200u, TestName = "GivenSystemFlag_WhenWritten_ThenRefuses")]
  [TestCase(0x20000000u, TestName = "GivenTruncInProgressFlag_WhenWritten_ThenRefuses")]
  [TestCase(0x80000000u, TestName = "GivenInheritJdataFlag_WhenWritten_ThenRefuses")]
  [Category("ErrorHandling")]
  public void GivenLayoutFlag_WhenWritten_ThenRefuses(uint flags) {
    var writer = new Gfs2Writer(ImageSize, lockTable: LockTable);
    Assert.Throws<ArgumentException>(() => writer.AddFile("ATTR.BIN", [1], Attributes(flags: flags)));
    Assert.Throws<ArgumentException>(() => writer.AddStreamingFile("ATTR.BIN", 1, () => new MemoryStream([1]), Attributes(flags: flags)));
  }

  [TestCase(0x41EDu, TestName = "GivenDirectoryMode_WhenWrittenAsFile_ThenRefuses")]
  [TestCase(0xA1FFu, TestName = "GivenSymlinkMode_WhenWrittenAsFile_ThenRefuses")]
  [TestCase(0x01A4u, TestName = "GivenModeWithoutFileType_WhenWrittenAsFile_ThenRefuses")]
  [Category("ErrorHandling")]
  public void GivenNonRegularMode_WhenWrittenAsFile_ThenRefuses(uint mode) {
    var writer = new Gfs2Writer(ImageSize, lockTable: LockTable);
    Assert.Throws<ArgumentException>(() => writer.AddFile("ATTR.BIN", [1], Attributes(mode: mode)));
  }

  [Test, Category("Boundary")]
  public void GivenLargestNanosecondValue_WhenWritten_ThenReadBackUnchanged() {
    var metadata = Attributes(nanoseconds: 999_999_999);
    using var image = Build(metadata);
    var entry = EntryOf(image, "ATTR.BIN", out var reader);
    using (reader)
      Assert.That(entry.Metadata, Is.EqualTo(metadata));
  }

  [Test, Category("Boundary")]
  public void GivenOneBillionNanoseconds_WhenWritten_ThenRefuses() {
    var writer = new Gfs2Writer(ImageSize, lockTable: LockTable);
    Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddFile("ATTR.BIN", [1], Attributes(nanoseconds: 1_000_000_000)));
  }

  [Test, Category("Regression")]
  public void GivenAttributedFile_WhenAnotherIsRemoved_ThenItsDinodeAttributesSurvive() {
    var metadata = Attributes(flags: 0x80);
    using var image = Build(metadata);

    ((IArchiveModifiable)new Gfs2FormatDescriptor()).Remove(image, ["KEEP.BIN"]);

    var entry = EntryOf(image, "ATTR.BIN", out var reader);
    using (reader)
      Assert.Multiple(() => {
        Assert.That(entry.Metadata, Is.EqualTo(metadata));
        Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "ATTR.BIN" }));
        Assert.That(reader.Extract(entry), Is.EqualTo(Payload(5, 600)));
      });
  }

  [Test, Category("Regression")]
  public void GivenSourceFileFlaggedJdata_WhenRebuilt_ThenOnlyPolicyFlagsAreCarriedOver() {
    using var image = Build(Attributes(flags: 0x20));
    var entry = EntryOf(image, "ATTR.BIN", out var reader);
    reader.Dispose();
    // di_flags sits at dinode offset 128; add JDATA and EA_INDIRECT to the source.
    var bytes = image.ToArray();
    var at = checked((int)(entry.InodeBlock * 4096 + 128));
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at, 4), 0x20u | 0x1u | 0x8u);
    using var patched = new MemoryStream();
    patched.Write(bytes);

    ((IArchiveModifiable)new Gfs2FormatDescriptor()).Add(patched, [ArchiveInputInfo.InMemory("NEW.BIN", [1, 2, 3])]);

    var rebuilt = EntryOf(patched, "ATTR.BIN", out var after);
    using (after)
      Assert.Multiple(() => {
        Assert.That(rebuilt.Metadata!.Flags, Is.EqualTo(0x20u));
        Assert.That(rebuilt.Metadata.UserId, Is.EqualTo(1201u));
        Assert.That(after.Extract(rebuilt), Is.EqualTo(Payload(5, 600)));
      });
  }

  [Test, Category("HappyPath")]
  public void GivenUuidOption_WhenCreated_ThenSuperblockCarriesIt() {
    using var image = new MemoryStream();
    new Gfs2FormatDescriptor().Create(image, [ArchiveInputInfo.InMemory("A.BIN", [1])],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          ["Uuid"] = "00112233445566778899AABBCCDDEEFF",
        },
      });
    image.Position = 0;
    using var reader = new Gfs2Reader(image);
    Assert.That(reader.UuidHex, Is.EqualTo("00112233445566778899AABBCCDDEEFF"));
  }

  [TestCase("xyz", TestName = "GivenNonHexUuidOption_WhenCreated_ThenRefuses")]
  [TestCase("00112233445566778899AABBCCDDEE", TestName = "GivenFifteenByteUuidOption_WhenCreated_ThenRefuses")]
  [TestCase("00112233445566778899AABBCCDDEEFF00", TestName = "GivenSeventeenByteUuidOption_WhenCreated_ThenRefuses")]
  [Category("ErrorHandling")]
  public void GivenMalformedUuidOption_WhenCreated_ThenRefuses(string uuid) {
    using var image = new MemoryStream();
    Assert.Throws<ArgumentException>(() => new Gfs2FormatDescriptor().Create(image, [ArchiveInputInfo.InMemory("A.BIN", [1])],
      new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Uuid"] = uuid },
      }));
  }
}

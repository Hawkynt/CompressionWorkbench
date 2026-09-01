using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzw;
using Compression.Registry;
using FileFormat.NuFx;

namespace Compression.Tests.NuFx;

[TestFixture]
public sealed class NuFxTests {
  private static readonly byte[] SampleA =
    "NuFX/ShrinkIt interoperability test data. NuFX/ShrinkIt interoperability test data."u8.ToArray();
  private static readonly byte[] SampleB =
    Enumerable.Range(0, 9000).Select(i => (byte)((i * 37 + i / 11) & 0xFF)).ToArray();

  [Test]
  public void Descriptor_AdvertisesTrueReadWriteAndSupportedMethods() {
    var descriptor = new NuFxFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(descriptor.Methods.Select(m => m.Name),
      Is.EquivalentTo(new[] { "stored", "squeeze", "nulzw1", "nulzw2", "auto" }));
    Assert.That(descriptor.Extensions, Does.Contain(".shk"));
    Assert.That(descriptor.Extensions, Does.Contain(".sdk"));
  }

  [TestCase("stored")]
  [TestCase("squeeze")]
  [TestCase("nulzw1")]
  [TestCase("nulzw2")]
  [TestCase("auto")]
  public void Create_RoundTripsEveryWritableCompressionMethod(string method) {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = new MemoryStream();
    descriptor.Create(archive, [
      ArchiveInputInfo.InMemory("DOCS/ÄPFEL.TXT", SampleA),
      ArchiveInputInfo.InMemory("BIN/SECOND.BIN", SampleB),
    ], new FormatCreateOptions { MethodName = method });

    archive.Position = 0;
    var entries = descriptor.List(archive, null);
    Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "DOCS/ÄPFEL.TXT", "BIN/SECOND.BIN" }));

    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "DOCS/ÄPFEL.TXT", null), Is.EqualTo(SampleA));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "BIN/SECOND.BIN", null), Is.EqualTo(SampleB));

    archive.Position = 0;
    var integrity = descriptor.ValidateIntegrity(archive);
    Assert.That(integrity.IsValid, Is.True);
    Assert.That(integrity.ValidEntries, Is.EqualTo(2));
  }

  [Test]
  public void Create_DiskImageModeProducesSdkStyleDiskThread() {
    var descriptor = new NuFxFormatDescriptor();
    var disk = new byte[143360];
    for (var i = 0; i < disk.Length; i++)
      disk[i] = (byte)(i * 13);

    using var archive = new MemoryStream();
    descriptor.Create(archive, [ArchiveInputInfo.InMemory("DISK140K", disk)],
      new FormatCreateOptions {
        MethodName = "nulzw2",
        FormatSpecific = new Dictionary<string, string> { ["Mode"] = "DiskImage" },
      });

    archive.Position = 0;
    var entry = descriptor.List(archive, null).Single();
    Assert.That(entry.Kind, Is.EqualTo("disk-image"));
    Assert.That(entry.OriginalSize, Is.EqualTo(disk.Length));

    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "DISK140K", null), Is.EqualTo(disk));
  }

  [Test]
  public void Create_DiskImageModeRejectsNonSectorMultiple() {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = new MemoryStream();
    var options = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Mode"] = "DiskImage" },
    };
    Assert.Throws<InvalidDataException>(() =>
      descriptor.Create(archive, [ArchiveInputInfo.InMemory("BAD", new byte[513])], options));
  }

  [Test]
  public void DirectAdd_PatchesCountEofAndMasterCrcBeforeNextEdit() {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = CreateArchive(descriptor, ("ONE", SampleA));

    descriptor.Add(archive, [
      ArchiveInputInfo.InMemory("TWO", SampleB),
      ArchiveInputInfo.InMemory("THREE", "three"u8),
    ]);

    var master = archive.ToArray().AsSpan(0, 48);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(master.Slice(8, 4)), Is.EqualTo(3u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(master.Slice(0x26, 4)), Is.EqualTo((uint)archive.Length));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(master.Slice(6, 2)),
      Is.EqualTo(NuLzwCodec.Crc16Xmodem(master.Slice(8), 0)));

    archive.Position = 0;
    Assert.That(descriptor.List(archive, null).Select(e => e.Name),
      Is.EqualTo(new[] { "ONE", "TWO", "THREE" }));
  }

  [Test]
  public void DirectReplace_ChangesOneRecordWithoutReencodingFollowingRecord() {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = CreateArchive(descriptor, ("ONE", SampleA), ("TWO", SampleB));

    var before = archive.ToArray();
    var secondSignature = FindNth(before, new byte[] { 0x4E, 0xF5, 0x46, 0xD8 }, 2);
    Assert.That(secondSignature, Is.GreaterThan(0));
    var secondRecordBefore = before.AsSpan(secondSignature).ToArray();

    var replacement = Enumerable.Repeat((byte)0xA5, 12000).ToArray();
    descriptor.Add(archive, [ArchiveInputInfo.InMemory("ONE", replacement)]);

    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "ONE", null), Is.EqualTo(replacement));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "TWO", null), Is.EqualTo(SampleB));

    var after = archive.ToArray();
    var secondAfter = FindNth(after, new byte[] { 0x4E, 0xF5, 0x46, 0xD8 }, 2);
    Assert.That(secondAfter, Is.GreaterThan(0));
    Assert.That(after.AsSpan(secondAfter).ToArray(), Is.EqualTo(secondRecordBefore));
  }

  [Test]
  public void DirectRemove_ClosesExtentAndRepairsMaster() {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = CreateArchive(descriptor, ("ONE", SampleA), ("TWO", SampleB), ("THREE", "3"u8.ToArray()));
    var oldLength = archive.Length;

    descriptor.Remove(archive, ["TWO"]);

    Assert.That(archive.Length, Is.LessThan(oldLength));
    var master = archive.ToArray().AsSpan(0, 48);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(master.Slice(8, 4)), Is.EqualTo(2u));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(master.Slice(0x26, 4)), Is.EqualTo((uint)archive.Length));

    archive.Position = 0;
    Assert.That(descriptor.List(archive, null).Select(e => e.Name), Is.EqualTo(new[] { "ONE", "THREE" }));
  }

  [Test]
  public void Defragment_TrimsShrinkItFilenameReserveWithoutChangingPayload() {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = CreateArchive(descriptor, ("A", SampleA), ("B", SampleB));
    var before = archive.Length;

    descriptor.Defragment(archive);

    Assert.That(archive.Length, Is.LessThan(before));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "A", null), Is.EqualTo(SampleA));
    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "B", null), Is.EqualTo(SampleB));
  }

  [Test]
  public void Shrink_UsesMetadataPreservingCompaction() {
    var descriptor = new NuFxFormatDescriptor();
    using var source = CreateArchive(descriptor, ("A", SampleA));
    using var shrunk = new MemoryStream();

    descriptor.Shrink(source, shrunk);

    Assert.That(shrunk.Length, Is.LessThan(source.Length));
    shrunk.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(shrunk, "A", null), Is.EqualTo(SampleA));
  }

  [Test]
  public void Validator_RejectsMasterCrcCorruption() {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = CreateArchive(descriptor, ("ONE", SampleA));
    var bytes = archive.ToArray();
    bytes[0x20] ^= 0x80;
    using var damaged = new MemoryStream(bytes);

    var result = descriptor.ValidateStructure(damaged);

    Assert.That(result.IsValid, Is.False);
    Assert.That(result.Health, Is.EqualTo(FormatHealth.Damaged));
  }

  [Test]
  public void Create_RejectsEncryptionAndUnsafePath() {
    var descriptor = new NuFxFormatDescriptor();
    using var encrypted = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => descriptor.Create(encrypted,
      [ArchiveInputInfo.InMemory("A", SampleA)], new FormatCreateOptions { Password = "secret" }));

    var unsafeInput = ArchiveInputInfo.InMemory("../EVIL", SampleA);
    Assert.That(descriptor.CanAccept(unsafeInput, out _), Is.False);
  }

  private static MemoryStream CreateArchive(NuFxFormatDescriptor descriptor,
      params (string Name, byte[] Data)[] files) {
    var stream = new MemoryStream();
    descriptor.Create(stream,
      files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data)).ToList(),
      new FormatCreateOptions { MethodName = "nulzw2" });
    stream.Position = 0;
    return stream;
  }

  private static int FindNth(byte[] haystack, byte[] needle, int occurrence) {
    var found = 0;
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      if (!haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
        continue;
      found++;
      if (found == occurrence)
        return i;
    }
    return -1;
  }
}

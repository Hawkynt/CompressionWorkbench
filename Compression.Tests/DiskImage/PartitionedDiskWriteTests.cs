#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using Compression.Registry;
using FileFormat.PartitionedDisk;

namespace Compression.Tests.DiskImage;

/// <summary>
/// Write support of the raw partitioned-disk format: files edited inside a partition's
/// filesystem in place, partitions added and deleted, fresh disks created from partition images —
/// and every refusal leaving the image byte-identical.
/// </summary>
[TestFixture]
public class PartitionedDiskWriteTests {

  [SetUp]
  public void EnsureRegistered() => Compression.Lib.FormatRegistration.EnsureInitialized();

  private static readonly PartitionedDiskFormatDescriptor Desc = new();

  private static byte[] FatImage(string name, byte[] content, int sectors = 2880) {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile(name, content);
    return writer.Build(totalSectors: sectors);
  }

  private static MemoryStream CreateDisk(string scheme, int sectorSize, params (string Name, byte[] Data)[] images) {
    var ms = new MemoryStream();
    var options = new FormatCreateOptions { FormatSpecific = new(StringComparer.OrdinalIgnoreCase) {
      ["scheme"] = scheme, ["sectorSize"] = sectorSize.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
    Desc.Create(ms, [.. images.Select(i => ArchiveInputInfo.InMemory(i.Name, i.Data))], options);
    return ms;
  }

  private static string Prefix(Stream disk, int index) {
    var detection = PartitionTableDetector.Detect(disk);
    return PartitionedDiskLister.MakePartitionPrefix(detection.Partitions[index]);
  }

  private static byte[] ReadEntry(Stream disk, string name) {
    using var s = Desc.OpenEntry(disk, name, null);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  // ── Create ─────────────────────────────────────────────────────────

  [TestCase("gpt", 512)]
  [TestCase("gpt", 4096)]
  [TestCase("mbr", 512)]
  [Category("HappyPath")]
  public void Given_TwoFatImages_When_Created_Then_EachPartitionListsItsFileAtMiBAlignment(string scheme, int sectorSize) {
    using var disk = CreateDisk(scheme, sectorSize,
      ("boot.img", FatImage("A.TXT", "first"u8.ToArray())),
      ("data.img", FatImage("B.TXT", "second"u8.ToArray())));

    var detection = PartitionTableDetector.Detect(disk);
    Assert.That(detection.Scheme, Is.EqualTo(scheme.ToUpperInvariant()));
    Assert.That(detection.SectorSize, Is.EqualTo(sectorSize));
    Assert.That(detection.Partitions, Has.Count.EqualTo(2));
    Assert.That(detection.Partitions[0].StartOffset, Is.EqualTo(1024 * 1024));
    Assert.That(detection.Partitions[1].StartOffset % (1024 * 1024), Is.Zero);

    var names = Desc.List(disk, null).Select(e => e.Name).ToList();
    Assert.That(names, Has.Some.Match("^Partition1_[^/]+/A\\.TXT$"), string.Join(", ", names));
    Assert.That(names, Has.Some.Match("^Partition2_[^/]+/B\\.TXT$"), string.Join(", ", names));

    var verify = new PartitionEditor(disk).Verify();
    Assert.That(verify.IsValid, Is.True, string.Join("; ", verify.Issues));
  }

  [Test, Category("HappyPath")]
  public void Given_GptCreate_When_InputNamed_Then_PartitionNameIsTheStem() {
    using var disk = CreateDisk("gpt", 512, ("Partition1_EFI.raw", new byte[4096]));
    var part = PartitionTableDetector.Detect(disk).Partitions.Single();
    Assert.That(part.Name, Is.EqualTo("EFI.raw".Replace(".raw", "")));
    Assert.That(part.Size, Is.EqualTo(4096));
  }

  [Test, Category("ErrorHandling")]
  public void Given_LooseTextFile_When_Created_Then_Refused() {
    Assert.That(() => CreateDisk("gpt", 512, ("PROBE.TXT", "hello"u8.ToArray())), Throws.ArgumentException);
  }

  [Test, Category("ErrorHandling")]
  public void Given_NoInputs_When_Created_Then_Refused() {
    Assert.That(() => CreateDisk("gpt", 512), Throws.ArgumentException);
  }

  [Test, Category("Boundary")]
  public void Given_FiveImagesOnMbr_When_Created_Then_RefusedBecauseOnlyFourPrimariesExist() {
    var images = Enumerable.Range(1, 5).Select(i => ($"p{i}.raw", new byte[512])).ToArray();
    Assert.That(() => CreateDisk("mbr", 512, images), Throws.ArgumentException);
    Assert.That(() => CreateDisk("mbr", 512, images[..4]), Throws.Nothing);
  }

  [TestCase("scheme", "apm")]
  [TestCase("sectorSize", "1024")]
  [Category("ErrorHandling")]
  public void Given_UnknownOption_When_Created_Then_Refused(string key, string value) {
    var options = new FormatCreateOptions { FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { [key] = value } };
    Assert.That(() => Desc.Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a.raw", new byte[512])], options),
      Throws.ArgumentException);
  }

  // ── Edit inside a partition ────────────────────────────────────────

  [TestCase("gpt", 512)]
  [TestCase("gpt", 4096)]
  [TestCase("mbr", 512)]
  [Category("HappyPath")]
  public void Given_FatPartition_When_FileAdded_Then_ItReadsBackAndBytesOutsideThePartitionAreUntouched(string scheme, int sectorSize) {
    using var disk = CreateDisk(scheme, sectorSize,
      ("a.img", FatImage("A.TXT", "first"u8.ToArray())),
      ("b.img", FatImage("B.TXT", "second"u8.ToArray())));
    var before = disk.ToArray();
    var p1 = PartitionTableDetector.Detect(disk).Partitions[0];
    var prefix = Prefix(disk, 0);

    Desc.Add(disk, [ArchiveInputInfo.InMemory($"{prefix}/NEW.TXT", "added in place"u8.ToArray())]);

    Assert.That(ReadEntry(disk, $"{prefix}/NEW.TXT"), Is.EqualTo("added in place"u8.ToArray()));
    Assert.That(ReadEntry(disk, $"{prefix}/A.TXT"), Is.EqualTo("first"u8.ToArray()));
    var after = disk.ToArray();
    Assert.That(after.Length, Is.EqualTo(before.Length));
    Assert.That(after.AsSpan(0, (int)p1.StartOffset).SequenceEqual(before.AsSpan(0, (int)p1.StartOffset)), Is.True,
      "bytes before the partition changed");
    var end = (int)(p1.StartOffset + p1.Size);
    Assert.That(after.AsSpan(end).SequenceEqual(before.AsSpan(end)), Is.True, "bytes after the partition changed");
  }

  [Test, Category("HappyPath")]
  public void Given_FatPartition_When_FileRemoved_Then_ItIsGoneAndTheOtherStays() {
    using var disk = CreateDisk("gpt", 512, ("a.img", FatImage("A.TXT", "first"u8.ToArray())));
    var prefix = Prefix(disk, 0);
    Desc.Add(disk, [ArchiveInputInfo.InMemory($"{prefix}/KEEP.TXT", "keep"u8.ToArray())]);

    Desc.Remove(disk, [$"{prefix}/A.TXT"]);

    var names = Desc.List(disk, null).Select(e => e.Name).ToList();
    Assert.That(names, Has.None.EqualTo($"{prefix}/A.TXT"));
    Assert.That(names, Has.Some.EqualTo($"{prefix}/KEEP.TXT"));
  }

  [Test, Category("ErrorHandling")]
  public void Given_RawPartitionWithoutFilesystem_When_FileAdded_Then_RefusedAndByteIdentical() {
    using var disk = CreateDisk("gpt", 512, ("blob.raw", new byte[64 * 1024]));
    var before = disk.ToArray();
    var prefix = Prefix(disk, 0);

    Assert.That(() => Desc.Add(disk, [ArchiveInputInfo.InMemory($"{prefix}/X.TXT", "x"u8.ToArray())]),
      Throws.InvalidOperationException);
    Assert.That(disk.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("Boundary")]
  public void Given_FullFatVolume_When_OversizedFileAdded_Then_RefusedAndByteIdentical() {
    using var disk = CreateDisk("gpt", 512, ("a.img", FatImage("A.TXT", "first"u8.ToArray())));
    var before = disk.ToArray();
    var prefix = Prefix(disk, 0);

    Assert.That(() => Desc.Add(disk, [ArchiveInputInfo.InMemory($"{prefix}/BIG.BIN", new byte[4 * 1024 * 1024])]),
      Throws.InvalidOperationException);
    Assert.That(disk.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("ErrorHandling")]
  public void Given_TwoEditsWhereTheSecondFails_When_Added_Then_NeitherIsWritten() {
    using var disk = CreateDisk("gpt", 512,
      ("a.img", FatImage("A.TXT", "first"u8.ToArray())),
      ("blob.raw", new byte[64 * 1024]));
    var before = disk.ToArray();

    Assert.That(() => Desc.Add(disk, [
      ArchiveInputInfo.InMemory($"{Prefix(disk, 0)}/OK.TXT", "fine"u8.ToArray()),
      ArchiveInputInfo.InMemory($"{Prefix(disk, 1)}/NO.TXT", "refused"u8.ToArray()),
    ]), Throws.InvalidOperationException);
    Assert.That(disk.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("ErrorHandling")]
  public void Given_NameOutsideAnyPartition_When_Removed_Then_FileNotFoundAndByteIdentical() {
    using var disk = CreateDisk("gpt", 512, ("a.img", FatImage("A.TXT", "first"u8.ToArray())));
    var before = disk.ToArray();
    Assert.That(() => Desc.Remove(disk, ["Partition9_Nope/A.TXT"]), Throws.InstanceOf<FileNotFoundException>());
    Assert.That(disk.ToArray(), Is.EqualTo(before));
  }

  // ── Whole partitions ───────────────────────────────────────────────

  [TestCase("gpt")]
  [TestCase("mbr")]
  [Category("HappyPath")]
  public void Given_TwoPartitions_When_FirstRemovedWhole_Then_TableShrinksAndItsSectorsStay(string scheme) {
    using var disk = CreateDisk(scheme, 512,
      ("a.img", FatImage("A.TXT", "first"u8.ToArray())),
      ("b.img", FatImage("B.TXT", "second"u8.ToArray())));
    var first = PartitionTableDetector.Detect(disk).Partitions[0];
    var payload = disk.ToArray().AsSpan((int)first.StartOffset, (int)first.Size).ToArray();

    Desc.Remove(disk, [Prefix(disk, 0)]);

    var left = PartitionTableDetector.Detect(disk).Partitions;
    Assert.That(left, Has.Count.EqualTo(1));
    Assert.That(Desc.List(disk, null).Select(e => e.Name), Has.Some.EndsWith("/B.TXT"));
    Assert.That(disk.ToArray().AsSpan((int)first.StartOffset, (int)first.Size).ToArray(), Is.EqualTo(payload));
    Assert.That(new PartitionEditor(disk).Verify().IsValid, Is.True);
  }

  [Test, Category("HappyPath")]
  public void Given_GptDisk_When_FilesystemImageAddedAtRoot_Then_ItBecomesAMiBAlignedPartition() {
    using var disk = CreateDisk("gpt", 512, ("a.img", FatImage("A.TXT", "first"u8.ToArray())));
    disk.SetLength(disk.Length + 4 * 1024 * 1024);
    new PartitionEditor(disk).Reload();
    // Re-home the backup GPT at the new end: a grown disk is what partition tools hand over.
    var grow = new PartitionEditor(disk);
    grow.ConvertGptToMbr();
    grow.ConvertMbrToGpt();

    Desc.Add(disk, [ArchiveInputInfo.InMemory("second.img", FatImage("N.TXT", "new"u8.ToArray()))]);

    var parts = PartitionTableDetector.Detect(disk).Partitions;
    Assert.That(parts, Has.Count.EqualTo(2));
    Assert.That(parts[1].StartOffset % (1024 * 1024), Is.Zero);
    Assert.That(Desc.List(disk, null).Select(e => e.Name), Has.Some.EndsWith("/N.TXT"));
    Assert.That(new PartitionEditor(disk).Verify().IsValid, Is.True);
  }

  [Test, Category("Boundary")]
  public void Given_NoFreeSpace_When_ImageAddedAtRoot_Then_RefusedAndByteIdentical() {
    using var disk = CreateDisk("gpt", 512, ("a.img", FatImage("A.TXT", "first"u8.ToArray())));
    var before = disk.ToArray();
    Assert.That(() => Desc.Add(disk, [ArchiveInputInfo.InMemory("second.img", FatImage("N.TXT", "new"u8.ToArray()))]),
      Throws.InvalidOperationException);
    Assert.That(disk.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("ErrorHandling")]
  public void Given_LooseFileAtRoot_When_Added_Then_RefusedAndByteIdentical() {
    using var disk = CreateDisk("gpt", 512, ("a.img", FatImage("A.TXT", "first"u8.ToArray())));
    var before = disk.ToArray();
    Assert.That(() => Desc.Add(disk, [ArchiveInputInfo.InMemory("notes.txt", "text"u8.ToArray())]),
      Throws.InvalidOperationException);
    Assert.That(disk.ToArray(), Is.EqualTo(before));
  }

  // ── Staged window ──────────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Given_StagedWindow_When_WritePastItsEnd_Then_ThrowsAndDiskUntouched() {
    var bytes = Enumerable.Range(0, 8192).Select(i => (byte)i).ToArray();
    using var disk = new MemoryStream(bytes.ToArray());
    var window = new StagedWindowStream(disk, 1024, 4096);
    window.Position = 4090;
    Assert.That(() => window.Write(new byte[7]), Throws.InstanceOf<IOException>());
    Assert.That(() => window.SetLength(4097), Throws.InstanceOf<IOException>());
    window.SetLength(100); // shrinking is a no-op
    Assert.That(disk.ToArray(), Is.EqualTo(bytes));
  }

  [Test, Category("HappyPath")]
  public void Given_StagedWrites_When_ReadBackBeforeCommit_Then_SeenOnlyThroughTheWindowUntilCommitted() {
    var bytes = new byte[16384];
    using var disk = new MemoryStream(bytes.ToArray());
    var window = new StagedWindowStream(disk, 4096, 8192);
    window.Position = 4094; // straddles a page boundary
    window.Write([1, 2, 3, 4]);
    window.Position = 4094;
    var back = new byte[4];
    window.ReadExactly(back);
    Assert.That(back, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
    Assert.That(disk.ToArray(), Is.EqualTo(bytes), "nothing reaches the disk before Commit");

    window.Commit();
    Assert.That(disk.ToArray().AsSpan(4096 + 4094, 4).ToArray(), Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
  }
}

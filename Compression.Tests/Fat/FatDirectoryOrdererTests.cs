using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// The FAT directory sort (<see cref="IFilesystemDirectoryOrderer"/>): every directory ends
/// up in name order, and nothing else about the volume changes — same size, same bytes in
/// every file, same clusters, same timestamps; a pass it cannot carry out exactly is refused
/// with the image byte for byte as it was.
/// </summary>
[TestFixture]
public sealed class FatDirectoryOrdererTests {

  private static readonly DateTime Stamp = new(2001, 2, 3, 4, 5, 6);

  private static FatWriter UnsortedTree() {
    var writer = new FatWriter();
    writer.AddFile("Zulu long file name.txt", "root-z"u8.ToArray(), Stamp);
    writer.AddFile("alpha.txt", "root-a"u8.ToArray(), Stamp.AddDays(1));
    writer.AddFile("Middle Name.bin", new byte[5000], Stamp.AddDays(2));
    writer.AddFile("BETA.TXT", "root-b"u8.ToArray(), Stamp.AddDays(3));
    writer.AddFile("Folder/zz child with a long name.txt", "child-z"u8.ToArray(), Stamp);
    writer.AddFile("Folder/Alpha child.txt", "child-a"u8.ToArray(), Stamp);
    writer.AddFile("Folder/Deeper/omega.dat", RandomBytes(3000, 1), Stamp);
    writer.AddFile("Folder/Deeper/Beta.dat", RandomBytes(700, 2), Stamp);
    return writer;
  }

  private static byte[] RandomBytes(int count, int seed) {
    var bytes = new byte[count];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  private static IEnumerable<TestCaseData> Variants() {
    yield return new TestCaseData(12, 2880).SetName("SortEntries_Fat12");
    yield return new TestCaseData(16, 32_768).SetName("SortEntries_Fat16");
    yield return new TestCaseData(32, 140_000).SetName("SortEntries_Fat32");
  }

  [TestCaseSource(nameof(Variants)), Category("HappyPath")]
  public void GivenAnUnsortedVolume_WhenSorted_ThenEveryDirectoryIsInNameOrderAndNothingElseChanged(int fatType, int sectors) {
    using var image = new MemoryStream(UnsortedTree().Build(totalSectors: sectors, forcedFatType: fatType, volumeLabel: "CWBLABEL"));
    var before = Snapshot(image);
    var length = image.Length;

    new FatFormatDescriptor().SortDirectoryEntries(image);

    var after = Snapshot(image);
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(length), "the image size changed");
      Assert.That(after.Files, Is.EqualTo(before.Files), "a file's bytes, length or time changed");
      Assert.That(after.Extents, Is.EqualTo(before.Extents), "a file moved: sorting must not touch allocation");
      Assert.That(after.Boot, Is.EqualTo(before.Boot), "the boot sector (label, serial, geometry) changed");
      foreach (var (folder, names) in after.Listing)
        Assert.That(names, Is.EqualTo(Sorted(names)), $"'{folder}' is not in name order");
      Assert.That(before.Listing[""], Is.Not.EqualTo(Sorted(before.Listing[""])), "precondition: the root started out unsorted");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenASortedVolume_WhenSortedAgain_ThenTheImageIsByteIdentical() {
    using var image = new MemoryStream(UnsortedTree().Build());
    var descriptor = new FatFormatDescriptor();
    descriptor.SortDirectoryEntries(image);
    var once = image.ToArray();

    descriptor.SortDirectoryEntries(image);

    Assert.That(image.ToArray(), Is.EqualTo(once), "sorting a sorted volume must change nothing");
  }

  [Test, Category("EdgeCase")]
  public void GivenAnEmptyVolume_WhenSorted_ThenNothingChanges() {
    var original = new FatWriter().Build();
    using var image = new MemoryStream((byte[])original.Clone());

    new FatFormatDescriptor().SortDirectoryEntries(image);

    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("EdgeCase")]
  public void GivenAVolumeLabelAndDeletedSlots_WhenSorted_ThenTheLabelStaysFirstAndTheDeletedSlotsAreKept() {
    var writer = UnsortedTree();
    using var image = new MemoryStream(writer.Build(volumeLabel: "CWBLABEL"));
    var descriptor = new FatFormatDescriptor();
    descriptor.Remove(image, ["alpha.txt"]);
    var deletedBefore = RootSlots(image).Count(static s => s[0] == 0xE5);
    Assume.That(deletedBefore, Is.GreaterThan(0), "precondition: the removal left free slots in the root");

    descriptor.SortDirectoryEntries(image);

    var slots = RootSlots(image);
    Assert.Multiple(() => {
      Assert.That(slots[0][11] & 0x08, Is.EqualTo(0x08), "the volume label entry must stay the first slot");
      Assert.That(slots.Count(static s => s[0] == 0xE5), Is.EqualTo(deletedBefore), "deleted slots are kept (wiping them is another verb)");
    });
  }

  [Test, Category("Boundary")]
  public void GivenARootLongerThanOneCluster_WhenSortedOnFat32_ThenTheWholeChainIsSorted() {
    var writer = new FatWriter();
    for (var i = 199; i >= 0; --i)
      writer.AddFile($"entry number {i:D3} with a long name.txt", [(byte)i], Stamp);
    using var image = new MemoryStream(writer.Build(totalSectors: 140_000, forcedFatType: 32, requestedClusterSize: 512));
    var before = Snapshot(image);

    new FatFormatDescriptor().SortDirectoryEntries(image);

    var after = Snapshot(image);
    Assert.Multiple(() => {
      Assert.That(after.Files, Is.EqualTo(before.Files));
      Assert.That(after.Listing[""], Is.EqualTo(Sorted(after.Listing[""])));
      Assert.That(after.Listing[""], Has.Count.EqualTo(200));
    });
  }

  [Test, Category("Exception")]
  public void GivenALongNameWithTheWrongChecksum_WhenSorted_ThenItIsRefusedAndTheImageIsUntouched() {
    var writer = new FatWriter();
    writer.AddFile("zz long file name.txt", "z"u8.ToArray(), Stamp);
    writer.AddFile("aa long file name.txt", "a"u8.ToArray(), Stamp);
    using var image = new MemoryStream(writer.Build());
    var slot = RootSlotOffsets(image).First(o => (ReadAt(image, o + 11, 1)[0] & 0x3F) == 0x0F);
    var checksum = ReadAt(image, slot + 13, 1)[0];
    image.Position = slot + 13;
    image.WriteByte((byte)(checksum ^ 0xFF));
    var original = image.ToArray();

    Assert.That(() => new FatFormatDescriptor().SortDirectoryEntries(image), Throws.TypeOf<NotSupportedException>());
    Assert.That(image.ToArray(), Is.EqualTo(original), "a refused sort must leave every byte in place");
  }

  [Test, Category("Exception")]
  public void GivenAStreamThatCannotBeWritten_WhenSorted_ThenItIsRejectedUpFront() {
    using var image = new MemoryStream(UnsortedTree().Build(), writable: false);
    Assert.That(() => new FatFormatDescriptor().SortDirectoryEntries(image), Throws.TypeOf<ArgumentException>());
  }

  [Test, Category("Registry")]
  public void TheDescriptor_AdvertisesSortingSeparatelyFromExtentDefragmentation() {
    var profile = MaintenanceCapabilities.Describe(new FatFormatDescriptor());
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.SortDirectoryEntries), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.DefragmentExtents), Is.True);
      Assert.That(profile.Supports(DefragFeature.Packing | DefragFeature.CarveHole | DefragFeature.AscendingOrder), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.ChangeGeometry), Is.False,
        "a FAT relayout keeps names and bytes but not the serial, label or attributes");
    });
  }

  internal static IReadOnlyList<string> Sorted(IEnumerable<string> names)
    => [.. names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ThenBy(static n => n, StringComparer.Ordinal)];

  private sealed record VolumeSnapshot(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<(string? Name, long Offset, long Length)> Extents,
    IReadOnlyDictionary<string, List<string>> Listing,
    byte[] Boot);

  private static VolumeSnapshot Snapshot(MemoryStream image) {
    image.Position = 0;
    var reader = new FatReader(image, leaveOpen: true);
    var files = reader.Entries.Where(static e => !e.IsDirectory).ToDictionary(
      static e => e.Name,
      e => $"{e.Size}|{e.LastModified:O}|{Convert.ToHexString(SHA256.HashData(reader.Extract(e)))}",
      StringComparer.Ordinal);
    var listing = new Dictionary<string, List<string>>(StringComparer.Ordinal) { [""] = [] };
    foreach (var entry in reader.Entries) {
      var name = entry.Name.Replace('\\', '/');
      var slash = name.LastIndexOf('/');
      var folder = slash < 0 ? "" : name[..slash];
      if (!listing.TryGetValue(folder, out var names)) listing[folder] = names = [];
      names.Add(name[(slash + 1)..]);
    }
    image.Position = 0;
    var extents = new FatFormatDescriptor().EnumerateExtents(image)
      .Where(static e => e.Kind == DefragBlockKind.Used)
      .Select(static e => (e.FileName, e.Offset, e.Length))
      .OrderBy(static e => e.FileName, StringComparer.Ordinal).ThenBy(static e => e.Offset)
      .ToList();
    return new VolumeSnapshot(files, extents, listing, ReadAt(image, 0, 512));
  }

  private static List<long> RootSlotOffsets(MemoryStream image) {
    var boot = ReadAt(image, 0, 64);
    var bytesPerSector = BitConverter.ToUInt16(boot, 11);
    var root = (long)(BitConverter.ToUInt16(boot, 14) + boot[16] * BitConverter.ToUInt16(boot, 22)) * bytesPerSector;
    var entries = BitConverter.ToUInt16(boot, 17);
    return [.. Enumerable.Range(0, entries).Select(i => root + i * 32L).TakeWhile(o => ReadAt(image, o, 1)[0] != 0)];
  }

  private static List<byte[]> RootSlots(MemoryStream image) => [.. RootSlotOffsets(image).Select(o => ReadAt(image, o, 32))];

  private static byte[] ReadAt(Stream image, long offset, int count) {
    var data = new byte[count];
    image.Position = offset;
    image.ReadExactly(data);
    return data;
  }
}

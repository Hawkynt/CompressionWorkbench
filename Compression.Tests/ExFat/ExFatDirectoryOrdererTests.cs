using System.Security.Cryptography;
using Compression.Registry;
using FileSystem.ExFat;

namespace Compression.Tests.ExFat;

/// <summary>
/// The exFAT directory sort (<see cref="IFilesystemDirectoryOrderer"/>): every directory ends
/// up in name order with each File entry set moved whole, and nothing else changes — size,
/// file bytes, clusters, the boot region; a set it cannot carry is refused untouched.
/// </summary>
[TestFixture]
public sealed class ExFatDirectoryOrdererTests {

  private static ExFatWriter UnsortedTree() {
    var writer = new ExFatWriter();
    writer.AddFile("Zulu.txt", "root-z"u8.ToArray());
    writer.AddFile("alpha.txt", "root-a"u8.ToArray());
    writer.AddFile("Middle name with more than fifteen characters.bin", new byte[9000]);
    writer.AddFile("Folder/zz child.txt", "child-z"u8.ToArray());
    writer.AddFile("Folder/Alpha child.txt", "child-a"u8.ToArray());
    writer.AddFile("Folder/Deeper/omega.dat", RandomBytes(5000, 3));
    writer.AddFile("Folder/Deeper/Beta.dat", RandomBytes(700, 4));
    return writer;
  }

  private static byte[] RandomBytes(int count, int seed) {
    var bytes = new byte[count];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }

  [Test, Category("HappyPath")]
  public void GivenAnUnsortedVolume_WhenSorted_ThenEveryDirectoryIsInNameOrderAndNothingElseChanged() {
    using var image = new MemoryStream(UnsortedTree().Build(volumeLabel: "CWBLABEL"));
    var before = Snapshot(image);
    var length = image.Length;

    new ExFatFormatDescriptor().SortDirectoryEntries(image);

    var after = Snapshot(image);
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(length), "the image size changed");
      Assert.That(after.Files, Is.EqualTo(before.Files), "a file's bytes, length or time changed");
      Assert.That(after.Extents, Is.EqualTo(before.Extents), "a file moved: sorting must not touch allocation");
      Assert.That(after.Boot, Is.EqualTo(before.Boot), "the boot sector (serial, geometry) changed");
      foreach (var (folder, names) in after.Listing)
        Assert.That(names, Is.EqualTo(Fat.FatDirectoryOrdererTests.Sorted(names)), $"'{folder}' is not in name order");
      Assert.That(before.Listing[""], Is.Not.EqualTo(Fat.FatDirectoryOrdererTests.Sorted(before.Listing[""])), "precondition: the root started out unsorted");
    });
  }

  [Test, Category("HappyPath")]
  public void GivenASortedVolume_WhenSortedAgain_ThenTheImageIsByteIdentical() {
    using var image = new MemoryStream(UnsortedTree().Build());
    var descriptor = new ExFatFormatDescriptor();
    descriptor.SortDirectoryEntries(image);
    var once = image.ToArray();

    descriptor.SortDirectoryEntries(image);

    Assert.That(image.ToArray(), Is.EqualTo(once));
  }

  [Test, Category("EdgeCase")]
  public void GivenAnEmptyVolume_WhenSorted_ThenNothingChanges() {
    var original = new ExFatWriter().Build(volumeLabel: "EMPTY");
    using var image = new MemoryStream((byte[])original.Clone());

    new ExFatFormatDescriptor().SortDirectoryEntries(image);

    Assert.That(image.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("Exception")]
  public void GivenAnEntrySetWithABadChecksum_WhenSorted_ThenItIsRefusedAndTheImageIsUntouched() {
    using var image = new MemoryStream(UnsortedTree().Build());
    var set = FindFileEntry(image);
    image.Position = set + 2;
    image.WriteByte(0x5A);
    image.WriteByte(0xA5);
    var original = image.ToArray();

    Assert.That(() => new ExFatFormatDescriptor().SortDirectoryEntries(image), Throws.TypeOf<NotSupportedException>());
    Assert.That(image.ToArray(), Is.EqualTo(original), "a refused sort must leave every byte in place");
  }

  [Test, Category("Registry")]
  public void TheDescriptor_AdvertisesSortingButNotGeometry() {
    var profile = MaintenanceCapabilities.Describe(new ExFatFormatDescriptor());
    Assert.Multiple(() => {
      Assert.That(profile.Supports(MaintenanceCapability.SortDirectoryEntries), Is.True);
      Assert.That(profile.Supports(MaintenanceCapability.DefragmentExtents), Is.True);
      Assert.That(profile.Supports(DefragFeature.AscendingOrder), Is.False, "exFAT refuses ascending order");
      Assert.That(profile.Supports(MaintenanceCapability.ChangeGeometry), Is.False);
    });
  }

  private sealed record VolumeSnapshot(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<(string? Name, long Offset, long Length)> Extents,
    IReadOnlyDictionary<string, List<string>> Listing,
    byte[] Boot);

  private static VolumeSnapshot Snapshot(MemoryStream image) {
    image.Position = 0;
    using var reader = new ExFatReader(image, leaveOpen: true);
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
    var extents = new ExFatFormatDescriptor().EnumerateExtents(image)
      .Where(static e => e.Kind == DefragBlockKind.Used)
      .Select(static e => (e.FileName, e.Offset, e.Length))
      .OrderBy(static e => e.FileName, StringComparer.Ordinal).ThenBy(static e => e.Offset)
      .ToList();
    var boot = new byte[512];
    image.Position = 0;
    image.ReadExactly(boot);
    return new VolumeSnapshot(files, extents, listing, boot);
  }

  /// <summary>Byte offset of the first File entry (0x85) in the root directory.</summary>
  private static long FindFileEntry(MemoryStream image) {
    var boot = new byte[512];
    image.Position = 0;
    image.ReadExactly(boot);
    var bytesPerSector = 1 << boot[108];
    var clusterSize = bytesPerSector << boot[109];
    var heap = (long)BitConverter.ToUInt32(boot, 88) * bytesPerSector;
    var root = BitConverter.ToUInt32(boot, 96);
    var start = heap + (root - 2L) * clusterSize;
    for (var offset = start; offset < start + clusterSize; offset += 32) {
      image.Position = offset;
      if (image.ReadByte() == 0x85) return offset;
    }
    throw new InvalidDataException("No File entry in the first root cluster.");
  }
}

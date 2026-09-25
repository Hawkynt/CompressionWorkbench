using Compression.Registry;

namespace Compression.Tests.Operations;

[TestFixture]
public class DirectoryOrderingTests {
  [Test, Category("RoundTrip")]
  public void Fat_SortsEntryGroupsRecursively_WithoutChangingSemantics() {
    var writer = new FileSystem.Fat.FatWriter();
    writer.AddFile("zeta.txt", "z"u8.ToArray());
    writer.AddFile("A Long Root File Name.txt", "long"u8.ToArray());
    writer.AddFile("alpha.txt", "a"u8.ToArray());
    writer.AddFile("docs/zeta.txt", "dz"u8.ToArray());
    writer.AddFile("docs/A Long Child File Name.txt", "dl"u8.ToArray());
    writer.AddFile("docs/alpha.txt", "da"u8.ToArray());
    using var image = new MemoryStream(writer.Build());

    var descriptor = new FileSystem.Fat.FatFormatDescriptor();
    var before = SemanticPreservationManifest.Capture(image, descriptor);

    ((IFilesystemDirectoryOrderer)descriptor).SortDirectoryEntries(image);

    image.Position = 0;
    var after = SemanticPreservationManifest.Capture(image, descriptor);
    before.VerifyEquivalent(after);

    image.Position = 0;
    using var reader = new FileSystem.Fat.FatReader(image, leaveOpen: true);
    AssertDirectoryIsSorted(reader.Entries.Select(entry => entry.Name));
  }

  [Test, Category("RoundTrip")]
  public void ExFat_SortsEntrySetsRecursively_WithoutChangingSemantics() {
    var writer = new FileSystem.ExFat.ExFatWriter();
    writer.AddFile("zeta.txt", "z"u8.ToArray());
    writer.AddFile("A Long Root File Name.txt", "long"u8.ToArray());
    writer.AddFile("alpha.txt", "a"u8.ToArray());
    writer.AddFile("docs/zeta.txt", "dz"u8.ToArray());
    writer.AddFile("docs/A Long Child File Name.txt", "dl"u8.ToArray());
    writer.AddFile("docs/alpha.txt", "da"u8.ToArray());
    using var image = new MemoryStream(writer.Build());

    var descriptor = new FileSystem.ExFat.ExFatFormatDescriptor();
    var before = SemanticPreservationManifest.Capture(image, descriptor);

    ((IFilesystemDirectoryOrderer)descriptor).SortDirectoryEntries(image);

    image.Position = 0;
    var after = SemanticPreservationManifest.Capture(image, descriptor);
    before.VerifyEquivalent(after);

    image.Position = 0;
    using var reader = new FileSystem.ExFat.ExFatReader(image, leaveOpen: true);
    AssertDirectoryIsSorted(reader.Entries.Select(entry => entry.Name));
  }

  private static void AssertDirectoryIsSorted(IEnumerable<string> paths) {
    var byDirectory = paths
      .GroupBy(path => {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
      });

    foreach (var group in byDirectory) {
      var names = group.Select(path => {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? path : path[(separator + 1)..];
      }).ToArray();
      var sorted = names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(name => name, StringComparer.Ordinal)
        .ToArray();
      Assert.That(names, Is.EqualTo(sorted), $"directory '{group.Key}' is not sorted");
    }
  }
}

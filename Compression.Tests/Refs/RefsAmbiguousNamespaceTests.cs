using Compression.Registry;
using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public sealed class RefsAmbiguousNamespaceTests {
  [Test, Category("ErrorHandling")]
  public void WritableResolver_RejectsCaseFoldedDuplicateNames() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", [1, 2, 3])
      .WithFile("ALPHA.bin", [4, 5, 6])
      .Build();
    using var stream = new MemoryStream(image, writable: true);

    var resolver = new RefsWritableNamespace(RefsMetadataReader.Open(stream));
    Assert.Throws<NotSupportedException>(() => resolver.ResolveDirectoryEntry("alpha.bin"));
  }

  [Test, Category("ErrorHandling")]
  public void OfflineMutations_RejectAmbiguousPathBeforeWriting() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", [1, 2, 3])
      .WithFile("ALPHA.bin", [4, 5, 6])
      .WithFile("destination.bin", [7, 8, 9])
      .Build();
    var original = image.ToArray();
    using var stream = new MemoryStream(image, writable: true);

    Assert.Multiple(() => {
      Assert.Throws<NotSupportedException>(() => RefsOfflineModifier.Add(
        stream, [ArchiveInputInfo.InMemory("alpha.bin", [9, 9, 9])]));
      Assert.Throws<NotSupportedException>(() => RefsOfflineModifier.Remove(stream, ["alpha.bin"]));
      Assert.Throws<NotSupportedException>(() => RefsOfflineBlockCloner.CloneWholeFile(
        stream, "alpha.bin", "destination.bin"));
      Assert.That(image, Is.EqualTo(original));
    });
  }
}

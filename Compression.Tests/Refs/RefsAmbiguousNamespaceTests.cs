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
    Assert.That(() => resolver.ResolveDirectoryEntry("alpha.bin"),
      Throws.TypeOf<NotSupportedException>().With.Message.Contains("ambiguous"));
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

    // Add also refuses a missing path with NotSupportedException, so the
    // message is what proves the ambiguity check fired rather than that one.
    static NUnit.Framework.Constraints.IResolveConstraint Ambiguous() => Throws.TypeOf<NotSupportedException>().With.Message.Contains("ambiguous");
    Assert.Multiple(() => {
      Assert.That(() => RefsOfflineModifier.Add(
        stream, [ArchiveInputInfo.InMemory("alpha.bin", [9, 9, 9])]), Ambiguous());
      Assert.That(() => RefsOfflineModifier.Remove(stream, ["alpha.bin"]), Ambiguous());
      Assert.That(() => RefsOfflineBlockCloner.CloneWholeFile(
        stream, "alpha.bin", "destination.bin"), Ambiguous());
      Assert.That(() => RefsOfflineBlockCloner.CloneWholeFile(
        stream, "destination.bin", "ALPHA.BIN"), Ambiguous());
      Assert.That(image, Is.EqualTo(original));
    });
  }

  [Test, Category("HappyPath")]
  public void UniqueCaseFoldedPath_StillResolvesToTheOnlyMatch() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", [1, 2, 3])
      .WithFile("beta.bin", [4, 5, 6])
      .Build();
    using var stream = new MemoryStream(image, writable: true);

    var location = new RefsWritableNamespace(RefsMetadataReader.Open(stream)).ResolveDirectoryEntry("ALPHA.BIN");

    Assert.That(location.EntryRow.Key.AsSpan(4).ToArray(), Is.EqualTo(System.Text.Encoding.Unicode.GetBytes("alpha.bin")));
  }
}

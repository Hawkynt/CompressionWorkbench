using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public sealed class RefsOfflineRenameTests {
  [Test, Category("HappyPath")]
  public void RenameInPlace_PreservesFileIdentityDataAndDirectoryValue() {
    var content = Enumerable.Range(0, 2 * RefsSyntheticVolume.ClusterSize + 17)
      .Select(i => (byte)(i * 29)).ToArray();
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", content)
      .WithFile("other.bin", [7, 8, 9])
      .Build();
    using var stream = new MemoryStream(image, writable: true);

    var before = RefsMetadataReader.Open(stream);
    var source = new RefsWritableNamespace(before).ResolveStorage("alpha.bin");
    var directoryValue = source.Entry.EntryRow.Value.ToArray();
    var backingKey = source.StorageRow.Key.ToArray();
    var backingValue = source.StorageRow.Value.ToArray();
    var oldCheckpoint = before.ActiveCheckpointLcn;
    var beforeProbe = new RefsImageProbe(image);
    var originalClusters = beforeProbe.ReadFiles(beforeProbe.ActiveCheckpoint())["alpha.bin"].Clusters;

    new RefsFormatDescriptor().Rename(stream, "alpha.bin", "renamed.bin");

    var after = RefsMetadataReader.Open(stream);
    var renamed = new RefsWritableNamespace(after).ResolveStorage("renamed.bin");
    var paths = new RefsNamespaceReader(after).ReadAll().Select(file => file.Path).ToArray();
    var afterProbe = new RefsImageProbe(image);
    var checkpoint = afterProbe.ActiveCheckpoint();
    var files = afterProbe.ReadFiles(checkpoint);

    Assert.Multiple(() => {
      Assert.That(after.ActiveCheckpointLcn, Is.Not.EqualTo(oldCheckpoint));
      Assert.That(paths, Is.EquivalentTo(new[] { "renamed.bin", "other.bin" }));
      Assert.That(renamed.Entry.EntryRow.Value, Is.EqualTo(directoryValue));
      Assert.That(renamed.StorageRow.Key, Is.EqualTo(backingKey));
      Assert.That(renamed.StorageRow.Value, Is.EqualTo(backingValue));
      Assert.That(files["renamed.bin"].Clusters, Is.EqualTo(originalClusters));
      Assert.That(afterProbe.ReadFileContent(files["renamed.bin"]), Is.EqualTo(content));
      Assert.That(afterProbe.ReadFileContent(files["other.bin"]), Is.EqualTo(new byte[] { 7, 8, 9 }));
    });
  }

  [Test, Category("ErrorHandling")]
  public void ExistingDestination_RefusesBeforeChangingImage() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", [1, 2, 3])
      .WithFile("other.bin", [4, 5, 6])
      .Build();
    var original = image.ToArray();
    using var stream = new MemoryStream(image, writable: true);

    Assert.Throws<IOException>(() => new RefsFormatDescriptor().Rename(stream, "alpha.bin", "other.bin"));
    Assert.That(image, Is.EqualTo(original));
  }

  [Test, Category("ErrorHandling")]
  public void AmbiguousCaseSensitiveSource_RefusesBeforeChangingImage() {
    var image = new RefsSyntheticVolume()
      .WithFile("alpha.bin", [1])
      .WithFile("ALPHA.bin", [2])
      .Build();
    var original = image.ToArray();
    using var stream = new MemoryStream(image, writable: true);

    Assert.Throws<NotSupportedException>(() => new RefsFormatDescriptor().Rename(stream, "alpha.bin", "renamed.bin"));
    Assert.That(image, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void CaseOnlyRename_ChangesKeyWithoutReallocatingData() {
    var image = new RefsSyntheticVolume().WithFile("alpha.bin", [1, 2, 3]).Build();
    using var stream = new MemoryStream(image, writable: true);

    new RefsFormatDescriptor().Rename(stream, "alpha.bin", "ALPHA.bin");

    var metadata = RefsMetadataReader.Open(stream);
    Assert.That(new RefsNamespaceReader(metadata).ReadAll().Single().Path, Is.EqualTo("ALPHA.bin"));
  }
}

using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

[TestFixture]
public sealed class FatDirectoryOrdererTests {
  [Test]
  [Category("Optimization")]
  [Category("Regression")]
  public void SortDirectoryEntries_RecursesAndPreservesLfnPayloadAndAllocation() {
    var writer = new FatWriter();
    writer.AddFile("Zulu long filename.txt", "root-z"u8.ToArray());
    writer.AddFile("alpha.txt", "root-a"u8.ToArray());
    writer.AddFile("Middle Name.bin", "root-m"u8.ToArray());
    writer.AddFile("Folder/Zulu child with long name.txt", "child-z"u8.ToArray());
    writer.AddFile("Folder/alpha child.txt", "child-a"u8.ToArray());
    writer.AddFile("Folder/Middle child.bin", "child-m"u8.ToArray());

    using var image = new MemoryStream(writer.Build());
    var descriptor = new FatFormatDescriptor();
    var before = Snapshot(image, descriptor);

    descriptor.SortDirectoryEntries(image);

    var after = Snapshot(image, descriptor);
    Assert.Multiple(() => {
      Assert.That(after.Files, Is.EqualTo(before.Files), "logical file bytes changed");
      Assert.That(after.Layout, Is.EqualTo(before.Layout), "file allocation/extents changed");
      Assert.That(after.RootNames,
        Is.EqualTo(after.RootNames.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ThenBy(static n => n, StringComparer.Ordinal)),
        "root entries are not sorted");
      Assert.That(after.ChildNames,
        Is.EqualTo(after.ChildNames.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ThenBy(static n => n, StringComparer.Ordinal)),
        "subdirectory entries are not sorted");
    });
  }

  [Test]
  [Category("Registry")]
  public void Descriptor_AdvertisesDirectoryOrderingSeparatelyFromDefragmentation() {
    IFormatDescriptor descriptor = new FatFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(descriptor, Is.InstanceOf<IFilesystemDirectoryOrderer>());
      Assert.That(descriptor, Is.InstanceOf<IFilesystemBlockMover>());
      Assert.That(OptimizationCapabilities.CanSortDirectoryEntries(descriptor), Is.True);
      Assert.That(OptimizationCapabilities.CanDefragmentExtents(descriptor), Is.True);
    });
  }

  private static SnapshotResult Snapshot(MemoryStream image, FatFormatDescriptor descriptor) {
    image.Position = 0;
    using var reader = new FatReader(image, leaveOpen: true);
    var files = reader.Entries
      .Where(static e => !e.IsDirectory)
      .ToDictionary(
        static e => e.Name,
        e => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(reader.Extract(e))),
        StringComparer.Ordinal);

    var rootNames = reader.Entries
      .Where(static e => !e.Name.Contains('/'))
      .Select(static e => e.Name)
      .ToArray();
    var childNames = reader.Entries
      .Where(static e => e.Name.StartsWith("Folder/", StringComparison.Ordinal))
      .Select(static e => e.Name["Folder/".Length..])
      .ToArray();

    image.Position = 0;
    var layout = descriptor.EnumerateExtents(image)
      .Where(static e => e.Kind == DefragBlockKind.Used)
      .Select(static e => (e.FileName, e.Offset, e.Length))
      .OrderBy(static e => e.FileName, StringComparer.Ordinal)
      .ThenBy(static e => e.Offset)
      .ToArray();

    return new SnapshotResult(files, layout, rootNames, childNames);
  }

  private sealed record SnapshotResult(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<(string? FileName, long Offset, long Length)> Layout,
    IReadOnlyList<string> RootNames,
    IReadOnlyList<string> ChildNames);
}

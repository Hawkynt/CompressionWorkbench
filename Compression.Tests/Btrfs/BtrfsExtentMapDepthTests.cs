using Compression.Registry;

namespace Compression.Tests.Btrfs;

/// <summary>
/// The block map must describe a volume whose fs tree has more than one leaf.
/// </summary>
/// <remarks>
/// <para>An fs tree is a single leaf only while it is small. Past roughly
/// fourteen files it gains a level, and the node its root names then holds key
/// pointers rather than items. The map read that one node, found no item it
/// recognised, and reported a volume of twenty-eight readable files as holding
/// no file data at all.</para>
///
/// <para>Every consumer reads that as free space. It is what let a wipe zero an
/// entire volume, and it would have told the defragmentation planner the same
/// story.</para>
///
/// <para>The threshold is the whole point of the test: thirteen files mapped
/// perfectly and fifteen mapped nothing, so a fixture of a handful of files
/// could never have shown it.</para>
/// </remarks>
[TestFixture]
public class BtrfsExtentMapDepthTests {

  [Test, Category("Regression")]
  [TestCase(14)]
  [TestCase(32)]
  public void FilesAreMappedWhenTheTreeHasMoreThanOneLeaf(int fileCount) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("Btrfs")!;

    // One file taking half the volume and a spread of smaller ones. What splits
    // the tree is how many items its leaf has to hold, so the mix matters as
    // much as the count — a set of equal middling files does not reach it.
    const int totalBytes = 50 * 1024;
    var inputs = new List<ArchiveInputInfo>();

    void Add(string name, int length, int seed) {
      var data = new byte[length];
      for (var j = 0; j < length; ++j) data[j] = (byte)(j * 31 + seed * 7 + (j >> 11));
      inputs.Add(ArchiveInputInfo.InMemory(name, data));
    }

    Add("BIG00001.BIN", totalBytes / 2, 1);
    var perFile = (totalBytes - totalBytes / 2) / fileCount;
    for (var i = 0; i < fileCount; ++i) {
      var length = Math.Max(1, perFile + (i % 7) * 1024 - 3 * 1024);
      if (i % 11 == 0) length = 17 + i;      // stored inline, owns no extent
      Add($"F{i:D4}.BIN", length, i + 2);
    }

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    image.Position = 0;
    var used = ((IFilesystemExtentMap)ops).EnumerateExtents(image)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .ToList();

    Assert.That(used, Is.Not.Empty,
      $"{fileCount} files are on the volume and the map describes none of them");

    // The big file alone is far past the inline threshold, so at the very least
    // the map has to describe that. What it did instead was describe nothing.
    var named = used.Select(e => Path.GetFileName(e.FileName ?? "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert.That(named, Does.Contain("BIG00001.BIN"),
      "the largest file on the volume owns extents and the map does not mention it");
  }
}

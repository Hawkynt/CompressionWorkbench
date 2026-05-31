using System.Text;
using FileSystem.Os9Rbf;

namespace Compression.Tests.Os9Rbf;

/// <summary>
/// An OS-9 RBF directory is an array of 32-byte entries stored in the directory
/// file's data sectors, addressed through the file descriptor's segment list.
/// A directory holding many children spans several sectors; the writer must
/// allocate enough sectors (one contiguous segment is sufficient) and the reader
/// must walk the whole FD.SIZ-bounded directory data. A directory with a thousand
/// entries must therefore round-trip every child.
/// </summary>
[TestFixture]
public class Os9RbfLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void Subdirectory_WithThousandEntries_RoundTripsAllChildren() {
    // 1000 entries far exceeds the 8 entries that fit in a single 256-byte
    // directory sector, forcing the directory across ~126 sectors.
    const int count = 1000;
    var files = new List<(string Name, byte[] Data)>(count);
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < count; i++) {
      var name = $"D/F{i:D4}";
      // Keep payloads empty so the whole tree fits the 1260-sector reference image.
      var data = Array.Empty<byte>();
      files.Add((name, data));
      expected[name] = data;
    }

    var image = Os9RbfWriter.Build(files, "BIGDIR");

    var v = Os9RbfReader.Read(image);

    var byPath = v.Files
      .Where(f => !f.IsDirectory)
      .ToDictionary(f => f.Name, f => Os9RbfReader.Extract(v, f));

    Assert.That(byPath.Count, Is.EqualTo(count), "every child file is present");
    foreach (var path in expected.Keys)
      Assert.That(byPath.ContainsKey(path), Is.True, $"{path} present");
  }

  [Test, Category("RoundTrip")]
  public void Directory_WithManyEntries_PreservesSpotCheckedContent() {
    // Fewer entries but with real payloads to confirm content survives a
    // multi-sector directory layout.
    const int count = 120;
    var files = new List<(string Name, byte[] Data)>(count);
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < count; i++) {
      var name = $"D/F{i:D4}";
      var data = Encoding.ASCII.GetBytes($"payload number {i}");
      files.Add((name, data));
      expected[name] = data;
    }

    var image = Os9RbfWriter.Build(files, "MANYDIR");
    var v = Os9RbfReader.Read(image);

    var byPath = v.Files
      .Where(f => !f.IsDirectory)
      .ToDictionary(f => f.Name, f => Os9RbfReader.Extract(v, f));

    Assert.That(byPath.Count, Is.EqualTo(count), "every child file is present");
    // Spot-check first, middle, last.
    foreach (var i in new[] { 0, count / 2, count - 1 }) {
      var path = $"D/F{i:D4}";
      Assert.That(byPath[path], Is.EqualTo(expected[path]).AsCollection, $"{path} content intact");
    }
  }
}

#pragma warning disable CS1591
using System.Text;

namespace Compression.Tests.Udf;

/// <summary>
/// A single directory holding many files must round-trip. The directory's File
/// Identifier Descriptors no longer fit in one logical block, so the writer
/// must spread them across multiple data blocks (multiple allocation
/// descriptors, FIDs padded so none crosses a block boundary) and the reader
/// must walk every block to recover every entry.
/// </summary>
[TestFixture]
public class UdfLargeDirectoryTests {

  [Test, Category("RoundTrip")]
  public void ManyFilesInOneDirectory_RoundTripThroughReader() {
    const int count = 2000;
    var w = new FileSystem.Udf.UdfWriter();
    for (var i = 0; i < count; i++)
      w.AddFile($"bulk/file_{i:D4}.txt", Encoding.ASCII.GetBytes($"payload-{i}"));

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Udf.UdfReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();

    Assert.That(files.Count, Is.EqualTo(count), "every file in the large directory is recovered");

    // The directory itself must surface.
    Assert.That(r.Entries.Any(e => e.IsDirectory && e.Name == "bulk"), Is.True, "directory present");

    // Spot-check content at a few positions (first, middle, last).
    foreach (var i in new[] { 0, 1, count / 2, count - 2, count - 1 }) {
      var name = $"bulk/file_{i:D4}.txt";
      var entry = files.SingleOrDefault(e => e.Name == name);
      Assert.That(entry, Is.Not.Null, $"{name} present");
      Assert.That(r.Extract(entry!), Is.EqualTo(Encoding.ASCII.GetBytes($"payload-{i}")),
        $"{name} content intact");
    }
  }
}

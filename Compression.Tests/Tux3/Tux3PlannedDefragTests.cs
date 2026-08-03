#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Tux3;

namespace Compression.Tests.Tux3;

/// <summary>
/// Tux3 lays a container out again by moving whole records, and on one this
/// writer produced the answer is usually that nothing is out of place.
/// </summary>
/// <remarks>
/// A record's data sits behind the header naming it at an offset nothing
/// records — the reader finds the next record by adding this one's length to a
/// cursor — so nothing has to be repointed, and the only layout the walk
/// reaches is one with the records in some order and nothing between them.
/// Removing a file writes the container out packed, so a pass over ours finds
/// no gap; what it is for is a container that arrived from somewhere else.
/// </remarks>
[TestFixture]
public class Tux3PlannedDefragTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  private static MemoryStream Volume(out Dictionary<string, byte[]> files) {
    var work = Path.Combine(Path.GetTempPath(), "cwb_Tux3_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    try {
      var inputs = new List<ArchiveInputInfo>();
      for (var k = 0; k < 5; ++k) {
        var data = Payload(k, 3000 + k * 1500);
        var path = Path.Combine(work, $"F{k}.BIN");
        File.WriteAllBytes(path, data);
        inputs.Add(new ArchiveInputInfo(path, $"F{k}.BIN", false));
        files[$"F{k}.BIN"] = data;
      }

      var image = new MemoryStream();
      new Tux3FormatDescriptor().Create(image, inputs, new FormatCreateOptions());
      return image;
    } finally {
      try { Directory.Delete(work, true); } catch { /* scratch is gone already */ }
    }
  }

  private static Dictionary<string, byte[]> ReadBack(MemoryStream image) {
    image.Position = 0;
    using var reader = new Tux3Reader(image);
    return reader.Entries
      .Where(e => e.Size > 0 && !e.Name.StartsWith("FULL.", StringComparison.Ordinal)
                  && e.Name != "metadata.ini")
      .ToDictionary(e => e.Name, reader.Extract, StringComparer.Ordinal);
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  public void Defragment_KeepsEveryPayloadAndTheContainersSize(DefragMode mode) {
    using var image = Volume(out var files);
    var size = image.Length;

    image.Position = 0;
    new Tux3FormatDescriptor().Defragment(image, new DefragOptions { Mode = mode });
    Assert.That(image.Length, Is.EqualTo(size), "a container keeps its size");

    var read = ReadBack(image);
    foreach (var (name, data) in files) {
      Assert.That(read.Keys, Does.Contain(name), $"{name} must still be in the container");
      Assert.That(read[name], Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void Defragment_FindsNothingToMoveOnOneOfOurOwn() {
    using var image = Volume(out _);
    var before = image.ToArray();

    image.Position = 0;
    new Tux3FormatDescriptor().Defragment(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    // The writer packs its records, so front-packing has nothing to do — and
    // doing nothing must mean writing nothing.
    Assert.That(image.ToArray(), Is.EqualTo(before),
      "a container already packed must come back byte for byte");
  }

  [Test]
  public void Defragment_LeavesTheRecordsWithNothingBetweenThem() {
    using var image = Volume(out _);
    image.Position = 0;
    new Tux3FormatDescriptor().Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var records = Tux3RecordMap.Enumerate(image)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .OrderBy(e => e.Offset)
      .ToList();
    Assert.That(records, Is.Not.Empty, "the container must still describe its records");

    for (var i = 1; i < records.Count; ++i)
      Assert.That(records[i].Offset, Is.EqualTo(records[i - 1].Offset + records[i - 1].Length),
        "a gap between records is what the walk cannot get past");
  }
}

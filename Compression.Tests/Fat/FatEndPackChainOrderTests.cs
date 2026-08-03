#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// End-packing moves runs backwards over ground they already occupy, and the
/// clusters have to come back in the order the chain is in.
/// </summary>
/// <remarks>
/// The in-place pass works out where each original cluster ended up by
/// simulating the moves it just made. That simulation wrote each destination
/// slot as it went, so when a run shifted by less than its own length — source
/// and destination overlapping — a later slot read the origin an earlier one
/// had just put there. The owner was relinked with its clusters in the wrong
/// order: every byte still on the volume, the file reading as noise. Nothing
/// threw, and this path keeps what it produces, so the volume was simply
/// wrong afterwards.
/// </remarks>
[TestFixture]
public class FatEndPackChainOrderTests {

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  /// <summary>Six files of rising size, which is what makes the runs overlap.</summary>
  private static MemoryStream Volume(out IReadOnlyList<(string Name, byte[] Data)> files) {
    var built = new List<(string Name, byte[] Data)>();
    var writer = new FatWriter();
    for (var k = 0; k < 6; ++k) {
      var data = Payload(k, 8 * 1024 + k * 1024);
      writer.AddFile($"F{k}.BIN", data);
      built.Add(($"F{k}.BIN", data));
    }

    var image = new MemoryStream();
    var bytes = writer.BuildAutoSized();
    image.Write(bytes, 0, bytes.Length);
    files = built;
    return image;
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void DefragmentInPlace_KeepsEveryPayload(DefragMode mode) {
    using var image = Volume(out var files);
    image.Position = 0;
    new FatFormatDescriptor().DefragmentInPlace(image, new DefragOptions { Mode = mode });

    image.Position = 0;
    var reader = new FatReader(image);
    foreach (var (name, data) in files) {
      var entry = reader.Entries.FirstOrDefault(
        e => !e.IsDirectory && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{name} must still be in the directory");
      Assert.That(reader.Extract(entry!), Is.EqualTo(data), $"{name} must read back byte for byte");
    }
  }

  [Test]
  public void EndPacking_LeavesEachFileInOnePiece() {
    using var image = Volume(out var files);
    image.Position = 0;
    new FatFormatDescriptor().DefragmentInPlace(image,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });

    image.Position = 0;
    var runs = FatExtentMap.Enumerate(image)
      .Where(e => e.Kind == DefragBlockKind.Used)
      .GroupBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(g => g.Key!, g => g.Count(), StringComparer.OrdinalIgnoreCase);

    foreach (var (name, _) in files)
      Assert.That(runs.GetValueOrDefault(name), Is.EqualTo(1),
        $"{name} was packed against the tail and should be in one piece");
  }
}

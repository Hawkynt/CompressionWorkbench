#pragma warning disable CS1591
using Compression.Core.Layout;
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// The placement round trip, across every filesystem that offers the verb:
/// build, scatter, put one named file at one chosen offset, read everything
/// back, defragment, read it back again.
/// </summary>
/// <remarks>
/// The assertions that matter are the middle three. A file that lands at the
/// offset it was asked for is the promise; every other file coming back
/// byte-identical is what makes the eviction trustworthy; and the placed file
/// reading forwards is the property that survives when the volume forces a
/// split.
/// </remarks>
[TestFixture]
public class FilesystemPlacementTests {

  private const int Seed = 4711;
  private const string Placed = "BIG00001.BIN";

  private static List<ArchiveInputInfo> Payloads(Dictionary<string, byte[]> expected) {
    var inputs = new List<ArchiveInputInfo>();

    void Add(string name, int length, int seed) {
      var data = new byte[length];
      // The byte at index j carries j and j >> 11, so a block that lands in the
      // wrong place differs from the one that belongs there and says which.
      for (var j = 0; j < length; ++j) data[j] = (byte)(j * 31 + seed * 7 + (j >> 11));
      expected[name] = data;
      inputs.Add(ArchiveInputInfo.InMemory(name, data));
    }

    Add(Placed, 24 * 1024, 1);
    for (var i = 0; i < 6; ++i) Add($"F{i:D4}.BIN", 3 * 1024 + i * 1024, i + 2);
    return inputs;
  }

  private static List<DefragBlockInfo> Layout(IArchiveFormatOperations ops, MemoryStream image) {
    image.Position = 0;
    return ((IFilesystemExtentMap)ops).EnumerateExtents(image).ToList();
  }

  /// <summary>
  /// One allocation block, read off the volume rather than assumed: a scattered
  /// owner is one run per block, so the shortest run is the block size.
  /// </summary>
  private static int ClusterOf(IReadOnlyList<DefragBlockInfo> layout)
    => (int)layout.Where(e => e.Kind == DefragBlockKind.Used && e.Length > 0).Min(e => e.Length);

  private static void AssertContents(IArchiveFormatOperations ops, MemoryStream image,
      Dictionary<string, byte[]> expected, string what) {
    var outDir = Path.Combine(Path.GetTempPath(), "place_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ops.Extract(image, outDir, null, null);
      foreach (var (name, want) in expected) {
        var path = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        Assert.That(path, Is.Not.Null, $"{what}: '{name}' is missing");
        Assert.That(File.ReadAllBytes(path!), Is.EqualTo(want),
          $"{what}: '{name}' does not read back byte-identical");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { }
    }
  }

  [Test, Category("RoundTrip")]
  [TestCase("Fat")]
  [TestCase("Fatx")]
  [TestCase("Ext1")]
  public void AFileGoesWhereItIsAsked_AndEverythingElseSurvivesTheEviction(string formatId) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    Assert.That(ops, Is.InstanceOf<IFilesystemPlaceable>(),
      $"{formatId} does not offer the placement verb.");

    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, Payloads(expected), new FormatCreateOptions());

    image.Position = 0;
    ((IFilesystemScrambleable)ops).Scramble(image, new ScrambleOptions { Seed = Seed });
    AssertContents(ops, image, expected, $"{formatId} after the scramble");

    var scattered = Layout(ops, image);
    var cluster = ClusterOf(scattered);
    var before = AscendingBlockOrder.Read(scattered, Placed, cluster);
    TestContext.Out.WriteLine($"{formatId}: cluster {cluster}, scattered {before}");

    // The lowest live cluster: a real boundary inside the data area, and
    // occupied, so the placement has to evict something to get there.
    var target = scattered.Where(e => e.Kind == DefragBlockKind.Used).Min(e => e.Offset);

    image.Position = 0;
    ((IFilesystemPlaceable)ops).PlaceFileAt(image,
      new PlacementOptions { FileName = Placed, TargetOffset = target });

    var placedLayout = Layout(ops, image);
    var placed = placedLayout.Where(e => e.Kind == DefragBlockKind.Used
      && string.Equals(e.FileName, Placed, StringComparison.OrdinalIgnoreCase)).ToList();
    Assert.That(placed, Is.Not.Empty, $"{formatId}: '{Placed}' vanished from the extent map.");
    Assert.That(placed[0].Offset, Is.EqualTo(target),
      $"{formatId}: '{Placed}' does not start where it was asked to.");

    var after = AscendingBlockOrder.Read(placedLayout, Placed, cluster);
    TestContext.Out.WriteLine($"{formatId}: placed at {target:N0} — {after}");
    Assert.That(after.Ascends, Is.True, $"{formatId}: {after}");

    AssertContents(ops, image, expected, $"{formatId} after the placement");

    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    AssertContents(ops, image, expected, $"{formatId} after the defragmentation");

    var tidied = Layout(ops, image);
    foreach (var reading in AscendingBlockOrder.ReadAll(tidied, ClusterOf(tidied))) {
      TestContext.Out.WriteLine($"{formatId}: defragmented — {reading}");
      Assert.That(reading.Ascends, Is.True, $"{formatId}: {reading}");
    }
  }

  /// <summary>
  /// The weaker goal against a real volume: every owner comes out reading
  /// forwards and every file comes back byte-identical, having moved far less
  /// than a packing pass would.
  /// </summary>
  [Test, Category("RoundTrip")]
  [TestCase("Fat")]
  [TestCase("Fatx")]
  [TestCase("Ext1")]
  public void TheAscendingModeLeavesEveryOwnerReadingForwards(string formatId) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(formatId)!;

    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, Payloads(expected), new FormatCreateOptions());

    image.Position = 0;
    ((IFilesystemScrambleable)ops).Scramble(image, new ScrambleOptions { Seed = Seed });

    var scattered = Layout(ops, image);
    var cluster = ClusterOf(scattered);
    var backwards = AscendingBlockOrder.Violations(scattered, cluster);
    Assert.That(backwards, Is.Not.Empty,
      $"{formatId}: the scattered volume already read forwards, so this proves nothing.");
    TestContext.Out.WriteLine($"{formatId}: {backwards.Count} owner(s) read backwards after the scramble");

    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions { Mode = DefragMode.AscendingOrder });

    AssertContents(ops, image, expected, $"{formatId} after the ascending pass");

    var after = Layout(ops, image);
    foreach (var reading in AscendingBlockOrder.ReadAll(after, ClusterOf(after)))
      Assert.That(reading.Ascends, Is.True, $"{formatId}: {reading}");
    TestContext.Out.WriteLine(
      $"{formatId}: {LayoutSimulation.TotalRuns(scattered)} run(s) -> {LayoutSimulation.TotalRuns(after)}");
  }

  [Test, Category("ErrorHandling")]
  [TestCase("Fat")]
  [TestCase("Fatx")]
  [TestCase("Ext1")]
  public void ARequestTheVolumeCannotHonour_LeavesItExactlyAsItWas(string formatId) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(formatId)!;

    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, Payloads(expected), new FormatCreateOptions());

    var untouched = image.ToArray();

    var thrown = Assert.Throws<InvalidOperationException>(() =>
      ((IFilesystemPlaceable)ops).PlaceFileAt(image,
        new PlacementOptions { FileName = "NOSUCH.BIN", TargetOffset = 0 }));
    TestContext.Out.WriteLine($"{formatId}: {thrown!.Message}");

    Assert.That(image.ToArray(), Is.EqualTo(untouched),
      $"{formatId}: a refused placement wrote to the image anyway.");
  }
}

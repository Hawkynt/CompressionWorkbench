#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Layout;

/// <summary>
/// The scramble round trip, across every filesystem that offers the verb: build,
/// scatter, read back, defragment, read back again.
/// </summary>
/// <remarks>
/// The assertion that matters is the middle one. A defragmentation of a volume
/// that was never fragmented proves nothing, and until scramble existed that was
/// the only kind of volume a fixture could produce.
/// </remarks>
[TestFixture]
public class FilesystemScrambleTests {

  private const int Seed = 4711;

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

    Add("BIG00001.BIN", 24 * 1024, 1);
    for (var i = 0; i < 6; ++i) Add($"F{i:D4}.BIN", 3 * 1024 + i * 1024, i + 2);
    return inputs;
  }

  /// <summary>
  /// Runs per owner: how many separate stretches of the volume each owner's
  /// allocation breaks into.
  /// </summary>
  /// <remarks>
  /// Counting the extents the map yields would not do. Some maps merge an
  /// owner's consecutive blocks into one entry and some emit one entry per
  /// block, so the raw count measures the map rather than the layout — FATX
  /// reports the same figure for a contiguous file and a scattered one.
  /// Stretches that end where the next begins are one run either way.
  /// </remarks>
  private static Dictionary<string, int> RunsPerOwner(IArchiveFormatOperations ops, MemoryStream image) {
    image.Position = 0;
    var runs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var endOf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var extent in ((IFilesystemExtentMap)ops).EnumerateExtents(image)) {
      if (extent.Kind != DefragBlockKind.Used) continue;
      var owner = extent.FileName ?? "<unknown>";
      if (!endOf.TryGetValue(owner, out var previousEnd) || previousEnd != extent.Offset) {
        runs.TryGetValue(owner, out var count);
        runs[owner] = count + 1;
      }
      endOf[owner] = extent.Offset + extent.Length;
    }
    return runs;
  }

  private static void AssertContents(IArchiveFormatOperations ops, MemoryStream image,
      Dictionary<string, byte[]> expected, string what) {
    var outDir = Path.Combine(Path.GetTempPath(), "scramble_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(outDir);
    try {
      image.Position = 0;
      ops.Extract(image, outDir, null, null);
      foreach (var (name, want) in expected) {
        var path = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
          .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        Assert.That(path, Is.Not.Null, $"{what}: '{name}' is missing");
        Assert.That(File.ReadAllBytes(path!), Is.EqualTo(want), $"{what}: '{name}' does not read back byte-identical");
      }
    } finally {
      try { Directory.Delete(outDir, true); } catch { }
    }
  }

  [Test, Category("RoundTrip")]
  [TestCase("Fat")]
  [TestCase("Fatx")]
  [TestCase("Ext1")]
  public void AVolumeSurvivesBeingScattered_AndComesBackContiguous(string formatId) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    Assert.That(ops, Is.InstanceOf<IFilesystemScrambleable>(),
      $"{formatId} no longer offers the scramble verb.");

    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, Payloads(expected), new FormatCreateOptions());

    var before = RunsPerOwner(ops, image);
    TestContext.Out.WriteLine($"{formatId}: {before.Values.Sum()} run(s) over {before.Count} owner(s) before");

    image.Position = 0;
    ((IFilesystemScrambleable)ops).Scramble(image, new ScrambleOptions { Seed = Seed });

    var scattered = RunsPerOwner(ops, image);
    TestContext.Out.WriteLine($"{formatId}: {scattered.Values.Sum()} run(s) after scramble");
    foreach (var (owner, count) in scattered.OrderBy(kv => kv.Key, StringComparer.Ordinal))
      TestContext.Out.WriteLine($"    {owner,-24} {before.GetValueOrDefault(owner)} -> {count}");
    Assert.That(scattered.Values.Sum(), Is.GreaterThan(before.Values.Sum() * 4),
      $"{formatId}: the volume came out of the scramble barely fragmented.");
    AssertContents(ops, image, expected, $"{formatId} after the scramble");

    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    var tidied = RunsPerOwner(ops, image);
    TestContext.Out.WriteLine($"{formatId}: {tidied.Values.Sum()} run(s) after defragment");
    AssertContents(ops, image, expected, $"{formatId} after the defragmentation");
    foreach (var (owner, count) in tidied)
      Assert.That(count, Is.LessThanOrEqualTo(before.GetValueOrDefault(owner, 1)),
        $"{formatId}: '{owner}' is still in {count} pieces after a defragmentation.");
  }
}

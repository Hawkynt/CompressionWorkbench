using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// In-place edits of feature-flag (version 5000) pools: the ones OpenZFS 2.2.2 made
/// (<c>ReferenceVectors</c>: log space maps, spacemap_v2, histograms, userobj and project
/// accounting, zstd datasets, ashift 12, mirror and RAID-Z) and the ones the writer makes. The reader
/// and the accounting probe check every edit here; OpenZFS checks them in the external tests —
/// <c>zdb -bcc</c>, <c>zdb -d</c> (feature and space-map counts, MOS leaks) and the kernel importing,
/// scrubbing and serving exactly the edited file set.
/// </summary>
[TestFixture]
public sealed class ZfsFeatureEditTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  /// <summary>An edit: the pool, its device image names, what is added and what is removed.</summary>
  public sealed record Scenario(string Pool, string[] Devices, (string Path, byte[] Data)[] Adds, string[] Removes) {
    public override string ToString() => this.Pool;
  }

  private static byte[] Text(string seed, int lines) =>
    Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, lines).Select(i => $"{seed} {i % 37}\n")));

  private static (string, byte[])[] CommonAdds(string prefix = "") => [
    (prefix + "added/new.txt", Text("new", 30)), (prefix + "added/deeper/lines.txt", Text("lines", 40000)),
    (prefix + "added/random.bin", ZfsWriterTests.Pattern(300_000, 29)), (prefix + "tiny.txt", "replaced tiny\n"u8.ToArray()),
  ];

  private static readonly string[] CommonRemoves = ["text.txt", "rand.bin", "sparse.bin", "dir"];

  private static IEnumerable<TestCaseData> OpenZfsPools() {
    yield return new TestCaseData(new Scenario("cwbmirror", ["cwbmirror-0", "cwbmirror-1"], CommonAdds(), CommonRemoves));
    yield return new TestCaseData(new Scenario("cwbraidz1", ["cwbraidz1-0", "cwbraidz1-1", "cwbraidz1-2"], CommonAdds(), CommonRemoves));
    yield return new TestCaseData(new Scenario("cwbraidz2", ["cwbraidz2-0", "cwbraidz2-1", "cwbraidz2-2", "cwbraidz2-3"], CommonAdds(), CommonRemoves));
    yield return new TestCaseData(new Scenario("cwbgang", ["cwbgang"], CommonAdds(), ["empty.txt", "dir", "tiny.txt"]));
    yield return new TestCaseData(new Scenario("cwbmain", ["cwbmain"],
      [.. CommonAdds("c-zstd-19/"), .. CommonAdds("c-gzip-9/"), ("root-file.txt", Text("root", 100))],
      ["c-zstd-19/rand.bin", "c-zstd-19/dir", "c-gzip-9/text.txt", "c-off/sparse.bin"]));
    yield return new TestCaseData(new Scenario("cwbenc", ["cwbenc"], [("more.txt", Text("more", 500))], ["plain.txt"]));
  }

  private static MemoryStream[] Devices(Scenario s) => s.Devices.Select(ZfsReferencePools.Copy).ToArray();

  /// <summary>The files the pool must hold after the edit: the reference manifest, minus what was removed, plus what was added.</summary>
  private static Dictionary<string, string> Expected(Scenario s) {
    // snapshot contents (.zfs/snapshot/...) are not part of the edit, nor listed by a mount's find
    var expected = ZfsReferencePools.Manifest(s.Pool).Where(m => m.Kind == 'F' && !m.Path.Contains(".zfs/", StringComparison.Ordinal)).ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal);
    foreach (var gone in s.Removes)
      foreach (var path in expected.Keys.Where(p => p == gone || p.StartsWith(gone + "/", StringComparison.Ordinal)).ToList())
        expected.Remove(path);
    foreach (var (path, data) in s.Adds) expected[path] = ZfsOracle.Sha(data);
    return expected;
  }

  /// <summary>Removes then adds, through all devices; returns the edited images.</summary>
  private static byte[][] Apply(Scenario s) {
    var devices = Devices(s);
    Descriptor.RemoveFromPool(devices, s.Removes);
    Descriptor.AddToPool(devices, s.Adds.Select(a => ArchiveInputInfo.InMemory(a.Path, a.Data)).ToList());
    return devices.Select(d => d.ToArray()).ToArray();
  }

  private static void AssertReads(byte[][] images, Dictionary<string, string> expected, ulong txg) {
    using var reader = new ZfsReader(images.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList());
    Assert.That(reader.Txg, Is.EqualTo(txg));
    var files = reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null && !e.Name.Contains(".zfs/", StringComparison.Ordinal)
      && reader.Datasets.Any(d => d.Refusal == null && d.Name == e.Dataset)).ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    Assert.Multiple(() => {
      foreach (var (name, sha) in expected)
        if (files.TryGetValue(name, out var entry))
          Assert.That(ZfsOracle.Sha(reader.Extract(entry)), Is.EqualTo(sha), name);
    });
  }

  private static ulong TxgOf(Scenario s) {
    using var reader = new ZfsReader(Devices(s).Cast<Stream>().ToList());
    return reader.Txg;
  }

  [TestCaseSource(nameof(OpenZfsPools))]
  public void Given_AFeaturePoolOpenZfsMade_When_Edited_Then_TheReaderSeesTheEditAndTheAccountingMovesWithTheBlocks(Scenario s) {
    var before = s.Devices.Select(ZfsReferencePools.ImageBytes).ToArray();
    var images = Apply(s);
    AssertReads(images, Expected(s), TxgOf(s) + 2);
    ZfsAccountingProbe.AssertMovedTogether(ZfsAccountingProbe.Probe(before), ZfsAccountingProbe.Probe(images));
  }

  // ── pools the writer made ─────────────────────────────────────────────────────────────

  private static IEnumerable<TestCaseData> WrittenPools() {
    yield return new TestCaseData("lz4", "mirror", 1);
    yield return new TestCaseData("zstd", "mirror", 2);
    yield return new TestCaseData("lz4", "raidz1", 3);
  }

  private static (byte[][] Images, Dictionary<string, string> Expected, byte[][] Before) EditWritten(string compression, string layout, int count) {
    var files = ZfsWriterTests.Corpus(large: false);
    var outputs = Enumerable.Range(0, count).Select(_ => new MemoryStream()).ToArray();
    Descriptor.CreatePool(outputs, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions { FormatSpecific = { ["PoolName"] = "cwbx", ["Version"] = "5000", ["Compression"] = compression, ["Layout"] = layout } });
    var before = outputs.Select(o => o.ToArray()).ToArray();
    var adds = CommonAdds();
    Descriptor.RemoveFromPool(outputs, ["dir", "multi.bin"]);
    Descriptor.AddToPool(outputs, adds.Select(a => ArchiveInputInfo.InMemory(a.Item1, a.Item2)).ToList());
    files.Remove("dir/sub/deep.txt");
    files.Remove("multi.bin");
    foreach (var (path, data) in adds) files[path] = data;
    return (outputs.Select(o => o.ToArray()).ToArray(), files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal), before);
  }

  [TestCaseSource(nameof(WrittenPools))]
  public void Given_AWrittenFeaturePool_When_Edited_Then_ItReadsBackAndAccountsForItsBlocks(string compression, string layout, int count) {
    var (images, expected, before) = EditWritten(compression, layout, count);
    AssertReads(images, expected, 6);
    var end = ZfsAccountingProbe.Probe(images);
    Assert.Multiple(() => {
      foreach (var (key, ledger) in end)
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: recorded space");
    });
    ZfsAccountingProbe.AssertMovedTogether(ZfsAccountingProbe.Probe(before), end);
  }

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  private static (string Dir, string Wsl) Stage(string pool, byte[][] images) =>
    ZfsOracle.Stage(images.Select((d, i) => ($"{pool}-{i}.img", d)).ToList());

  [TestCaseSource(nameof(OpenZfsPools)), Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedOpenZfsFeaturePool_When_ZdbTraversesIt_Then_NothingLeaksAndEveryCountHolds(Scenario s) {
    ZfsOracle.RequireZdb();
    var (dir, wsl) = Stage(s.Pool, Apply(s));
    try {
      ZfsOracle.AssertNoLeaks(wsl, s.Pool);
      ZfsOracle.AssertFeatureCounts(wsl, s.Pool);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(OpenZfsPools)), Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedOpenZfsFeaturePool_When_TheKernelImportsAndScrubsIt_Then_ItServesExactlyTheEdit(Scenario s) {
    ZfsOracle.RequireAppliance();
    var (dir, wsl) = Stage(s.Pool, Apply(s));
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, s.Pool), Is.EquivalentTo(Expected(s)));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(WrittenPools)), Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedWrittenFeaturePool_When_OpenZfsChecksIt_Then_ItIsClean(string compression, string layout, int count) {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (images, expected, _) = EditWritten(compression, layout, count);
    var (dir, wsl) = Stage("cwbx", images);
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbx");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbx");
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbx"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}

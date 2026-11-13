using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Feature-flag pools (version 5000) with enabled_txg, hole_birth, embedded_data, lz4_compress,
/// spacemap_histogram and empty_bpobj: small blocks embedded in their pointers, lz4 blocks, space
/// maps with histograms. The reader and the accounting probe check them here; OpenZFS checks them in
/// the external tests — <c>zdb -bcc</c>, <c>zdb -d</c> (feature reference counts, space-map count,
/// MOS leaks), and the kernel importing, scrubbing and serving every file and reporting each
/// feature active or enabled as written.
/// </summary>
[TestFixture]
public sealed class ZfsFeatureWriteTests {
  private static IEnumerable<TestCaseData> Cases() {
    yield return new TestCaseData("lz4", "single", 1);
    yield return new TestCaseData("gzip", "single", 1);
    yield return new TestCaseData("off", "single", 1);
    yield return new TestCaseData("on", "mirror", 2);
    yield return new TestCaseData("lz4", "raidz1", 3);
    yield return new TestCaseData("zstd", "single", 1);
    yield return new TestCaseData("zstd-19", "mirror", 2);
  }

  private static Dictionary<string, byte[]> Corpus() {
    var files = ZfsWriterTests.Corpus(large: false);
    files["tiny-text.txt"] = Encoding.ASCII.GetBytes(new string('z', 4000));                       // compresses into a pointer
    files["lines.txt"] = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 30000).Select(i => $"line {i % 41}\n")));
    files["zeros.bin"] = new byte[300 * 1024];
    return files;
  }

  private static (byte[][] Devices, Dictionary<string, string> Expected) Write(string compression, string layout, int devices) {
    var files = Corpus();
    var outputs = Enumerable.Range(0, devices).Select(_ => new MemoryStream()).ToArray();
    new ZfsFormatDescriptor().CreatePool(outputs, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions { FormatSpecific = {
        ["PoolName"] = "cwbf", ["Version"] = "5000", ["Compression"] = compression, ["Layout"] = layout == "single" ? "mirror" : layout,
      } });
    return (outputs.Select(o => o.ToArray()).ToArray(), files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal));
  }

  [TestCaseSource(nameof(Cases))]
  public void Given_AFeaturePool_When_Written_Then_ItReadsBackAndAccountsForItsBlocks(string compression, string layout, int devices) {
    var (images, expected) = Write(compression, layout, devices);
    using var reader = new ZfsReader(images.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList());
    Assert.That(reader.PoolVersion, Is.EqualTo(5000UL));
    Assert.That(reader.Warnings, Is.Empty);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (name, sha) in expected)
      Assert.That(ZfsOracle.Sha(reader.Extract(files[name])), Is.EqualTo(sha), name);
    if (compression != "off")
      Assert.That(reader.Statistics.EmbeddedBlocks, Is.GreaterThan(0), "small compressed blocks are embedded");
    if (compression is "lz4" or "on")
      Assert.That(reader.Statistics.Compressions, Does.Contain(ZfsConstants.CompressLz4));
    if (compression.StartsWith("zstd", StringComparison.Ordinal))
      Assert.That(reader.Statistics.Compressions, Does.Contain(ZfsConstants.CompressZstd));
    Assert.Multiple(() => {
      foreach (var (key, ledger) in ZfsAccountingProbe.Probe(images))
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: recorded space");
    });
  }

  [Test]
  public void Given_Lz4OnAVersion28Pool_When_Written_Then_ItIsRefused() =>
    Assert.Throws<NotSupportedException>(() => new ZfsFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])],
      new FormatCreateOptions { FormatSpecific = { ["Compression"] = "lz4" } }));

  [Test]
  public void Given_AnUnknownVersion_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => new ZfsFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])],
      new FormatCreateOptions { FormatSpecific = { ["Version"] = "29" } }));

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  private static (string Dir, string Wsl) Stage(byte[][] devices) =>
    ZfsOracle.Stage(devices.Select((d, i) => ($"cwbf-{i}.img", d)).ToList());

  [TestCaseSource(nameof(Cases)), Category("Slow"), Category("ExternalTool")]
  public void Given_AFeaturePool_When_ZdbTraversesIt_Then_NothingLeaksAndEveryFeatureCountHolds(string compression, string layout, int devices) {
    ZfsOracle.RequireZdb();
    var (images, expected) = Write(compression, layout, devices);
    var (dir, wsl) = Stage(images);
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbf");
      ZfsOracle.AssertFeatureCounts(wsl, "cwbf");
      foreach (var name in new[] { "tiny-text.txt", "lines.txt", "multi.bin", "hello.txt" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbf", name, expected[name]);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(Cases)), Category("Slow"), Category("ExternalTool")]
  public void Given_AFeaturePool_When_TheKernelImportsAndScrubsIt_Then_ItServesEveryFileWithItsFeatures(string compression, string layout, int devices) {
    ZfsOracle.RequireAppliance();
    var (images, expected) = Write(compression, layout, devices);
    var (dir, wsl) = Stage(images);
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbf", out var log), Is.EquivalentTo(expected));
      var features = log.Split('\n').Where(l => l.StartsWith("FEATURE ", StringComparison.Ordinal))
        .Select(l => l.Split(' ')).ToDictionary(p => p[1], p => p[2].Trim(), StringComparer.Ordinal);
      Assert.Multiple(() => {
        foreach (var active in new[] { "hole_birth", "embedded_data", "lz4_compress", "spacemap_histogram", "enabled_txg" })
          Assert.That(features.GetValueOrDefault(active), Is.EqualTo("active"), active);
        Assert.That(features.GetValueOrDefault("empty_bpobj"), Is.EqualTo("enabled").Or.EqualTo("active"), "empty_bpobj");
        var zstd = compression.StartsWith("zstd", StringComparison.Ordinal) ? "active" : "enabled";
        Assert.That(features.GetValueOrDefault("zstd_compress"), Is.EqualTo(zstd), "zstd_compress");
        Assert.That(features.GetValueOrDefault("extensible_dataset"), Is.EqualTo(zstd), "extensible_dataset");
      });
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}

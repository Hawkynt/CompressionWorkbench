using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// RAID-Z: pools written with single, double and triple parity. The reader reads them (and a
/// single-parity pool with any one device missing); the accounting probe checks the recorded
/// space, RAID-Z's deflate ratio included. OpenZFS checks them in the external tests —
/// <c>zdb -bcc</c> with every device, and the kernel imports, scrubs and serves every file both
/// whole and with as many devices missing as the parity allows.
/// </summary>
[TestFixture]
public sealed class ZfsRaidzWriteTests {
  private static IEnumerable<TestCaseData> Layouts() {
    yield return new TestCaseData(1, 3);
    yield return new TestCaseData(1, 5);
    yield return new TestCaseData(2, 4);
    yield return new TestCaseData(2, 6);
    yield return new TestCaseData(3, 5);
  }

  private static (byte[][] Devices, Dictionary<string, string> Expected) Write(int parity, int devices) {
    var files = ZfsWriterTests.Corpus(large: false);
    files["big.bin"] = ZfsWriterTests.Pattern(1_500_000, 17);            // blocks past the 1 MiB parity swap
    var outputs = Enumerable.Range(0, devices).Select(_ => new MemoryStream()).ToArray();
    new ZfsFormatDescriptor().CreatePool(outputs, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions { FormatSpecific = { ["PoolName"] = "cwbz", ["Layout"] = $"raidz{parity}", ["Compression"] = "gzip" } });
    return (outputs.Select(o => o.ToArray()).ToArray(), files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal));
  }

  private static void AssertReads(IReadOnlyList<byte[]> devices, Dictionary<string, string> expected, string what) {
    using var reader = new ZfsReader(devices.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList());
    Assert.That(reader.Warnings, Is.Empty, what);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys), what);
    foreach (var (name, sha) in expected)
      Assert.That(ZfsOracle.Sha(reader.Extract(files[name])), Is.EqualTo(sha), $"{what}: {name}");
  }

  [TestCaseSource(nameof(Layouts))]
  public void Given_ARaidz_When_Written_Then_ItReadsBackAndAccountsForItsBlocks(int parity, int devices) {
    var (images, expected) = Write(parity, devices);
    AssertReads(images, expected, "all devices");
    using (var reader = new ZfsReader(images.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList()))
      Assert.That(reader.Topology.Single(), Does.StartWith($"raidz{parity}("));
    if (parity == 1)
      for (var missing = 0; missing < devices; ++missing)
        AssertReads(images.Where((_, i) => i != missing).ToList(), expected, $"device {missing} missing");
    Assert.Multiple(() => {
      foreach (var (key, ledger) in ZfsAccountingProbe.Probe(images))
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: recorded space");
    });
  }

  [Test]
  public void Given_TooFewDevices_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => Write(2, 3));

  [Test]
  public void Given_AnUnknownLayout_When_Written_Then_ItIsRefused() =>
    Assert.Throws<ArgumentException>(() => new ZfsFormatDescriptor().CreatePool([new MemoryStream(), new MemoryStream()], [],
      new FormatCreateOptions { FormatSpecific = { ["Layout"] = "raidz4" } }));

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  private static (string Dir, string Wsl) Stage(byte[][] devices, int skip = 0) =>
    ZfsOracle.Stage(devices.Select((d, i) => ($"cwbz-{i}.img", d)).Skip(skip).ToList());

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool")]
  public void Given_ARaidz_When_ZdbTraversesIt_Then_NothingLeaksAndFilesCopyOut(int parity, int devices) {
    ZfsOracle.RequireZdb();
    var (images, expected) = Write(parity, devices);
    var (dir, wsl) = Stage(images);
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbz");
      foreach (var name in new[] { "big.bin", "multi.bin", "hello.txt" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbz", name, expected[name]);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool")]
  public void Given_ARaidz_When_TheKernelImportsAndScrubsIt_Then_ItServesEveryFile(int parity, int devices) {
    ZfsOracle.RequireAppliance();
    var (images, expected) = Write(parity, devices);
    var (dir, wsl) = Stage(images);
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbz"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool")]
  public void Given_ARaidzMissingAsManyDevicesAsItsParity_When_TheKernelImportsIt_Then_ItServesEveryFileDegraded(int parity, int devices) {
    ZfsOracle.RequireAppliance();
    var (images, expected) = Write(parity, devices);
    var (dir, wsl) = Stage(images, skip: parity);
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbz"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}

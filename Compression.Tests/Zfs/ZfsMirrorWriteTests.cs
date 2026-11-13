using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Mirrors: pools written over two and three devices, each holding every block. The reader
/// reads them with every device and with any one alone; OpenZFS checks them in the external
/// tests — <c>zdb -bcc</c> with all devices, and the kernel imports, scrubs and serves the files
/// both whole and with only one device present (degraded).
/// </summary>
[TestFixture]
public sealed class ZfsMirrorWriteTests {
  private static (byte[][] Devices, Dictionary<string, string> Expected) Write(int ways) {
    var files = ZfsWriterTests.Corpus(large: false);
    var outputs = Enumerable.Range(0, ways).Select(_ => new MemoryStream()).ToArray();
    new ZfsFormatDescriptor().CreatePool(outputs, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions { FormatSpecific = { ["PoolName"] = "cwbm", ["Compression"] = "lzjb" } });
    return (outputs.Select(o => o.ToArray()).ToArray(), files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal));
  }

  private static void AssertReads(IReadOnlyList<byte[]> devices, Dictionary<string, string> expected, string what) {
    using var reader = new ZfsReader(devices.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList());
    Assert.That(reader.Warnings, Is.Empty, what);
    Assert.That(reader.Topology.Single(), Does.StartWith("mirror("), what);
    var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys), what);
    foreach (var (name, sha) in expected)
      Assert.That(ZfsOracle.Sha(reader.Extract(files[name])), Is.EqualTo(sha), $"{what}: {name}");
  }

  [TestCase(2)]
  [TestCase(3)]
  public void Given_AMirror_When_Written_Then_EveryDeviceAloneAndAllTogetherReadBack(int ways) {
    var (devices, expected) = Write(ways);
    AssertReads(devices, expected, "all devices");
    for (var d = 0; d < ways; ++d)
      AssertReads([devices[d]], expected, $"device {d} alone");
    Assert.Multiple(() => {
      foreach (var (key, ledger) in ZfsAccountingProbe.Probe(devices[0]))
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: recorded space");
    });
  }

  [Test]
  public void Given_AMirror_When_Edited_Then_TheEditorRefusesItUntouched() {
    var (devices, _) = Write(2);
    var image = new MemoryStream();
    image.Write(devices[0]);
    var before = image.ToArray();
    Assert.Throws<NotSupportedException>(() => new ZfsFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("x.txt", [1])]));
    Assert.That(image.ToArray().SequenceEqual(before), Is.True);
  }

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  private static (string Dir, string Wsl) Stage(byte[][] devices, int? only = null) =>
    ZfsOracle.Stage(devices.Select((d, i) => ($"cwbm-{i}.img", d)).Where((_, i) => only == null || i == only).ToList());

  [TestCase(2), TestCase(3), Category("Slow"), Category("ExternalTool")]
  public void Given_AMirror_When_ZdbTraversesIt_Then_NothingLeaksAndFilesCopyOut(int ways) {
    ZfsOracle.RequireZdb();
    var (devices, expected) = Write(ways);
    var (dir, wsl) = Stage(devices);
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbm");
      foreach (var name in new[] { "hello.txt", "multi.bin", "dir/sub/deep.txt" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbm", name, expected[name]);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCase(2), TestCase(3), Category("Slow"), Category("ExternalTool")]
  public void Given_AMirror_When_TheKernelImportsAndScrubsIt_Then_ItServesEveryFile(int ways) {
    ZfsOracle.RequireAppliance();
    var (devices, expected) = Write(ways);
    var (dir, wsl) = Stage(devices);
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbm"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_OneHalfOfAMirror_When_TheKernelImportsIt_Then_ItServesEveryFileDegraded() {
    ZfsOracle.RequireAppliance();
    var (devices, expected) = Write(2);
    var (dir, wsl) = Stage(devices, only: 1);
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbm"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}

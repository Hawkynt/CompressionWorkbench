using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// Striped pools: several top-level vdevs, blocks spread over them. Written ones (version 28 and
/// 5000) and the one OpenZFS made (<c>ReferenceVectors/cwbstripe</c>), edited in place. The reader
/// and the accounting probe check them here; OpenZFS checks them in the external tests.
/// </summary>
[TestFixture]
public sealed class ZfsStripeTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private static byte[] Text(string seed, int lines) =>
    Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, lines).Select(i => $"{seed} {i % 23}\n")));

  private static (string Path, byte[] Data)[] Adds() => [
    ("striped/new.txt", Text("new", 50)), ("striped/lines.txt", Text("lines", 30000)),
    ("striped/random.bin", ZfsWriterTests.Pattern(500_000, 31)), ("hello.txt", "replaced\n"u8.ToArray()),
  ];

  private static (byte[][] Images, Dictionary<string, string> Expected, byte[][] Before) WriteAndEdit(int devices, string version) {
    var files = ZfsWriterTests.Corpus(large: false);
    var outputs = Enumerable.Range(0, devices).Select(_ => new MemoryStream()).ToArray();
    Descriptor.CreatePool(outputs, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions { FormatSpecific = { ["PoolName"] = "cwbs", ["Layout"] = "stripe", ["Version"] = version, ["Compression"] = "on" } });
    var before = outputs.Select(o => o.ToArray()).ToArray();
    var shuffled = outputs.Reverse().Cast<Stream>().ToList();
    Descriptor.RemoveFromPool(shuffled, ["dir", "multi.bin"]);
    Descriptor.AddToPool(shuffled, Adds().Select(a => ArchiveInputInfo.InMemory(a.Path, a.Data)).ToList());
    files.Remove("dir/sub/deep.txt");
    files.Remove("multi.bin");
    foreach (var (path, data) in Adds()) files[path] = data;
    return (outputs.Select(o => o.ToArray()).ToArray(), files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal), before);
  }

  private static (byte[][] Images, Dictionary<string, string> Expected) EditReference() {
    var devices = new[] { "cwbstripe-0", "cwbstripe-1" }.Select(ZfsReferencePools.Copy).ToArray();
    Descriptor.RemoveFromPool(devices, ["text.txt", "rand.bin", "big.txt", "dir"]);
    Descriptor.AddToPool(devices, Adds().Select(a => ArchiveInputInfo.InMemory(a.Path, a.Data)).ToList());
    var expected = ZfsReferencePools.Manifest("cwbstripe").Where(m => m.Kind == 'F' && m.Path is not ("text.txt" or "rand.bin" or "big.txt")
      && !m.Path.StartsWith("dir/", StringComparison.Ordinal)).ToDictionary(m => m.Path, m => m.Sha256!, StringComparer.Ordinal);
    foreach (var (path, data) in Adds()) expected[path] = ZfsOracle.Sha(data);
    return (devices.Select(d => d.ToArray()).ToArray(), expected);
  }

  private static void AssertReads(byte[][] images, Dictionary<string, string> expected) {
    using var reader = new ZfsReader(images.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList());
    Assert.That(reader.Warnings, Is.Empty);
    Assert.That(reader.Topology, Has.Count.EqualTo(images.Length), "one top-level vdev per device");
    var files = reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null).ToDictionary(e => e.Name, StringComparer.Ordinal);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (name, sha) in expected)
      Assert.That(ZfsOracle.Sha(reader.Extract(files[name])), Is.EqualTo(sha), name);
    // with a device missing, the pool cannot be read whole: its blocks really are spread
    var incomplete = false;
    try {
      using var half = new ZfsReader([new MemoryStream(images[0], writable: false)]);
      incomplete = half.Warnings.Count > 0;
    } catch (Exception e) when (e is IOException or InvalidDataException) {
      incomplete = true;
    }
    Assert.That(incomplete, Is.True, "blocks live on every device");
  }

  [TestCase(2, "28")]
  [TestCase(3, "5000")]
  public void Given_AWrittenStripe_When_Edited_Then_ItReadsBackAndAccountsForItsBlocks(int devices, string version) {
    var (images, expected, before) = WriteAndEdit(devices, version);
    AssertReads(images, expected);
    var end = ZfsAccountingProbe.Probe(images);
    Assert.Multiple(() => {
      foreach (var (key, ledger) in ZfsAccountingProbe.Probe(before).Concat(end))
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: recorded space");
    });
  }

  [Test]
  public void Given_TheStripeOpenZfsMade_When_Edited_Then_TheReaderSeesTheEditAndTheAccountingMovesWithTheBlocks() {
    var (images, expected) = EditReference();
    AssertReads(images, expected);
    ZfsAccountingProbe.AssertMovedTogether(
      ZfsAccountingProbe.Probe(ZfsReferencePools.ImageBytes("cwbstripe-0"), ZfsReferencePools.ImageBytes("cwbstripe-1")),
      ZfsAccountingProbe.Probe(images));
  }

  [Test]
  public void Given_AStripeWithADeviceMissing_When_Edited_Then_ItIsRefusedUntouched() {
    var half = ZfsReferencePools.Copy("cwbstripe-0");
    var before = half.ToArray();
    Assert.Throws<NotSupportedException>(() => Descriptor.AddToPool([half], [ArchiveInputInfo.InMemory("x", [1])]));
    Assert.That(half.ToArray().SequenceEqual(before), Is.True);
  }

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  private static void AssertOpenZfs(string pool, byte[][] images, Dictionary<string, string> expected) {
    var (dir, wsl) = ZfsOracle.Stage(images.Select((d, i) => ($"{pool}-{i}.img", d)).ToList());
    try {
      ZfsOracle.AssertNoLeaks(wsl, pool);
      ZfsOracle.AssertFeatureCounts(wsl, pool);
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, pool), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCase(2, "28"), TestCase(3, "5000"), Category("Slow"), Category("ExternalTool")]
  public void Given_AWrittenAndEditedStripe_When_OpenZfsChecksIt_Then_ItIsClean(int devices, string version) {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (images, expected, _) = WriteAndEdit(devices, version);
    AssertOpenZfs("cwbs", images, expected);
  }

  [Test, Category("Slow"), Category("ExternalTool")]
  public void Given_TheEditedStripeOpenZfsMade_When_OpenZfsChecksIt_Then_ItIsClean() {
    ZfsOracle.RequireZdb();
    ZfsOracle.RequireAppliance();
    var (images, expected) = EditReference();
    AssertOpenZfs("cwbstripe", images, expected);
  }
}

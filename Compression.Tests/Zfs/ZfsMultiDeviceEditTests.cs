using System.Text;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// In-place edits of mirror and RAID-Z pools: every device gets its share of each new block
/// (a full copy, or its data and parity columns) and the new uberblock. The reader and the
/// accounting probe check the edits here; OpenZFS checks them in the external tests —
/// <c>zdb -bcc</c>, and the kernel importing, scrubbing and serving the files whole and with
/// devices missing.
/// </summary>
[TestFixture]
public sealed class ZfsMultiDeviceEditTests {
  private static readonly ZfsFormatDescriptor Descriptor = new();

  private static IEnumerable<TestCaseData> Layouts() {
    yield return new TestCaseData("mirror", 2);
    yield return new TestCaseData("mirror", 3);
    yield return new TestCaseData("raidz1", 3);
    yield return new TestCaseData("raidz2", 5);
    yield return new TestCaseData("raidz3", 5);
  }

  private static int Tolerated(string layout, int devices) => layout == "mirror" ? devices - 1 : layout[^1] - '0';

  /// <summary>A written pool, then two edits through the devices given in a shuffled order.</summary>
  private static (byte[][] Devices, Dictionary<string, string> Expected, byte[][] Before) Build(string layout, int count) {
    var files = ZfsWriterTests.Corpus(large: false);
    var outputs = Enumerable.Range(0, count).Select(_ => new MemoryStream()).ToArray();
    Descriptor.CreatePool(outputs, files.Select(f => ArchiveInputInfo.InMemory(f.Key, f.Value)).ToList(),
      new FormatCreateOptions { FormatSpecific = { ["PoolName"] = "cwbe", ["Layout"] = layout, ["Compression"] = "lzjb" } });
    var before = outputs.Select(o => o.ToArray()).ToArray();
    var shuffled = outputs.Reverse().Cast<Stream>().ToList();

    var big = ZfsWriterTests.Pattern(1_300_000, 23);
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 5000).Select(i => $"row {i % 31}\n")));
    Descriptor.AddToPool(shuffled, [
      ArchiveInputInfo.InMemory("new/big.bin", big), ArchiveInputInfo.InMemory("new/text.txt", text),
      ArchiveInputInfo.InMemory("hello.txt", "replaced\n"u8.ToArray()),
    ]);
    Descriptor.RemoveFromPool(shuffled, ["dir", "multi.bin"]);
    files["new/big.bin"] = big;
    files["new/text.txt"] = text;
    files["hello.txt"] = "replaced\n"u8.ToArray();
    files.Remove("dir/sub/deep.txt");
    files.Remove("multi.bin");
    return (outputs.Select(o => o.ToArray()).ToArray(), files.ToDictionary(f => f.Key, f => ZfsOracle.Sha(f.Value), StringComparer.Ordinal), before);
  }

  [TestCaseSource(nameof(Layouts))]
  public void Given_AMultiDevicePool_When_Edited_Then_ItReadsBackAndAccountsForItsBlocks(string layout, int count) {
    var (devices, expected, before) = Build(layout, count);
    using (var reader = new ZfsReader(devices.Select(d => (Stream)new MemoryStream(d, writable: false)).ToList())) {
      Assert.That(reader.Warnings, Is.Empty);
      Assert.That(reader.Txg, Is.EqualTo(6UL), "two edits, two transaction groups");
      var files = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
      Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
      foreach (var (name, sha) in expected)
        Assert.That(ZfsOracle.Sha(reader.Extract(files[name])), Is.EqualTo(sha), name);
    }
    var start = ZfsAccountingProbe.Probe(before);
    var end = ZfsAccountingProbe.Probe(devices);
    Assert.Multiple(() => {
      foreach (var (key, ledger) in start.Concat(end))
        Assert.That(ledger.Recorded, Is.EqualTo(ledger.Counted), $"{key}: recorded space");
    });
  }

  [Test]
  public void Given_AMirrorWithADeviceMissing_When_Edited_Then_ItIsRefusedUntouched() {
    var outputs = new[] { new MemoryStream(), new MemoryStream() };
    Descriptor.CreatePool(outputs, [ArchiveInputInfo.InMemory("a.txt", [1, 2, 3])], new FormatCreateOptions());
    var half = new MemoryStream();
    half.Write(outputs[0].ToArray());
    var before = half.ToArray();
    Assert.Throws<NotSupportedException>(() => Descriptor.AddToPool([half], [ArchiveInputInfo.InMemory("b.txt", [4])]));
    Assert.That(half.ToArray().SequenceEqual(before), Is.True);
  }

  // ── OpenZFS ───────────────────────────────────────────────────────────────────────────

  private static (string Dir, string Wsl) Stage(byte[][] devices, int skip = 0) =>
    ZfsOracle.Stage(devices.Select((d, i) => ($"cwbe-{i}.img", d)).Skip(skip).ToList());

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedMultiDevicePool_When_ZdbTraversesIt_Then_NothingLeaksAndFilesCopyOut(string layout, int count) {
    ZfsOracle.RequireZdb();
    var (devices, expected, _) = Build(layout, count);
    var (dir, wsl) = Stage(devices);
    try {
      ZfsOracle.AssertNoLeaks(wsl, "cwbe");
      foreach (var name in new[] { "new/big.bin", "new/text.txt", "hello.txt" })
        ZfsOracle.AssertCopiesOut(dir, wsl, "cwbe", name, expected[name]);
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedMultiDevicePool_When_TheKernelImportsAndScrubsIt_Then_ItServesExactlyTheEdit(string layout, int count) {
    ZfsOracle.RequireAppliance();
    var (devices, expected, _) = Build(layout, count);
    var (dir, wsl) = Stage(devices);
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbe"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool")]
  public void Given_AnEditedMultiDevicePoolMissingDevices_When_TheKernelImportsIt_Then_ItServesExactlyTheEdit(string layout, int count) {
    ZfsOracle.RequireAppliance();
    var (devices, expected, _) = Build(layout, count);
    var (dir, wsl) = Stage(devices, skip: Tolerated(layout, count));
    try {
      Assert.That(ZfsOracle.KernelManifest(dir, wsl, "cwbe"), Is.EquivalentTo(expected));
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }
}

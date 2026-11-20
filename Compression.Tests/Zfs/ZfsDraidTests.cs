using System.IO.Compression;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

/// <summary>
/// dRAID pools: the fixed permutation maps regenerate exactly (every map's checksum), and pools the
/// OpenZFS kernel writes — single and double parity, with a distributed spare, with a file large
/// enough to cross redundancy groups — read byte for byte as the kernel served them, whole and
/// with a device missing.
/// </summary>
[TestFixture]
public sealed class ZfsDraidTests {
  [Test]
  public void Given_EveryChildCount_When_ThePermutationsAreGenerated_Then_TheirChecksumsMatchTheMaps() {
    foreach (var map in ZfsDraidMaps.Maps)
      Assert.DoesNotThrow(() => ZfsDraidVdev.Permutations(map.Children, out _), $"{map.Children} children");
  }

  [Test]
  public void Given_ABlock_When_Mapped_Then_ItTakesWholeRowsAndSplitsAtAGroupEnd() {
    var draid = new ZfsDraidVdev([.. Enumerable.Range(0, 5).Select(_ => (ZfsVdev)new ZfsMissingVdev("x"))], 2, 1, 1, 4, 9);
    Assert.That(draid.AllocatedSize(512), Is.EqualTo(3 * 512));
    Assert.That(draid.AllocatedSize(2048), Is.EqualTo(2 * 3 * 512));
    var rows = draid.Map(draid.GroupSize - 3 * 512, 4096);
    Assert.That(rows, Has.Count.EqualTo(2));
    Assert.That(rows.Sum(r => r.Where(c => c.IsData).Sum(c => c.Size)), Is.EqualTo(4096));
  }

  private static XdrNvList SpareSlot(string? resilverOn, string key = "resilver_txg") {
    XdrNvListWriter.NvList Part(string type, string name) {
      var l = new XdrNvListWriter.NvList().Add("type", type).Add("guid", 7UL);
      if (type == "file") l.Add("faulted", 1UL);
      if (resilverOn == name) l.Add(key, 42UL);
      return l;
    }
    var spare = new XdrNvListWriter.NvList().Add("type", "spare");
    if (resilverOn == "spare") spare.Add(key, 42UL);
    spare.Add("children", new[] { Part("file", "original"), Part("dspare", "dspare") });
    return XdrNvList.Decode(XdrNvListWriter.Pack(spare));
  }

  [Test]
  public void Given_ARebuiltDistributedSpare_When_TheEditorTakesItsSlot_Then_ItNamesTheOriginalAndTheSpare() {
    var (original, spare) = ZfsPoolEditor.DraidSpareSlot(SpareSlot(null), 2);
    Assert.That(original.String("type"), Is.EqualTo("file"));
    Assert.That(spare.String("type"), Is.EqualTo("dspare"));
  }

  [TestCase("dspare", "resilver_txg"), TestCase("dspare", "rebuild_txg"), TestCase("original", "resilver_txg"), TestCase("spare", "rebuild_txg")]
  public void Given_ADistributedSpareStillBeingRebuilt_When_TheEditorTakesItsSlot_Then_ItIsRefused(string on, string key) {
    var e = Assert.Throws<NotSupportedException>(() => ZfsPoolEditor.DraidSpareSlot(SpareSlot(on, key), 2));
    Assert.That(e!.Message, Does.Contain("still being rebuilt"));
  }

  [Test]
  public void Given_ADraidChildThatIsNeitherADeviceNorARebuiltSpare_When_TheEditorTakesItsSlot_Then_ItIsRefused() {
    var replacing = XdrNvList.Decode(XdrNvListWriter.Pack(new XdrNvListWriter.NvList().Add("type", "replacing")
      .Add("children", new[] { new XdrNvListWriter.NvList().Add("type", "file"), new XdrNvListWriter.NvList().Add("type", "file") })));
    Assert.Throws<NotSupportedException>(() => ZfsPoolEditor.DraidSpareSlot(replacing, 1));
    var lone = XdrNvList.Decode(XdrNvListWriter.Pack(new XdrNvListWriter.NvList().Add("type", "spare")
      .Add("children", new[] { new XdrNvListWriter.NvList().Add("type", "dspare") })));
    Assert.Throws<NotSupportedException>(() => ZfsPoolEditor.DraidSpareSlot(lone, 1));
  }

  public static IEnumerable<TestCaseData> Layouts() {
    yield return new TestCaseData("draid1:2d:1s:5c", 5).SetName("Given_ADraid1PoolTheKernelWrote_When_Read_Then_EveryFileMatches");
    yield return new TestCaseData("draid2:3d:0s:6c", 6).SetName("Given_ADraid2PoolTheKernelWrote_When_Read_Then_EveryFileMatches");
  }

  [TestCaseSource(nameof(Layouts)), Category("Slow"), Category("ExternalTool")]
  public void KernelPool(string layout, int children) {
    ZfsOracle.RequireAppliance();
    var (dir, wsl) = ZfsOracle.Stage([]);
    try {
      var devices = Enumerable.Range(0, children).Select(i => $"$O/d{i}.img").ToList();
      var log = ZfsOracle.Appliance(dir, wsl,
        $"for i in $(seq 0 {children - 1}); do truncate -s 128M $O/d$i.img; done\n" +
        $"zpool create -f -o ashift=9 -O compression=off -O mountpoint=$M/cwbdraid cwbdraid {layout} {string.Join(' ', devices)} || exit 1\n" +
        "D=$M/cwbdraid; head -c 52000000 /dev/urandom > $D/big.bin; seq 1 30000 > $D/text.txt; printf tiny > $D/tiny.txt\n" +
        "mkdir $D/dir; for i in $(seq 1 50); do head -c $((i * 3000)) /dev/urandom > $D/dir/f$i; done\n" +
        "zpool scrub -w cwbdraid; zpool status cwbdraid | grep -c 'with 0 errors' | sed 's/^/SCRUBBED /'\n" +
        "( cd $D && find . -type f | sort | while read -r p; do echo \"FILE $(sha256sum < \"$p\" | cut -d' ' -f1) ${p#./}\"; done )\n" +
        "zpool export cwbdraid && echo EXPORTED\n" +
        $"for i in $(seq 0 {children - 1}); do gzip -1 -c $O/d$i.img > $O/d$i.img.gz; done",
        [.. Enumerable.Range(0, children).Select(i => $"d{i}.img.gz")]);
      Assert.That(log, Does.Contain("SCRUBBED 1").And.Contain("EXPORTED"), log);
      var expected = log.Split('\n').Where(l => l.StartsWith("FILE ", StringComparison.Ordinal))
        .ToDictionary(l => l[70..].TrimEnd('\r'), l => l[5..69], StringComparer.Ordinal);
      Assert.That(expected, Has.Count.EqualTo(53));
      var images = Enumerable.Range(0, children).Select(i => {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(dir, $"d{i}.img.gz")), CompressionMode.Decompress);
        var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
      }).ToList();

      Assert.That(Read(images), Is.EquivalentTo(expected), "every device");
      for (var gone = 0; gone < children; ++gone)
        Assert.That(Read(images.Where((_, i) => i != gone).ToList()), Is.EquivalentTo(expected), $"without device {gone}");
    } finally {
      ZfsOracle.Cleanup(dir);
    }
  }

  private static Dictionary<string, string> Read(IReadOnlyList<byte[]> images) {
    using var reader = new ZfsReader(images.Select(i => (Stream)new MemoryStream(i, writable: false)).ToList());
    Assert.That(reader.Warnings, Is.Empty);
    Assert.That(reader.Topology.Single(), Does.StartWith("draid"));
    return reader.Entries.Where(e => !e.IsDirectory && e.LinkTarget == null)
      .ToDictionary(e => e.Name, e => ZfsOracle.Sha(reader.Extract(e)), StringComparer.Ordinal);
  }
}

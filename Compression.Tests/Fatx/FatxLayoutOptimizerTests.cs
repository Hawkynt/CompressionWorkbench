using Compression.Registry;
using FileSystem.Fatx;

namespace Compression.Tests.Fatx;

/// <summary>
/// FATX layout-optimiser wiring: the writer's auto cluster-size pick must reduce
/// wasted slack versus the canonical 16 KiB Xbox cluster for a small-file set,
/// the image must still round-trip, and the <see cref="ILayoutOptimizable"/>
/// contract (Analyze → recommend → RebuildStreaming) must work end-to-end.
/// </summary>
[TestFixture]
public class FatxLayoutOptimizerTests {

  private static long SlackOf(byte[] image, int sectorsPerCluster) {
    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    var clusterBytes = sectorsPerCluster * 512;
    long slack = 0;
    foreach (var e in r.Entries) {
      if (e.IsDirectory || e.Size <= 0) continue;
      slack += (clusterBytes - e.Size % clusterBytes) % clusterBytes;
    }
    return slack;
  }

  [Test, Category("Spec")]
  public void AutoCluster_SmallFiles_PicksLessSlackThanXboxDefault() {
    // Several sub-cluster files: at the 16 KiB Xbox default each wastes most of a
    // cluster; the optimiser should pick a smaller cluster with far less slack.
    var w = new FatxWriter();
    var rng = new Random(0x7A7);
    var sizes = new[] { 700, 1300, 2100, 900, 1500 };
    var pinned = new FatxWriter();
    foreach (var (sz, i) in sizes.Select((s, i) => (s, i))) {
      var data = new byte[sz];
      rng.NextBytes(data);
      w.AddFile($"f{i}.bin", data);
      pinned.AddFile($"f{i}.bin", data);
    }

    var auto = w.Build();                                 // optimiser-selected cluster
    var pinnedXbox = pinned.Build(sectorsPerCluster: 32); // 16 KiB clusters

    using var msAuto = new MemoryStream(auto);
    using var rAuto = new FatxReader(msAuto);
    var autoSlack = SlackOf(auto, (int)rAuto.SectorsPerCluster);
    var xboxSlack = SlackOf(pinnedXbox, 32);

    Assert.That(autoSlack, Is.LessThan(xboxSlack),
      "auto cluster selection must waste less slack than the 16 KiB Xbox default for sub-cluster files");
  }

  [Test, Category("Spec")]
  public void AutoCluster_Image_RoundTrips() {
    var w = new FatxWriter();
    var rng = new Random(0xB0B);
    var payloads = new Dictionary<string, byte[]>();
    foreach (var i in Enumerable.Range(0, 6)) {
      var data = new byte[500 + i * 333];
      rng.NextBytes(data);
      payloads[$"file{i}.dat"] = data;
      w.AddFile($"file{i}.dat", data);
    }
    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    foreach (var (name, expected) in payloads) {
      var e = r.Entries.First(x => x.Name == name && !x.IsDirectory);
      Assert.That(r.Extract(e), Is.EqualTo(expected), $"{name} must round-trip");
    }
  }

  [Test, Category("Spec")]
  public void PinnedCluster_IsHonouredVerbatim() {
    var w = new FatxWriter();
    w.AddFile("a.bin", new byte[100]);
    var image = w.Build(sectorsPerCluster: 64);
    using var ms = new MemoryStream(image);
    using var r = new FatxReader(ms);
    Assert.That(r.SectorsPerCluster, Is.EqualTo(64u), "an explicit cluster size must not be overridden");
  }

  [Test, Category("Spec")]
  public void ILayoutOptimizable_Analyze_Recommend_Rebuild() {
    // Build an image at a deliberately wasteful 64 KiB cluster, then prove the
    // optimisable contract: Analyze flags a smaller optimum, RebuildStreaming
    // produces a smaller-slack image that still round-trips.
    var w = new FatxWriter();
    var rng = new Random(0xFA7);
    var payloads = new Dictionary<string, byte[]>();
    foreach (var i in Enumerable.Range(0, 5)) {
      var data = new byte[800 + i * 200];
      rng.NextBytes(data);
      payloads[$"x{i}.bin"] = data;
      w.AddFile($"x{i}.bin", data);
    }
    var original = w.Build(sectorsPerCluster: 128); // 64 KiB clusters — wasteful

    var desc = new FatxFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<ILayoutOptimizable>());
    var opt = (ILayoutOptimizable)desc;

    using var src = new MemoryStream(original);
    var analysis = opt.AnalyzeLayout(src);
    Assert.That(analysis.CurrentUnitSize, Is.EqualTo(64 * 1024));
    Assert.That(analysis.OptimalUnitSize, Is.LessThan(analysis.CurrentUnitSize),
      "Analyze must recommend a smaller cluster for sub-cluster files");
    Assert.That(analysis.PotentialSavingsBytes, Is.GreaterThan(0));
    Assert.That(analysis.RequiresRebuild, Does.Contain("cluster size"));

    src.Position = 0;
    using var rebuilt = new MemoryStream();
    opt.RebuildStreaming(src, rebuilt, new LayoutRebuildOptions { UnitSize = 0 }); // auto
    var rebuiltBytes = rebuilt.ToArray();

    using var rr = new FatxReader(new MemoryStream(rebuiltBytes));
    Assert.That(rr.ClusterSize, Is.LessThan(64 * 1024), "rebuild must shrink the cluster");
    foreach (var (name, expected) in payloads) {
      var e = rr.Entries.First(x => x.Name == name && !x.IsDirectory);
      Assert.That(rr.Extract(e), Is.EqualTo(expected), $"{name} must survive the rebuild");
    }
  }

  [Test, Category("Spec")]
  public void ILayoutOptimizable_PatchInPlace_UpdatesVolumeId() {
    var w = new FatxWriter();
    w.AddFile("a.bin", new byte[10]);
    var image = w.Build();
    var desc = (ILayoutOptimizable)new FatxFormatDescriptor();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;
    desc.PatchInPlace(ms, new LayoutPatch { SerialNumber = 0xCAFEBABE });

    ms.Position = 0;
    using var r = new FatxReader(ms);
    // Volume id is at superblock +0x04; the reader doesn't surface it, so read raw.
    ms.Position = 0x04;
    Span<byte> buf = stackalloc byte[4];
    ms.ReadExactly(buf);
    Assert.That(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buf), Is.EqualTo(0xCAFEBABEu));
  }
}

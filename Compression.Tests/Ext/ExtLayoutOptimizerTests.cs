using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// ext layout-optimiser wiring: the auto block-size pick must reduce slack
/// versus the 4 KiB default for a small-file set, the resulting image must
/// round-trip, and the <see cref="ILayoutOptimizable"/> contract (Analyze →
/// recommend → RebuildStreaming) must work end-to-end.
/// </summary>
[TestFixture]
public class ExtLayoutOptimizerTests {

  private static long SlackOf(IReadOnlyList<long> sizes, int blockSize) {
    long slack = 0;
    foreach (var s in sizes)
      if (s > 0) slack += (blockSize - s % blockSize) % blockSize;
    return slack;
  }

  [Test, Category("Spec")]
  public void SelectOptimalBlockSize_SmallFiles_BeatsDefaultSlack() {
    var w = new ExtWriter();
    var sizes = new long[] { 300, 700, 1100, 500, 900 };
    foreach (var (s, i) in sizes.Select((s, i) => (s, i)))
      w.AddFile($"f{i}", new byte[s]);

    var chosen = w.SelectOptimalBlockSize();
    Assert.That(chosen, Is.AnyOf(1024, 2048, 4096));
    Assert.That(SlackOf(sizes, chosen), Is.LessThanOrEqualTo(SlackOf(sizes, 4096)),
      "the optimiser must never pick a block size with more slack than the 4 KiB default");
    // For these sub-4KiB files, 1024 strictly wins.
    Assert.That(chosen, Is.EqualTo(1024));
  }

  [Test, Category("Spec")]
  public void AutoSized_Image_RoundTrips() {
    var w = new ExtWriter();
    var rng = new Random(0xE47);
    var payloads = new Dictionary<string, byte[]>();
    foreach (var i in Enumerable.Range(0, 5)) {
      var data = new byte[200 + i * 300];
      rng.NextBytes(data);
      payloads[$"file{i}.dat"] = data;
      w.AddFile($"file{i}.dat", data);
    }
    var image = w.BuildAutoSized();

    using var r = new ExtReader(new MemoryStream(image));
    foreach (var (name, expected) in payloads) {
      var e = r.Entries.First(x => x.Name == name && !x.IsDirectory);
      Assert.That(r.Extract(e), Is.EqualTo(expected), $"{name} must round-trip");
    }
  }

  [Test, Category("Spec")]
  public void ILayoutOptimizable_Analyze_Recommend_Rebuild() {
    // Build at a wasteful 4 KiB block; Analyze should recommend 1 KiB and the
    // rebuild must shrink slack while preserving every file byte-for-byte.
    var w = new ExtWriter();
    var rng = new Random(0x123);
    var payloads = new Dictionary<string, byte[]>();
    foreach (var i in Enumerable.Range(0, 5)) {
      var data = new byte[400 + i * 200];
      rng.NextBytes(data);
      payloads[$"x{i}.bin"] = data;
      w.AddFile($"x{i}.bin", data);
    }
    var original = w.Build(blockSize: 4096, totalBlocks: 4096);

    var desc = new ExtFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<ILayoutOptimizable>());
    var opt = (ILayoutOptimizable)desc;

    using var src = new MemoryStream(original);
    var analysis = opt.AnalyzeLayout(src);
    Assert.That(analysis.CurrentUnitSize, Is.EqualTo(4096));
    Assert.That(analysis.OptimalUnitSize, Is.LessThan(4096),
      "Analyze must recommend a smaller block for sub-block files");
    Assert.That(analysis.PotentialSavingsBytes, Is.GreaterThan(0));
    Assert.That(analysis.RequiresRebuild, Does.Contain("block size"));

    src.Position = 0;
    using var rebuilt = new MemoryStream();
    opt.RebuildStreaming(src, rebuilt, new LayoutRebuildOptions { UnitSize = 0 });
    var rebuiltBytes = rebuilt.ToArray();

    using var rr = new ExtReader(new MemoryStream(rebuiltBytes));
    foreach (var (name, expected) in payloads) {
      var e = rr.Entries.First(x => x.Name == name && !x.IsDirectory);
      Assert.That(rr.Extract(e), Is.EqualTo(expected), $"{name} must survive the rebuild");
    }
  }

  [Test, Category("Spec")]
  public void ILayoutOptimizable_PatchInPlace_SetsVolumeLabel() {
    var w = new ExtWriter();
    w.AddFile("a", new byte[16]);
    var image = w.Build(blockSize: 1024, totalBlocks: 4096);
    var opt = (ILayoutOptimizable)new ExtFormatDescriptor();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.Position = 0;
    opt.PatchInPlace(ms, new LayoutPatch { VolumeLabel = "MYVOL" });

    // s_volume_name: 16 bytes at superblock +120.
    ms.Position = 1024 + 120;
    Span<byte> buf = stackalloc byte[16];
    ms.ReadExactly(buf);
    var label = System.Text.Encoding.ASCII.GetString(buf).TrimEnd('\0');
    Assert.That(label, Is.EqualTo("MYVOL"));
  }
}

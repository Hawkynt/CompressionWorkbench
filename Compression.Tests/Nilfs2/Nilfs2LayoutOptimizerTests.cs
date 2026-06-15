using Compression.Registry;
using FileSystem.Nilfs2;

namespace Compression.Tests.Nilfs2;

/// <summary>
/// NILFS2 layout-optimiser wiring: the descriptor exposes a BlockSize knob,
/// auto-selects a legal block size when unset, honours a pinned size, and
/// implements <see cref="ILayoutOptimizable"/> (Analyze → recommend →
/// RebuildStreaming) with a working round-trip. Because NILFS2 packs payloads
/// contiguously, the optimiser trims image tail-padding rather than per-file
/// cluster slack — the savings surface is smaller but the contract holds.
/// </summary>
[TestFixture]
public class Nilfs2LayoutOptimizerTests {

  private static IReadOnlyList<(string Name, byte[] Data)> RealEntries(byte[] image) {
    var r = new Nilfs2Reader(new MemoryStream(image));
    var synthetic = new HashSet<string> { "FULL.nilfs2", "metadata.ini", "superblock.bin" };
    return r.Entries
      .Where(e => !e.IsDirectory && !synthetic.Contains(e.Name))
      .Select(e => (e.Name, Data: r.Extract(e)))
      .ToList();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesBlockSizeSchemaKnob() {
    var desc = (IFormatOptionsSchema)new Nilfs2FormatDescriptor();
    var bs = desc.OptionsSchema.FirstOrDefault(o => o.Key == "BlockSize");
    Assert.That(bs, Is.Not.Null);
    Assert.That(bs!.AllowedValues, Does.Contain("0"));   // auto
    Assert.That(bs.AllowedValues, Does.Contain("4096"));
  }

  [Test, Category("Spec")]
  public void Create_AutoBlockSize_RoundTrips() {
    var desc = new Nilfs2FormatDescriptor();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "nilfs payload bytes"u8.ToArray());
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmp, "doc.txt", false)], new FormatCreateOptions());
      var image = ms.ToArray();
      var entries = RealEntries(image);
      Assert.That(entries.Select(e => e.Name), Does.Contain("doc.txt"));
      Assert.That(entries.First(e => e.Name == "doc.txt").Data, Is.EqualTo("nilfs payload bytes"u8.ToArray()));
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void Create_PinnedBlockSize_IsHonoured() {
    var desc = new Nilfs2FormatDescriptor();
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, new byte[5000]);
      var opts = new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "8192" },
      };
      using var ms = new MemoryStream();
      desc.Create(ms, [new ArchiveInputInfo(tmp, "big.bin", false)], opts);
      var r = new Nilfs2Reader(new MemoryStream(ms.ToArray()));
      Assert.That(1024 << (int)r.LogBlockSize, Is.EqualTo(8192), "a pinned block size must be honoured verbatim");
    } finally {
      File.Delete(tmp);
    }
  }

  [Test, Category("Spec")]
  public void ILayoutOptimizable_Analyze_Recommend_Rebuild() {
    // Build at a deliberately large 65536 block; Analyze should recommend a
    // smaller size and the rebuild must still round-trip.
    var w = new Nilfs2Writer();
    var rng = new Random(0xA1B);
    var payloads = new Dictionary<string, byte[]>();
    foreach (var i in Enumerable.Range(0, 4)) {
      var data = new byte[300 + i * 250];
      rng.NextBytes(data);
      payloads[$"e{i}.bin"] = data;
      w.AddFile($"e{i}.bin", data);
    }
    var original = w.Build(blockSize: 65536);

    var desc = new Nilfs2FormatDescriptor();
    Assert.That(desc, Is.InstanceOf<ILayoutOptimizable>());
    var opt = (ILayoutOptimizable)desc;

    using var src = new MemoryStream(original);
    var analysis = opt.AnalyzeLayout(src);
    Assert.That(analysis.CurrentUnitSize, Is.EqualTo(65536));
    Assert.That(analysis.OptimalUnitSize, Is.LessThanOrEqualTo(analysis.CurrentUnitSize));

    src.Position = 0;
    using var rebuilt = new MemoryStream();
    opt.RebuildStreaming(src, rebuilt, new LayoutRebuildOptions { UnitSize = 0 });
    var entries = RealEntries(rebuilt.ToArray()).ToDictionary(e => e.Name, e => e.Data);
    foreach (var (name, expected) in payloads)
      Assert.That(entries[name], Is.EqualTo(expected), $"{name} must survive the rebuild");
  }
}

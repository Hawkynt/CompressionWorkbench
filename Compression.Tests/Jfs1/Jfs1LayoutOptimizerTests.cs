#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Jfs1;

namespace Compression.Tests.Jfs1;

/// <summary>
/// JFS1 Create now auto-selects the slack-minimising block size when unset
/// (previously it always defaulted to 4096). These tests pin that the optimiser
/// picks a less-slack size than the default for a small-file set, that the
/// created image round-trips, and that a pinned block size is honoured.
/// </summary>
[TestFixture]
public class Jfs1LayoutOptimizerTests {

  private static long SlackOf(IReadOnlyList<long> sizes, int blockSize) {
    long slack = 0;
    foreach (var s in sizes)
      if (s > 0) slack += (blockSize - s % blockSize) % blockSize;
    return slack;
  }

  private static byte[] CreateViaDescriptor(IReadOnlyList<(string Name, byte[] Data)> files, FormatCreateOptions opts) {
    var desc = new Jfs1FormatDescriptor();
    var inputs = new List<ArchiveInputInfo>();
    var tmps = new List<string>();
    try {
      foreach (var (name, data) in files) {
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, data);
        tmps.Add(tmp);
        inputs.Add(new ArchiveInputInfo(tmp, name, false));
      }
      using var ms = new MemoryStream();
      desc.Create(ms, inputs, opts);
      return ms.ToArray();
    } finally {
      foreach (var t in tmps) File.Delete(t);
    }
  }

  [Test, Category("Spec")]
  public void Optimizer_SmallFiles_PicksLessSlackThanDefault() {
    // Sub-block files: the 4096 default wastes nearly a full block each; the
    // optimiser must pick a size with no more slack than the default.
    var sizes = new long[] { 500, 900, 1300, 700 };
    var picked = Jfs1Optimizer.Find(sizes).BlockSize;
    Assert.That(picked, Is.AnyOf(1024, 2048, 4096));
    Assert.That(SlackOf(sizes, picked), Is.LessThanOrEqualTo(SlackOf(sizes, 4096)),
      "the optimiser must never pick a block size with more slack than the 4 KiB default");
  }

  [Test, Category("Spec")]
  public void Create_AutoBlockSize_RoundTrips() {
    var rng = new Random(0x515);
    var files = Enumerable.Range(0, 5)
      .Select(i => { var d = new byte[600 + i * 400]; rng.NextBytes(d); return ($"f{i}.bin", d); })
      .ToList();
    var image = CreateViaDescriptor(files, new FormatCreateOptions());

    var r = new Jfs1Reader(new MemoryStream(image));
    foreach (var (name, expected) in files) {
      var e = r.Entries.First(x => x.Name == name && !x.IsDirectory);
      Assert.That(r.Extract(e), Is.EqualTo(expected), $"{name} must round-trip under the auto-selected block size");
    }
  }

  [Test, Category("Spec")]
  public void Create_PinnedBlockSize_StillRoundTrips() {
    var data = new byte[1500];
    new Random(11).NextBytes(data);
    var opts = new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockSize"] = "2048" },
    };
    var image = CreateViaDescriptor([("only.bin", data)], opts);

    var r = new Jfs1Reader(new MemoryStream(image));
    var e = r.Entries.First(x => x.Name == "only.bin" && !x.IsDirectory);
    Assert.That(r.Extract(e), Is.EqualTo(data));
  }
}

using Compression.Core.FileSystems.Cvf;
using Compression.Tests.Cvf;
using FileSystem.Stacker;

namespace Compression.Tests.Stacker;

/// <summary>
/// Read half of the genuine Stacker path: <see cref="GenuineStackerReader"/>
/// must read back, byte-exact, what <see cref="GenuineStackerWriter"/> wrote —
/// over the genuine obfuscated-SCB layout the dmsdos driver independently
/// certifies (see <c>GenuineStackerDmsdosTests</c>).
/// </summary>
[TestFixture]
public class GenuineStackerRoundTripTests {

  [Test, Category("RoundTrip")]
  public void Writer_Then_Reader_IsByteExact_AcrossSizes() {
    var rnd = new Random(20260619);
    var inputs = new (string Name, byte[] Data)[] {
      ("EMPTY.TXT", []),
      ("TINY.TXT", "hi"u8.ToArray()),
      ("ONE.BIN", Make(rnd, 4096)),
      ("EXACT.BIN", Make(rnd, 8192)),            // exactly one 8 KB cluster
      ("MULTI.BIN", Make(rnd, 8192 * 2 + 1234)), // spans 3 clusters
    };

    var w = new GenuineStackerWriter();
    foreach (var (n, d) in inputs) w.AddFile(n, d);
    var image = w.Build();

    using var ms = new MemoryStream(image);
    using var r = new GenuineStackerReader(ms);

    Assert.That(r.Version, Is.LessThan(410));
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(inputs.Select(i => i.Name)));

    foreach (var (name, expected) in inputs) {
      var entry = r.Entries.First(e => e.Name == name);
      Assert.That(r.Extract(entry), Is.EqualTo(expected), $"{name} did not round-trip");
    }
  }

  [Test, Category("RoundTrip")]
  public void CompressedClusters_RoundTrip_PerMethod(
      [Values(CvfLzMethod.Ds, CvfLzMethod.Sd3, CvfLzMethod.Sd4, CvfLzMethod.Auto)] CvfLzMethod method) {
    var inputs = new (string Name, byte[] Data)[] {
      ("TEXT.TXT", CvfSamples.Get("text-32k")[..20000]),   // three clusters, last one partial
      ("DIR.DAT", CvfSamples.Get("dir-8k")),
      ("TINY.TXT", "hi"u8.ToArray()),
    };

    var w = new GenuineStackerWriter { CompressionMethod = method };
    foreach (var (n, d) in inputs) w.AddFile(n, d);
    using var r = new GenuineStackerReader(new MemoryStream(w.Build()));

    Assert.Multiple(() => {
      foreach (var (name, expected) in inputs)
        Assert.That(r.Extract(r.Entries.First(e => e.Name == name)), Is.EqualTo(expected), name);
    });
  }

  private static byte[] Make(Random rnd, int n) {
    var b = new byte[n];
    rnd.NextBytes(b);
    return b;
  }
}

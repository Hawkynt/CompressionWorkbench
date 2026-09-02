using Compression.Lib;
using Compression.Registry;
using FileFormat.Lzma;

namespace Compression.Tests.Optimizer;

/// <summary>
/// The LZMA stream format exposes its real tunables (level, dictionary size,
/// lc/lp/pb) through an <see cref="IFormatOptionsSchema"/>: the parameterised
/// <c>Compress</c> must honour any chosen combination and still round-trip via
/// <see cref="LzmaFormatDescriptor.Decompress"/>, and
/// <see cref="CompressionOptimizer"/> must search that multi-axis space and
/// return the smallest output (which also round-trips) plus the winning knobs.
/// </summary>
[TestFixture]
public class LzmaOptionsTests {

  private static byte[] CompressibleSample() {
    // Repetitive but not trivial: tuning the knobs should pay off.
    var ms = new MemoryStream();
    var rng = new Random(1234);
    var phrases = new[] { "the quick brown fox ", "jumps over the lazy dog ", "compression workbench " };
    for (var i = 0; i < 4000; i++) {
      var p = System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]);
      ms.Write(p);
    }
    return ms.ToArray();
  }

  private static byte[] Compress(LzmaFormatDescriptor d, byte[] data, IReadOnlyDictionary<string, string> opts) {
    using var inMs = new MemoryStream(data);
    using var outMs = new MemoryStream();
    d.Compress(inMs, outMs, new FormatCreateOptions { FormatSpecific = FormatCreateOptions.FormatSpecificFrom(opts) });
    return outMs.ToArray();
  }

  private static byte[] Decompress(LzmaFormatDescriptor d, byte[] compressed) {
    using var inMs = new MemoryStream(compressed);
    using var outMs = new MemoryStream();
    d.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  [Test, Category("Spec")]
  public void TwoDifferentOptionSets_BothRoundTrip() {
    var d = new LzmaFormatDescriptor();
    var data = CompressibleSample();

    var fast = Compress(d, data, new Dictionary<string, string> {
      ["Level"] = "Fast", ["DictionarySize"] = "64 KB", ["Lc"] = "0", ["Lp"] = "0", ["Pb"] = "0",
    });
    var best = Compress(d, data, new Dictionary<string, string> {
      ["Level"] = "Best", ["DictionarySize"] = "16 MB", ["Lc"] = "4", ["Lp"] = "1", ["Pb"] = "2",
    });

    Assert.That(Decompress(d, fast), Is.EqualTo(data), "the Fast/64 KB option set round-trips");
    Assert.That(Decompress(d, best), Is.EqualTo(data), "the Best/16 MB option set round-trips");
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallest_RoundTrips_AndReportsOptionKeys() {
    var d = new LzmaFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, d, d);

    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data shrinks");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data), "the optimal result round-trips");

    // The reported parameters must carry the schema's option keys.
    Assert.That(result.Parameters, Does.ContainKey("Level"));
    Assert.That(result.Parameters, Does.ContainKey("DictionarySize"));
    Assert.That(result.Parameters, Does.ContainKey("Lc"));
    Assert.That(result.Parameters, Does.ContainKey("Lp"));
    Assert.That(result.Parameters, Does.ContainKey("Pb"));

    // The winning output must be no larger than re-compressing with the very
    // same reported parameters (i.e. the result is internally consistent).
    var replay = Compress(d, data, result.Parameters);
    Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(replay.Length));
  }
}

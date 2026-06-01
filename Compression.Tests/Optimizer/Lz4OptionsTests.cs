using Compression.Lib;
using Compression.Registry;
using FileFormat.Lz4;

namespace Compression.Tests.Optimizer;

/// <summary>
/// The LZ4 frame format exposes a compression-level knob (Fast vs the
/// high-compression encoder) through its <see cref="IFormatOptionsSchema"/>.
/// Its parameterised <c>Compress</c> must honour the chosen level, every level
/// must still round-trip, and <see cref="CompressionOptimizer"/> must search the
/// level space and return the smallest output (which also round-trips).
/// </summary>
[TestFixture]
public class Lz4OptionsTests {

  private static byte[] CompressibleSample() {
    // Repetitive but not trivial: the HC encoder should beat Fast here.
    var ms = new MemoryStream();
    var rng = new Random(4321);
    var phrases = new[] { "the quick brown fox ", "jumps over the lazy dog ", "compression workbench " };
    for (var i = 0; i < 4000; i++) {
      var p = System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]);
      ms.Write(p);
    }
    return ms.ToArray();
  }

  private static byte[] Decompress(Lz4FormatDescriptor d, byte[] compressed) {
    using var inMs = new MemoryStream(compressed);
    using var outMs = new MemoryStream();
    d.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  private static byte[] CompressAt(Lz4FormatDescriptor d, byte[] data, string level) {
    using var inMs = new MemoryStream(data);
    using var outMs = new MemoryStream();
    d.Compress(inMs, outMs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
    });
    return outMs.ToArray();
  }

  [Test, Category("Spec")]
  public void Lz4_HonoursLevelOption_AndRoundTrips() {
    var d = new Lz4FormatDescriptor();
    var data = CompressibleSample();

    var fast = CompressAt(d, data, "Fast");
    var hc = CompressAt(d, data, "Hc");

    Assert.That(hc.Length, Is.LessThanOrEqualTo(fast.Length),
      "the HC level should be no larger than Fast on compressible data");
    Assert.That(Decompress(d, fast), Is.EqualTo(data), "Fast output round-trips");
    Assert.That(Decompress(d, hc), Is.EqualTo(data), "HC output round-trips");
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallest_AndResultRoundTrips() {
    var d = new Lz4FormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, d, d);

    Assert.That(result.Parameters, Does.ContainKey("Level"), "the winning level is reported");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data shrinks");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data), "optimal result round-trips");

    // The optimizer's pick must be <= every individual level (it is the minimum).
    foreach (var level in d.OptionsSchema.Single(o => o.Key == "Level").AllowedValues!) {
      var single = CompressAt(d, data, level);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(single.Length),
        $"optimizer result must be <= level {level}");
    }
  }
}

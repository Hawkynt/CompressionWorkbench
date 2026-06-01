using Compression.Lib;
using Compression.Registry;
using FileFormat.Brotli;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Brotli exposes a tunable compression-quality knob via its
/// <see cref="IFormatOptionsSchema"/>. The parameterised
/// <see cref="BrotliFormatDescriptor.Compress(System.IO.Stream,System.IO.Stream,FormatCreateOptions)"/>
/// must honour the chosen quality, and the
/// <see cref="CompressionOptimizer"/> must search that space and return the
/// smallest output, which still round-trips.
/// </summary>
[TestFixture]
public class BrotliOptionsTests {

  private static byte[] CompressibleSample() {
    // Repetitive but not trivial: a higher quality should pay off.
    var ms = new MemoryStream();
    var rng = new Random(4321);
    var phrases = new[] { "the quick brown fox ", "jumps over the lazy dog ", "compression workbench " };
    for (var i = 0; i < 4000; i++) {
      var p = System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]);
      ms.Write(p);
    }
    return ms.ToArray();
  }

  private static byte[] Compress(BrotliFormatDescriptor d, byte[] data, string quality) {
    using var inMs = new MemoryStream(data);
    using var outMs = new MemoryStream();
    d.Compress(inMs, outMs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Quality"] = quality },
    });
    return outMs.ToArray();
  }

  private static byte[] Decompress(BrotliFormatDescriptor d, byte[] compressed) {
    using var inMs = new MemoryStream(compressed);
    using var outMs = new MemoryStream();
    d.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  [Test, Category("Spec")]
  public void Brotli_HonoursQualityOption_AndBothRoundTrip() {
    var d = new BrotliFormatDescriptor();
    var data = CompressibleSample();

    var low = Compress(d, data, "Fast");
    var high = Compress(d, data, "Best");

    Assert.That(high.Length, Is.LessThanOrEqualTo(low.Length),
      "best quality should be no larger than fast quality on compressible data");
    Assert.That(Decompress(d, low), Is.EqualTo(data), "fast-quality output round-trips");
    Assert.That(Decompress(d, high), Is.EqualTo(data), "best-quality output round-trips");
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallestBrotli_AndResultRoundTrips() {
    var d = new BrotliFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, d, d);

    Assert.That(result.Parameters, Does.ContainKey("Quality"), "the winning quality is reported");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data shrinks");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data), "optimal result round-trips");

    // The optimizer's pick must be <= every individual quality (it is the minimum).
    foreach (var q in new[] { "Uncompressed", "Fast", "Default", "Best" })
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(Compress(d, data, q).Length),
        $"optimizer result must be <= quality {q}");
  }
}

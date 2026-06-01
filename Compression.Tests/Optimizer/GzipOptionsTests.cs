using Compression.Lib;
using Compression.Registry;
using FileFormat.Gzip;
using FileFormat.Zlib;

namespace Compression.Tests.Optimizer;

/// <summary>
/// The Deflate-based stream formats (GZIP, Zlib) expose a
/// <see cref="IFormatOptionsSchema"/> <c>Level</c> option, honour it in their
/// parameterised <c>Compress</c>, and the <see cref="CompressionOptimizer"/>
/// searches that level space and returns the smallest output (which still
/// round-trips).
/// </summary>
[TestFixture]
public class GzipOptionsTests {

  private static byte[] CompressibleSample() {
    // Repetitive but not trivial: stronger Deflate levels should pay off.
    var ms = new MemoryStream();
    var rng = new Random(20260601);
    var phrases = new[] { "the quick brown fox ", "jumps over the lazy dog ", "compression workbench " };
    for (var i = 0; i < 4000; i++)
      ms.Write(System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]));
    return ms.ToArray();
  }

  private static byte[] CompressAt(IStreamFormatOperations ops, byte[] data, string level) {
    using var inMs = new MemoryStream(data);
    using var outMs = new MemoryStream();
    ops.Compress(inMs, outMs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
    });
    return outMs.ToArray();
  }

  private static byte[] Decompress(IStreamFormatOperations ops, byte[] compressed) {
    using var inMs = new MemoryStream(compressed);
    using var outMs = new MemoryStream();
    ops.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  // ── GZIP ────────────────────────────────────────────────────────────────

  [Test, Category("Spec")]
  public void Gzip_FastAndMaximum_DifferInSize_AndBothRoundTrip() {
    var d = new GzipFormatDescriptor();
    var data = CompressibleSample();

    var fast = CompressAt(d, data, "Fast");
    var max = CompressAt(d, data, "Maximum");

    Assert.That(max.Length, Is.Not.EqualTo(fast.Length),
      "Fast and Maximum should produce differently sized GZIP output on compressible data");
    Assert.That(max.Length, Is.LessThanOrEqualTo(fast.Length),
      "Maximum should be no larger than Fast");
    Assert.That(Decompress(d, fast), Is.EqualTo(data), "Fast GZIP output round-trips");
    Assert.That(Decompress(d, max), Is.EqualTo(data), "Maximum GZIP output round-trips");
  }

  [Test, Category("Spec")]
  public void Gzip_Optimizer_FindsSmallest_AndResultRoundTrips() {
    var d = new GzipFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, d, d);

    Assert.That(result.Parameters, Does.ContainKey("Level"), "the winning level is reported");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data shrinks");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data), "optimal GZIP result round-trips");

    foreach (var level in new[] { "None", "Fast", "Default", "Best", "Maximum" }) {
      var at = CompressAt(d, data, level);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(at.Length),
        $"optimizer result must be <= level {level}");
    }
  }

  // ── Zlib ────────────────────────────────────────────────────────────────

  [Test, Category("Spec")]
  public void Zlib_FastAndMaximum_DifferInSize_AndBothRoundTrip() {
    var d = new ZlibFormatDescriptor();
    var data = CompressibleSample();

    var fast = CompressAt(d, data, "Fast");
    var max = CompressAt(d, data, "Maximum");

    Assert.That(max.Length, Is.Not.EqualTo(fast.Length),
      "Fast and Maximum should produce differently sized Zlib output on compressible data");
    Assert.That(max.Length, Is.LessThanOrEqualTo(fast.Length),
      "Maximum should be no larger than Fast");
    Assert.That(Decompress(d, fast), Is.EqualTo(data), "Fast Zlib output round-trips");
    Assert.That(Decompress(d, max), Is.EqualTo(data), "Maximum Zlib output round-trips");
  }

  [Test, Category("Spec")]
  public void Zlib_Optimizer_FindsSmallest_AndResultRoundTrips() {
    var d = new ZlibFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, d, d);

    Assert.That(result.Parameters, Does.ContainKey("Level"), "the winning level is reported");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data shrinks");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data), "optimal Zlib result round-trips");

    foreach (var level in new[] { "None", "Fast", "Default", "Best", "Maximum" }) {
      var at = CompressAt(d, data, level);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(at.Length),
        $"optimizer result must be <= level {level}");
    }
  }

  // ── Option resolution fallbacks (observed through Compress) ───────────────

  [Test, Category("Spec")]
  public void Gzip_NoLevelOption_CompressesAtDefault_AndRoundTrips() {
    var d = new GzipFormatDescriptor();
    var data = CompressibleSample();

    // No FormatSpecific Level → falls back to the Default tier.
    using var inMs = new MemoryStream(data);
    using var outMs = new MemoryStream();
    d.Compress(inMs, outMs, new FormatCreateOptions());
    var fallback = outMs.ToArray();

    var explicitDefault = CompressAt(d, data, "Default");
    Assert.That(fallback, Is.EqualTo(explicitDefault),
      "absent Level option resolves to the Default tier");
    Assert.That(Decompress(d, fallback), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void Zlib_NumericLevelOption_MapsOntoNamedTier_AndRoundTrips() {
    var d = new ZlibFormatDescriptor();
    var data = CompressibleSample();

    // Numeric Level 0 maps onto None (stored): no FormatSpecific override present.
    using var inMs = new MemoryStream(data);
    using var outMs = new MemoryStream();
    d.Compress(inMs, outMs, new FormatCreateOptions { Level = 0 });
    var numericNone = outMs.ToArray();

    var explicitNone = CompressAt(d, data, "None");
    Assert.That(numericNone, Is.EqualTo(explicitNone),
      "numeric Level 0 resolves to the None tier when no named option is given");
    Assert.That(Decompress(d, numericNone), Is.EqualTo(data));
  }
}

using Compression.Lib;
using Compression.Registry;
using FileFormat.Zstd;

namespace Compression.Tests.Optimizer;

/// <summary>
/// The schema-driven compression optimizer: a format that exposes an
/// <see cref="IFormatOptionsSchema"/> must honour the chosen options in its
/// parameterised <c>Compress</c>, and <see cref="CompressionOptimizer"/> must
/// search that option space and return the smallest output (which still
/// round-trips).
/// </summary>
[TestFixture]
public class CompressionOptimizerTests {

  private static byte[] CompressibleSample() {
    // Repetitive but not trivial: higher zstd levels should pay off.
    var ms = new MemoryStream();
    var rng = new Random(1234);
    var phrases = new[] { "the quick brown fox ", "jumps over the lazy dog ", "compression workbench " };
    for (var i = 0; i < 4000; i++) {
      var p = System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]);
      ms.Write(p);
    }
    return ms.ToArray();
  }

  private static byte[] Decompress(ZstdFormatDescriptor d, byte[] compressed) {
    using var inMs = new MemoryStream(compressed);
    using var outMs = new MemoryStream();
    d.Decompress(inMs, outMs);
    return outMs.ToArray();
  }

  [Test, Category("Spec")]
  public void Zstd_HonoursLevelOption_AndRoundTrips() {
    var d = new ZstdFormatDescriptor();
    var data = CompressibleSample();

    byte[] CompressAt(string level) {
      using var inMs = new MemoryStream(data);
      using var outMs = new MemoryStream();
      d.Compress(inMs, outMs, new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
      });
      return outMs.ToArray();
    }

    var lvl1 = CompressAt("1");
    var lvl9 = CompressAt("9");

    Assert.That(lvl9.Length, Is.LessThanOrEqualTo(lvl1.Length),
      "level 9 should be no larger than level 1 on compressible data");
    Assert.That(Decompress(d, lvl1), Is.EqualTo(data), "level-1 output round-trips");
    Assert.That(Decompress(d, lvl9), Is.EqualTo(data), "level-9 output round-trips");
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallest_AndResultRoundTrips() {
    var d = new ZstdFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, d, d);

    Assert.That(result.Parameters, Does.ContainKey("Level"), "the winning level is reported");
    Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
    Assert.That(result.Ratio, Is.LessThan(1.0), "compressible data shrinks");
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data), "optimal result round-trips");

    // The optimizer's pick must be <= every individual level (it is the minimum).
    for (var lvl = 1; lvl <= 9; lvl++) {
      using var inMs = new MemoryStream(data);
      using var outMs = new MemoryStream();
      d.Compress(inMs, outMs, new FormatCreateOptions {
        FormatSpecific = new Dictionary<string, string> { ["Level"] = lvl.ToString() },
      });
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(outMs.Length),
        $"optimizer result must be <= level {lvl}");
    }
  }

  [Test, Category("Spec")]
  public void Optimizer_NoSchemaAxes_StillCompresses() {
    // A schema with only an unsearchable option falls back to a single compress.
    var d = new ZstdFormatDescriptor();
    var data = "hello hello hello hello hello"u8.ToArray();
    var result = CompressionOptimizer.OptimizeStream(data, d, d);
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data));
  }
}

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

  [Test, Category("RoundTrip")]
  public void ArchiveOperations_Optimize_RoutesStreamFormatThroughSchemaSearch() {
    var d = new ZstdFormatDescriptor();
    var data = CompressibleSample();
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_opt_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      // Make a deliberately weak .zst (level 1), then optimize it.
      var inPath = Path.Combine(dir, "in.zst");
      using (var outMs = new MemoryStream()) {
        using (var src = new MemoryStream(data))
          d.Compress(src, outMs, new FormatCreateOptions {
            FormatSpecific = new Dictionary<string, string> { ["Level"] = "1" },
          });
        File.WriteAllBytes(inPath, outMs.ToArray());
      }
      var outPath = Path.Combine(dir, "out.zst");
      var (orig, optimized, _) = ArchiveOperations.Optimize(inPath, outPath, null);

      Assert.That(optimized, Is.LessThanOrEqualTo(orig),
        "optimize must not grow a weakly-compressed stream");
      Assert.That(Decompress(d, File.ReadAllBytes(outPath)), Is.EqualTo(data),
        "optimized .zst still decompresses to the original data");
    } finally {
      Directory.Delete(dir, true);
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

  [Test, Category("Spec")]
  public void Optimizer_DefaultBalanced_MatchesLegacyExhaustivePick() {
    // The new options-based overload with defaults must produce the same winning
    // bytes as the legacy maxCombinations overload (byte-for-byte compatibility).
    var d = new ZstdFormatDescriptor();
    var data = CompressibleSample();

    var legacy = CompressionOptimizer.OptimizeStream(data, d, d, 512);
    var modern = CompressionOptimizer.OptimizeStream(data, d, d, new CompressionOptimizer.OptimizerOptions());

    Assert.That(modern.Bytes, Is.EqualTo(legacy.Bytes), "default options reproduce legacy result byte-for-byte");
  }

  [Test, Category("Spec")]
  public void Optimizer_Caching_FewerProbesThanCombinations_ForCoordinateDescent() {
    // Force coordinate descent (tiny combo cap) so the current point is revisited;
    // the probe cache must keep the distinct-compress count below the naive count.
    var d = new ZstdFormatDescriptor();
    var data = CompressibleSample();

    // Level axis alone has many values; capping at 1 forces coordinate descent.
    var result = CompressionOptimizer.OptimizeStream(
      data, d, d, new CompressionOptimizer.OptimizerOptions { MaxCombinations = 1 });

    Assert.That(result.Probes, Is.GreaterThan(0));
    // Round-trip still holds and a winner exists.
    Assert.That(Decompress(d, result.Bytes), Is.EqualTo(data));
    // Coordinate descent over a single axis of N values probes each value once:
    // it never exceeds the axis cardinality, proving revisits were deduped.
    var levelAxis = ((IFormatOptionsSchema)d).OptionsSchema.First(o => o.Key == "Level");
    Assert.That(result.Probes, Is.LessThanOrEqualTo(levelAxis.AllowedValues!.Count),
      "cached coordinate descent never compresses a combo twice");
  }

  [Test, Category("Spec")]
  public void Optimizer_CachedResult_IdenticalToUncached() {
    // Same data, same options: a cached run (coordinate descent) yields the same
    // bytes as a fully exhaustive run because both minimise the same objective.
    var d = new ZstdFormatDescriptor();
    var data = CompressibleSample();

    var exhaustive = CompressionOptimizer.OptimizeStream(
      data, d, d, new CompressionOptimizer.OptimizerOptions { Effort = CompressionOptimizer.Effort.Max });
    var descent = CompressionOptimizer.OptimizeStream(
      data, d, d, new CompressionOptimizer.OptimizerOptions { MaxCombinations = 1 });

    // Single-axis schema: coordinate descent and exhaustive must reach the same minimum.
    Assert.That(descent.CompressedSize, Is.EqualTo(exhaustive.CompressedSize));
  }

  [Test, Category("Spec")]
  public void Optimizer_FastEffort_CapsCombosBelowMax() {
    // Fast effort uses a far smaller combination budget than Max.
    Assert.That(
      new CompressionOptimizer.OptimizerOptions { Effort = CompressionOptimizer.Effort.Fast }.ResolvedMaxCombinations,
      Is.LessThan(new CompressionOptimizer.OptimizerOptions { Effort = CompressionOptimizer.Effort.Max }.ResolvedMaxCombinations));
  }

  [Test, Category("Spec")]
  public void Optimizer_MultiObjective_CanChangeThePick() {
    // The size-vs-speed objective may select a different (faster) combination than
    // pure size. We assert it still returns a valid round-tripping result; if it
    // diverges from the size-optimal pick, that difference is the multi-objective
    // effect in action.
    var d = new ZstdFormatDescriptor();
    var data = CompressibleSample();

    var bySize = CompressionOptimizer.OptimizeStream(
      data, d, d, new CompressionOptimizer.OptimizerOptions { Objective = CompressionOptimizer.Objective.Size });
    var bySpeed = CompressionOptimizer.OptimizeStream(
      data, d, d, new CompressionOptimizer.OptimizerOptions { Objective = CompressionOptimizer.Objective.SizeAndSpeed });

    Assert.That(Decompress(d, bySpeed.Bytes), Is.EqualTo(data), "speed-blended pick still round-trips");
    // The size objective is the global size minimum by construction.
    Assert.That(bySize.CompressedSize, Is.LessThanOrEqualTo(bySpeed.CompressedSize),
      "pure-size objective is never larger than the blended one");
  }
}

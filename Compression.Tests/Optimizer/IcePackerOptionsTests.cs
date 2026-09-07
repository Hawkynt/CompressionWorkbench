using Compression.Lib;
using Compression.Registry;
using FileFormat.IcePacker;

namespace Compression.Tests.Optimizer;

/// <summary>
/// ICE's compression level is an encoder-only match-search knob: deeper hash
/// chains may discover a longer match without changing the stream syntax. These
/// tests keep a deterministic collision-heavy corpus so the levels are observable
/// and verify that the generic optimizer really chooses the smallest candidate.
/// </summary>
[TestFixture]
public sealed class IcePackerOptionsTests {

  private static byte[] LevelSensitiveSample() {
    var rng = new Random(1);
    var data = new byte[30_000];
    var p = 0;
    while (p < data.Length) {
      var run = rng.Next(2, 18);
      var value = (byte)rng.Next(64);
      for (var i = 0; i < run && p < data.Length; ++i)
        data[p++] = value;
    }
    return data;
  }

  private static byte[] CompressAt(IcePackerFormatDescriptor descriptor, byte[] data, string level) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
    });
    return output.ToArray();
  }

  private static byte[] CompressDefault(IcePackerFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(IcePackerFormatDescriptor descriptor, byte[] data) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void LevelOption_IsHonored_AndDefaultsStayByteIdentical() {
    var descriptor = new IcePackerFormatDescriptor();
    var data = LevelSensitiveSample();

    var fast = CompressAt(descriptor, data, "Fast");
    var normal = CompressAt(descriptor, data, "Normal");
    var best = CompressAt(descriptor, data, "Best");

    Assert.Multiple(() => {
      Assert.That(normal, Is.EqualTo(CompressDefault(descriptor, data)),
        "Normal must preserve the historical 64-candidate default byte-for-byte");
      Assert.That(fast, Is.Not.EqualTo(best), "the level axis must genuinely alter the parse");
      Assert.That(best.Length, Is.LessThan(fast.Length),
        "the deeper search should find the smaller parse on the level-sensitive corpus");
      Assert.That(Decompress(descriptor, fast), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, normal), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, best), Is.EqualTo(data));
    });
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void Optimizer_FindsSmallestDeclaredLevel_AndRoundTrips() {
    var descriptor = new IcePackerFormatDescriptor();
    var data = LevelSensitiveSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("Level"));
      Assert.That(result.Probes, Is.EqualTo(3), "all three declared ICE levels form a tiny exhaustive search space");
      Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    });

    foreach (var level in descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!) {
      var candidate = CompressAt(descriptor, data, level);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.Length),
        $"optimizer result must be no larger than Level={level}");
    }
  }
}

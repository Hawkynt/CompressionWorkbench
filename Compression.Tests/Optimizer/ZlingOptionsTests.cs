using Compression.Lib;
using Compression.Registry;
using FileFormat.Zling;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Zling exposes the five effort levels defined by libzling. The managed codec
/// maps those levels onto progressively deeper ROLZ candidate searches; the
/// generic optimizer must try all five and keep the smallest round-tripping
/// stream for the caller's payload.
/// </summary>
[TestFixture]
public class ZlingOptionsTests {

  private static byte[] DeepMatchSample() =>
    "A0000000000000000A1111111111111111Aabcdefghijklmnopqrstuvwxyz0123456789Aabcdefghijklmnopqrstuvwxyz0123456789"u8.ToArray();

  private static byte[] CompressibleSample() {
    using var ms = new MemoryStream();
    var phrases = new[] {
      "the quick brown fox jumps over the lazy dog ",
      "compression workbench zling optimizer ",
      "reduced offset lempel ziv huffman coding ",
    };
    var rng = new Random(73421);
    for (var i = 0; i < 5000; ++i)
      ms.Write(System.Text.Encoding.ASCII.GetBytes(phrases[rng.Next(phrases.Length)]));
    return ms.ToArray();
  }

  private static byte[] CompressAt(ZlingFormatDescriptor descriptor, byte[] data, string level) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["Level"] = level },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(ZlingFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void Descriptor_ExposesAllFiveZlingLevels() {
    var descriptor = new ZlingFormatDescriptor();
    var level = descriptor.OptionsSchema.Single(option => option.Key == "Level");

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
      Assert.That(level.Kind, Is.EqualTo(FormatOptionKind.Integer));
      Assert.That(level.Default, Is.EqualTo("4"));
      Assert.That(level.AllowedValues, Is.EqualTo(new[] { "0", "1", "2", "3", "4" }));
    });
  }

  [Test, Category("Spec")]
  public void EveryLevel_RoundTrips_AndLevelChangesTheSearch() {
    var descriptor = new ZlingFormatDescriptor();
    var data = DeepMatchSample();
    var outputs = descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!
      .ToDictionary(level => level, level => CompressAt(descriptor, data, level));

    Assert.Multiple(() => {
      foreach (var (level, compressed) in outputs)
        Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data), $"level {level} must round-trip");

      Assert.That(outputs["4"], Is.Not.EqualTo(outputs["0"]),
        "the deeper search must be observable on data whose useful match lies beyond level 0's search depth");
    });
  }

  [Test, Category("Regression")]
  public void DefaultCompression_PreservesHistoricalMaximumDepth() {
    var data = DeepMatchSample();
    using var input = new MemoryStream(data);
    using var defaultOutput = new MemoryStream();
    ZlingStream.Compress(input, defaultOutput);

    using var explicitInput = new MemoryStream(data);
    using var levelFourOutput = new MemoryStream();
    ZlingStream.Compress(explicitInput, levelFourOutput, 4);

    Assert.That(defaultOutput.ToArray(), Is.EqualTo(levelFourOutput.ToArray()));
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallestLevel_AndRoundTrips() {
    var descriptor = new ZlingFormatDescriptor();
    var data = CompressibleSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("Level"));
      Assert.That(result.Parameters["Level"], Is.AnyOf("0", "1", "2", "3", "4"));
      Assert.That(result.Probes, Is.EqualTo(5), "the single five-value axis should be searched exhaustively");
      Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
    });

    foreach (var level in descriptor.OptionsSchema.Single(option => option.Key == "Level").AllowedValues!) {
      var candidate = CompressAt(descriptor, data, level);
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.Length),
        $"optimizer result must be no larger than level {level}");
    }
  }
}

using Compression.Lib;
using Compression.Registry;
using FileFormat.Squeeze;

namespace Compression.Tests.Optimizer;

/// <summary>
/// Verifies that SQ's RLE threshold is a real compression axis and that the generic
/// schema-driven optimizer finds the smallest representation on the caller's data.
/// </summary>
[TestFixture]
public class SqueezeOptionsTests {

  private static byte[] CompressAt(SqueezeFormatDescriptor descriptor, byte[] data, string minimumRunLength) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["RleMinimumRunLength"] = minimumRunLength,
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(SqueezeFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void RleMinimumRunLength_IsHonored_AndDefaultIsHistorical() {
    var descriptor = new SqueezeFormatDescriptor();
    var data = "AAA"u8.ToArray();

    var historical = CompressAt(descriptor, data, "3");
    var selective = CompressAt(descriptor, data, "4");

    Assert.That(selective, Is.Not.EqualTo(historical));
    Assert.That(selective.Length, Is.LessThan(historical.Length),
      "for one three-byte run, leaving it literal avoids the RLE/count symbols and their Huffman nodes");
    Assert.That(Decompress(descriptor, historical), Is.EqualTo(data));
    Assert.That(Decompress(descriptor, selective), Is.EqualTo(data));

    using var defaultInput = new MemoryStream(data, writable: false);
    using var defaultOutput = new MemoryStream();
    descriptor.Compress(defaultInput, defaultOutput);
    Assert.That(defaultOutput.ToArray(), Is.EqualTo(historical),
      "the no-options writer must retain the historical three-byte RLE threshold");
  }

  [Test, Category("Spec")]
  public void Optimizer_FindsSmallestThreshold_AndRoundTrips() {
    var descriptor = new SqueezeFormatDescriptor();
    var data = "AAA"u8.ToArray();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.That(result.Parameters["RleMinimumRunLength"], Is.EqualTo("4"),
      "all thresholds >= 4 tie on this payload; exhaustive search keeps the first smallest one");
    Assert.That(result.Probes, Is.EqualTo(254), "every legal threshold 3..256 is enumerable and exhaustively searched");
    Assert.That(result.CompressedSize, Is.LessThan(CompressAt(descriptor, data, "3").Length));
    Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data));
  }

  [Test, Category("Spec")]
  public void EveryDeclaredThreshold_RoundTrips() {
    var descriptor = new SqueezeFormatDescriptor();
    var data = Enumerable.Repeat("AAABBBBCCCCCDDDDDD\u0090", 32)
      .SelectMany(static text => System.Text.Encoding.Latin1.GetBytes(text))
      .ToArray();
    var thresholds = descriptor.OptionsSchema.Single(option => option.Key == "RleMinimumRunLength").AllowedValues!;

    foreach (var threshold in thresholds) {
      var compressed = CompressAt(descriptor, data, threshold);
      Assert.That(Decompress(descriptor, compressed), Is.EqualTo(data), $"threshold {threshold}");
    }
  }
}

using Compression.Lib;
using Compression.Registry;
using FileFormat.Lzham;

namespace Compression.Tests.Optimizer;

/// <summary>
/// LZHAM exposes encoder-only match-finder limits through its option schema.
/// The settings must affect compression, every candidate must remain decodable,
/// and the generic optimizer must exhaustively keep the smallest of them.
/// </summary>
[TestFixture]
public class LzhamOptionsTests {

  private static byte[] DistantRepeatSample() {
    var random = new Random(0x1A2B3C);
    var block = new byte[6000];
    var spacer = new byte[1024];
    random.NextBytes(block);
    random.NextBytes(spacer);

    using var output = new MemoryStream(block.Length * 2 + spacer.Length);
    output.Write(block);
    output.Write(spacer);
    output.Write(block);
    return output.ToArray();
  }

  private static byte[] CompressAt(LzhamFormatDescriptor descriptor, byte[] data, string windowSize, string searchDepth) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["WindowSize"] = windowSize,
        ["SearchDepth"] = searchDepth,
      },
    });
    return output.ToArray();
  }

  private static byte[] Decompress(LzhamFormatDescriptor descriptor, byte[] compressed) {
    using var input = new MemoryStream(compressed, writable: false);
    using var output = new MemoryStream();
    descriptor.Decompress(input, output);
    return output.ToArray();
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void WindowSize_IsHonoured_AndBothOutputsRoundTrip() {
    var descriptor = new LzhamFormatDescriptor();
    var data = DistantRepeatSample();

    var narrow = CompressAt(descriptor, data, "4096", "64");
    var wide = CompressAt(descriptor, data, "32768", "64");

    Assert.Multiple(() => {
      Assert.That(wide.Length, Is.LessThan(narrow.Length),
        "the 32 KiB window should find the repeated block more than 4 KiB behind");
      Assert.That(Decompress(descriptor, narrow), Is.EqualTo(data), "narrow-window output round-trips");
      Assert.That(Decompress(descriptor, wide), Is.EqualTo(data), "wide-window output round-trips");
    });
  }

  [Test, Category("Spec"), Category("RoundTrip")]
  public void Optimizer_ExhaustsSchema_AndReturnsTheSmallestRoundTrippingCandidate() {
    var descriptor = new LzhamFormatDescriptor();
    var data = DistantRepeatSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("WindowSize"));
      Assert.That(result.Parameters, Does.ContainKey("SearchDepth"));
      Assert.That(result.Probes, Is.EqualTo(24), "4 windows x 6 search depths should be exhaustive");
      Assert.That(result.OriginalSize, Is.EqualTo(data.Length));
      Assert.That(result.Ratio, Is.LessThan(1.0));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data), "optimized output round-trips");
    });

    var windows = descriptor.OptionsSchema.Single(o => o.Key == "WindowSize").AllowedValues!;
    var depths = descriptor.OptionsSchema.Single(o => o.Key == "SearchDepth").AllowedValues!;
    foreach (var window in windows)
      foreach (var depth in depths) {
        var candidate = CompressAt(descriptor, data, window, depth);
        Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.LongLength),
          $"optimizer result must be no larger than WindowSize={window}, SearchDepth={depth}");
      }
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesOptimization_AndDefaultsMatchHistoricalSettings() {
    var descriptor = new LzhamFormatDescriptor();
    var data = "LZHAM optimizer defaults should remain stable across the schema path."u8.ToArray();

    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output);
    var defaultBytes = output.ToArray();
    var explicitBytes = CompressAt(descriptor, data, "32768", "64");

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
      Assert.That(defaultBytes, Is.EqualTo(explicitBytes), "schema defaults preserve the historical encoder settings");
    });
  }
}

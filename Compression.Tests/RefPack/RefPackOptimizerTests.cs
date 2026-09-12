using Compression.Lib;
using Compression.Registry;
using FileFormat.RefPack;

namespace Compression.Tests.RefPack;

/// <summary>
/// RefPack/QFS optimizer coverage. The known-answer stream follows the format
/// grammar published with EA's original RefPack source; optimizer tests stay on
/// the repository's independent managed encoder and use no external code.
/// </summary>
[TestFixture]
public sealed class RefPackOptimizerTests {

  private static byte[] RepeatedDistantBlock() {
    var block = new byte[4096];
    new Random(0x51F5).NextBytes(block);
    var data = new byte[block.Length * 2];
    block.CopyTo(data, 0);
    block.CopyTo(data, block.Length);
    return data;
  }

  private static byte[] Compress(
      RefPackFormatDescriptor descriptor,
      byte[] data,
      string windowSize,
      string searchDepth,
      bool quick) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["WindowSize"] = windowSize,
        ["SearchDepth"] = searchDepth,
        ["Quick"] = quick.ToString(),
      },
    });
    return output.ToArray();
  }

  [Test, Category("Spec")]
  public void KnownAnswer_FourLiteralBytes_MatchesEaRefPackGrammar() {
    byte[] encoded = [
      0x10, 0xFB, 0x00, 0x00, 0x04,
      0xE0, (byte)'A', (byte)'B', (byte)'C', (byte)'D',
      0xFC,
    ];
    var raw = "ABCD"u8.ToArray();

    Assert.Multiple(() => {
      Assert.That(RefPackStream.Decompress(encoded), Is.EqualTo(raw));
      Assert.That(RefPackStream.Compress(raw), Is.EqualTo(encoded));
    });
  }

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesFiniteOptimizerSchema() {
    var descriptor = new RefPackFormatDescriptor();
    var schema = ((IFormatOptionsSchema)descriptor).OptionsSchema;

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
      Assert.That(schema.Select(static option => option.Key),
        Is.EqualTo(new[] { "WindowSize", "SearchDepth", "Quick" }));
      Assert.That(schema.Single(static option => option.Key == "WindowSize").AllowedValues,
        Is.EqualTo(new[] { "1024", "16384", "131072" }));
      Assert.That(schema.Single(static option => option.Key == "SearchDepth").AllowedValues,
        Is.EqualTo(new[] { "16", "64", "128", "512" }));
    });
  }

  [Test, Category("RoundTrip")]
  public void EveryOptimizerCombination_RoundTrips() {
    var descriptor = new RefPackFormatDescriptor();
    var data = RepeatedDistantBlock();

    foreach (var window in new[] { "1024", "16384", "131072" })
      foreach (var depth in new[] { "16", "64", "128", "512" })
        foreach (var quick in new[] { true, false }) {
          var encoded = Compress(descriptor, data, window, depth, quick);
          Assert.That(RefPackStream.Decompress(encoded), Is.EqualTo(data),
            $"window={window}, depth={depth}, quick={quick}");
        }
  }

  [Test, Category("Spec")]
  public void WindowOption_ChangesSearchReach_AndCanImproveRatio() {
    var descriptor = new RefPackFormatDescriptor();
    var data = RepeatedDistantBlock();

    var shortWindow = Compress(descriptor, data, "1024", "512", quick: false);
    var fullWindow = Compress(descriptor, data, "131072", "512", quick: false);

    Assert.Multiple(() => {
      Assert.That(fullWindow.Length, Is.LessThan(shortWindow.Length),
        "the second 4 KiB random block is outside a 1 KiB history but inside the full RefPack window");
      Assert.That(RefPackStream.Decompress(shortWindow), Is.EqualTo(data));
      Assert.That(RefPackStream.Decompress(fullWindow), Is.EqualTo(data));
    });
  }

  [Test, Category("Spec")]
  public void GenericOptimizer_ExhaustsAllTwentyFourSettings_AndFindsMinimum() {
    var descriptor = new RefPackFormatDescriptor();
    var data = RepeatedDistantBlock();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);

    Assert.Multiple(() => {
      Assert.That(result.Probes, Is.EqualTo(24));
      Assert.That(result.Parameters.Keys,
        Is.EquivalentTo(new[] { "WindowSize", "SearchDepth", "Quick" }));
      Assert.That(RefPackStream.Decompress(result.Bytes), Is.EqualTo(data));
    });

    foreach (var window in new[] { "1024", "16384", "131072" })
      foreach (var depth in new[] { "16", "64", "128", "512" })
        foreach (var quick in new[] { true, false }) {
          var candidate = Compress(descriptor, data, window, depth, quick);
          Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(candidate.LongLength),
            $"optimizer missed window={window}, depth={depth}, quick={quick}");
        }
  }

  [Test, Category("RoundTrip")]
  public void ArchiveOperations_Optimize_ReencodesQfsThroughSchemaSearch() {
    var descriptor = new RefPackFormatDescriptor();
    var data = RepeatedDistantBlock();
    var weak = Compress(descriptor, data, "1024", "16", quick: true);
    var directory = Path.Combine(Path.GetTempPath(), $"cwb_refpack_opt_{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);

    try {
      var inputPath = Path.Combine(directory, "weak.qfs");
      var outputPath = Path.Combine(directory, "optimized.qfs");
      File.WriteAllBytes(inputPath, weak);

      var (originalSize, optimizedSize, entriesOptimized) = ArchiveOperations.Optimize(inputPath, outputPath, password: null);
      var optimized = File.ReadAllBytes(outputPath);

      Assert.Multiple(() => {
        Assert.That(originalSize, Is.EqualTo(weak.LongLength));
        Assert.That(optimizedSize, Is.LessThan(originalSize));
        Assert.That(entriesOptimized, Is.EqualTo(1));
        Assert.That(RefPackStream.Decompress(optimized), Is.EqualTo(data));
      });
    } finally {
      Directory.Delete(directory, recursive: true);
    }
  }
}

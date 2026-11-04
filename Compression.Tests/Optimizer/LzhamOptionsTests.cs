using Compression.Lib;
using Compression.Registry;
using FileFormat.Lzham;

namespace Compression.Tests.Optimizer;

/// <summary>
/// LZHAM exposes its one recorded stream parameter, the dictionary size. Every choice must be
/// written into the LZH0 header, decode again, and be searched by the generic optimizer.
/// </summary>
[TestFixture]
public class LzhamOptionsTests {

  private static byte[] DistantRepeatSample() {
    var random = new Random(0x1A2B3C);
    var block = new byte[6000];
    var spacer = new byte[40000];
    random.NextBytes(block);
    random.NextBytes(spacer);

    using var output = new MemoryStream(block.Length * 2 + spacer.Length);
    output.Write(block);
    output.Write(spacer);
    output.Write(block);
    return output.ToArray();
  }

  private static byte[] CompressAt(LzhamFormatDescriptor descriptor, byte[] data, string dictionarySize) {
    using var input = new MemoryStream(data, writable: false);
    using var output = new MemoryStream();
    descriptor.Compress(input, output, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["DictionarySize"] = dictionarySize },
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
  public void GivenTwoDictionarySizes_WhenCompressed_ThenTheHeaderRecordsEachAndOnlyTheLargerReachesTheRepeat() {
    var descriptor = new LzhamFormatDescriptor();
    var data = DistantRepeatSample();

    var narrow = CompressAt(descriptor, data, "15");
    var wide = CompressAt(descriptor, data, "16");

    Assert.Multiple(() => {
      Assert.That(narrow[4], Is.EqualTo(15));
      Assert.That(wide[4], Is.EqualTo(16));
      Assert.That(wide.Length, Is.LessThan(narrow.Length), "only a 64 KiB dictionary reaches the block 46 KB back");
      Assert.That(Decompress(descriptor, narrow), Is.EqualTo(data));
      Assert.That(Decompress(descriptor, wide), Is.EqualTo(data));
    });
  }

  [Test, Category("Spec")]
  public void GivenAuto_WhenCompressed_ThenTheSmallestCoveringDictionaryIsRecorded()
    => Assert.That(CompressAt(new LzhamFormatDescriptor(), new byte[70000], "auto")[4], Is.EqualTo(17));

  [Test, Category("Spec"), Category("RoundTrip")]
  public void Optimizer_ExhaustsSchema_AndReturnsTheSmallestRoundTrippingCandidate() {
    var descriptor = new LzhamFormatDescriptor();
    var data = DistantRepeatSample();

    var result = CompressionOptimizer.OptimizeStream(data, descriptor, descriptor);
    var candidates = descriptor.OptionsSchema.Single().AllowedValues!;

    Assert.Multiple(() => {
      Assert.That(result.Parameters, Does.ContainKey("DictionarySize"));
      Assert.That(result.Probes, Is.EqualTo(candidates.Count));
      Assert.That(Decompress(descriptor, result.Bytes), Is.EqualTo(data), "optimized output round-trips");
    });

    foreach (var candidate in candidates)
      Assert.That(result.CompressedSize, Is.LessThanOrEqualTo(CompressAt(descriptor, data, candidate).Length));
  }
}

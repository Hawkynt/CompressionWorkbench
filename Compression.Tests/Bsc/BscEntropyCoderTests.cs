using Compression.Registry;
using FileFormat.Bsc;

namespace Compression.Tests.Bsc;

/// <summary>
/// Every libbsc QLFC coder has to survive its own output. The adaptive coder in
/// particular mixes three predictors per bit, and a mixer selected differently on
/// the encode and decode sides desynchronises the range coder rather than merely
/// costing ratio, so each coder is round-tripped on its own.
/// </summary>
public class BscEntropyCoderTests {

  [TestCase("Fast")]
  [TestCase("Static")]
  [TestCase("Adaptive")]
  public void EveryQlfcCoder_RoundTrips(string entropyCoder) {
    var data = SkewedSample();
    var descriptor = new BscFormatDescriptor();
    var options = new FormatCreateOptions();
    options.FormatSpecific["BlockSize"] = "65536";
    options.FormatSpecific["SortingContexts"] = "Following";
    options.FormatSpecific["EntropyCoder"] = entropyCoder;

    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    descriptor.Compress(input, compressed, options);

    compressed.Position = 0;
    using var restored = new MemoryStream();
    descriptor.Decompress(compressed, restored);

    Assert.That(restored.ToArray(), Is.EqualTo(data));
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  // Long runs plus sprinkled noise drive the rank and run exponent paths past a
  // single bit, which is where the exponent mixer index actually varies.
  private static byte[] SkewedSample() {
    const string pattern = "abracadabra the quick brown fox ";
    var data = new byte[40_000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)pattern[i % pattern.Length];
    var random = new Random(1234);
    for (var i = 0; i < 2_000; ++i)
      data[random.Next(data.Length)] = (byte)random.Next(256);
    return data;
  }
}

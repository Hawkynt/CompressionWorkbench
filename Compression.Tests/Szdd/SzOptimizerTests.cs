using Compression.Lib;
using Compression.Registry;
using FileFormat.Szdd;

namespace Compression.Tests.Szdd;

[TestFixture]
public class SzOptimizerTests {
  [Category("Optimizer")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_OverlappingMatch_BeatsGreedyAndRoundTrips() {
    var input = "AAAAA"u8.ToArray();

    var greedy = SzddStream.CompressQBasic(input);
    var optimized = SzOptimizer.Compress(input);

    Assert.That(optimized.Length, Is.EqualTo(16));
    Assert.That(optimized.Length, Is.LessThan(greedy.Length));
    Assert.That(optimized.AsSpan(12).ToArray(), Is.EqualTo(new byte[] { 0x01, 0x41, 0xEE, 0xF1 }));
    Assert.That(SzddStream.Decompress(optimized), Is.EqualTo(input));
  }

  [Category("Optimizer")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_MixedCorpus_NeverLargerThanGreedy() {
    var pattern = "ABRACADABRA"u8.ToArray();
    var repeating = new byte[8192];
    for (var i = 0; i < repeating.Length; ++i)
      repeating[i] = pattern[i % pattern.Length];

    var random = new byte[4096];
    new Random(0x535A).NextBytes(random);

    byte[][] samples = [
      [],
      "A"u8.ToArray(),
      "AAABAAAAA"u8.ToArray(),
      "plain text without much repetition"u8.ToArray(),
      Enumerable.Repeat((byte)' ', 200).ToArray(),
      repeating,
      random,
    ];

    foreach (var input in samples) {
      var greedy = SzddStream.CompressQBasic(input);
      var optimized = SzOptimizer.Compress(input);

      Assert.That(optimized.Length, Is.LessThanOrEqualTo(greedy.Length), $"input length {input.Length}");
      Assert.That(SzddStream.Decompress(optimized), Is.EqualTo(input), $"input length {input.Length}");
    }
  }

  [Category("Optimizer")]
  [Test]
  public void Descriptor_AdvertisesAndRoutesOptimalParser() {
    var descriptor = new SzCompressFormatDescriptor();
    var input = "AAAAA"u8.ToArray();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
    Assert.That(descriptor.OptionsSchema.Single().AllowedValues, Is.EqualTo(new[] { "Greedy", "Optimal" }));

    using var source = new MemoryStream(input);
    using var encoded = new MemoryStream();
    descriptor.Compress(source, encoded, new FormatCreateOptions {
      FormatSpecific = new() { ["Parser"] = "Optimal" },
    });

    Assert.That(encoded.ToArray(), Is.EqualTo(SzOptimizer.Compress(input)));
  }

  [Category("Optimizer")]
  [Test]
  public void CompressionOptimizer_SelectsExactParserWhenItWins() {
    var descriptor = new SzCompressFormatDescriptor();
    var input = "AAAAA"u8.ToArray();

    var result = CompressionOptimizer.OptimizeStream(input, descriptor, descriptor);

    Assert.That(result.Parameters["Parser"], Is.EqualTo("Optimal"));
    Assert.That(result.Bytes, Is.EqualTo(SzOptimizer.Compress(input)));
    Assert.That(result.Probes, Is.EqualTo(2));
  }

  [Category("Optimizer")]
  [Category("EdgeCase")]
  [Test]
  public void Compress_Empty_ProducesHeaderOnlyStream() {
    var optimized = SzOptimizer.Compress([]);

    Assert.That(optimized.Length, Is.EqualTo(12));
    Assert.That(SzddStream.Decompress(optimized), Is.Empty);
  }
}

using Compression.Registry;
using FileFormat.Szdd;

namespace Compression.Tests.Szdd;

[TestFixture]
public sealed class SzCompressOptimizerTests {
  [Test]
  [Category("EdgeCase")]
  public void EmptyInput_EmitsOnlyLegacyHeader() {
    var encoded = SzCompressOptimizer.Compress([]);

    Assert.Multiple(() => {
      Assert.That(encoded.Length, Is.EqualTo(12));
      Assert.That(encoded.AsSpan(0, 8).ToArray(), Is.EqualTo(new byte[] { 0x53, 0x5A, 0x20, 0x88, 0xF0, 0x27, 0x33, 0xD1 }));
      Assert.That(SzddStream.Decompress(encoded), Is.Empty);
    });
  }

  [Test]
  [Category("Optimization")]
  [Category("Regression")]
  public void DynamicParse_BeatsGreedy_OnControlByteParserTrap() {
    var input = "DABCCDBBDBCCCBBCCBBC"u8.ToArray();

    var greedy = SzddStream.CompressQBasic(input);
    var optimized = SzCompressOptimizer.Compress(input);

    Assert.Multiple(() => {
      Assert.That(greedy.Length, Is.EqualTo(31));
      Assert.That(optimized.Length, Is.EqualTo(30));
      Assert.That(optimized.Length, Is.LessThan(greedy.Length));
      Assert.That(SzddStream.Decompress(optimized), Is.EqualTo(input));
    });
  }

  [Test]
  [Category("Optimization")]
  [Category("Boundary")]
  public void LeadingSpaces_UseOverlappingInitialWindowMatch() {
    var input = Enumerable.Repeat((byte)' ', 18).ToArray();

    var optimized = SzCompressOptimizer.Compress(input);

    Assert.Multiple(() => {
      Assert.That(optimized.AsSpan(12).ToArray(), Is.EqualTo(new byte[] { 0x00, 0xED, 0xFF }));
      Assert.That(SzddStream.Decompress(optimized), Is.EqualTo(input));
    });
  }

  [Test]
  [Category("Optimization")]
  [Category("RoundTrip")]
  public void RepresentativeCorpus_NeverExceedsGreedy_AndAlwaysRoundTrips() {
    var random = new byte[8192];
    new Random(0x534A).NextBytes(random);

    var pattern = "Microsoft COMPRESS.EXE / QBasic / SZ LZSS\r\n"u8.ToArray();
    var repetitive = new byte[32 * 1024];
    for (var i = 0; i < repetitive.Length; ++i)
      repetitive[i] = pattern[i % pattern.Length];

    byte[][] corpus = [
      "DABCCDBBDBCCCBBCCBBC"u8.ToArray(),
      Enumerable.Repeat((byte)' ', 4096).ToArray(),
      repetitive,
      random,
    ];

    Assert.Multiple(() => {
      foreach (var input in corpus) {
        var greedy = SzddStream.CompressQBasic(input);
        var optimized = SzCompressOptimizer.Compress(input);
        Assert.That(optimized.Length, Is.LessThanOrEqualTo(greedy.Length), $"input length {input.Length}");
        Assert.That(SzddStream.Decompress(optimized), Is.EqualTo(input), $"round-trip for input length {input.Length}");
      }
    });
  }

  [Test]
  [Category("Registry")]
  [Category("Optimization")]
  public void Descriptor_AdvertisesAndDispatchesOptimalCompression() {
    var descriptor = new SzCompressFormatDescriptor();
    var input = "DABCCDBBDBCCCBBCCBBC"u8.ToArray();

    using var source = new MemoryStream(input, writable: false);
    using var encoded = new MemoryStream();
    descriptor.CompressOptimal(source, encoded);

    Assert.Multiple(() => {
      Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
      Assert.That(encoded.Length, Is.EqualTo(30));
      Assert.That(SzddStream.Decompress(encoded.ToArray()), Is.EqualTo(input));
    });
  }
}

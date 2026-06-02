using System.Text;
using Compression.Core.Dictionary.MsLzh;

namespace Compression.Tests.MsLzh;

[TestFixture]
public class MsLzhRoundTripTests {

  [Test, Category("HappyPath")]
  public void RoundTrip_Empty() {
    var compressor = new MsLzhCompressor();
    var decompressor = new MsLzhDecompressor();
    var data = Array.Empty<byte>();
    var compressed = compressor.Compress(data);
    var decompressed = decompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleByte() {
    var compressor = new MsLzhCompressor();
    var decompressor = new MsLzhDecompressor();
    var data = new byte[] { 0x42 };
    var compressed = compressor.Compress(data);
    var decompressed = decompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_AsciiText() {
    var compressor = new MsLzhCompressor();
    var decompressor = new MsLzhDecompressor();
    var data = "The quick brown fox jumps over the lazy dog. The quick brown fox."u8.ToArray();
    var compressed = compressor.Compress(data);
    var decompressed = decompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_AllZeros() {
    var compressor = new MsLzhCompressor();
    var decompressor = new MsLzhDecompressor();
    var data = new byte[1024];
    var compressed = compressor.Compress(data);
    var decompressed = decompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_AllRandom() {
    var compressor = new MsLzhCompressor();
    var decompressor = new MsLzhDecompressor();
    var data = new byte[2048];
    new Random(424242).NextBytes(data);
    var compressed = compressor.Compress(data);
    var decompressed = decompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void CompressibleData_ShrinksBelowOriginal() {
    var compressor = new MsLzhCompressor();
    // Highly redundant input: 32 KB of repeated text. Must compress smaller
    // than 4-byte header + original size.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "DriveSpace 3 Microsoft Plus! Pack Windows 95. ", 700)));
    var compressed = compressor.Compress(text);
    Assert.That(compressed.Length, Is.LessThan(text.Length),
      "Highly redundant text must compress below its original size.");
    var decompressed = new MsLzhDecompressor().Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(text));
  }

  [Test, Category("HappyPath")]
  public void BuildingBlock_RoundTrips() {
    var bb = new MsLzhBuildingBlock();
    Assert.That(bb.Id, Is.EqualTo("BB_MsLzh"));
    Assert.That(bb.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Dictionary));
    var data = "Hello, MS LZH building block!"u8.ToArray();
    var roundTripped = bb.Decompress(bb.Compress(data));
    Assert.That(roundTripped, Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_TooSmall_Throws() {
    var decompressor = new MsLzhDecompressor();
    Assert.Throws<InvalidDataException>(() => decompressor.Decompress(new byte[2]));
  }

  [Test, Category("ErrorHandling")]
  public void Decompress_NegativeSize_Throws() {
    var decompressor = new MsLzhDecompressor();
    var bad = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };
    Assert.Throws<InvalidDataException>(() => decompressor.Decompress(bad));
  }

  // =========================================================================
  //                         Effort-tier tests
  // =========================================================================

  /// <summary>
  /// Round-trip invariant must hold at every effort level for random, all-zeros,
  /// and ASCII text inputs — the decoder is effort-agnostic, so encoder output
  /// at any tier must decode back to the original bytes.
  /// </summary>
  [TestCase(0, Category = "HappyPath")]
  [TestCase(1, Category = "HappyPath")]
  [TestCase(2, Category = "HappyPath")]
  public void Compress_EffortTiers_RoundTripIdentical_AtAllLevels(int effort) {
    var compressor = new MsLzhCompressor();
    var decompressor = new MsLzhDecompressor();

    // Random bytes — incompressible, exercises the literal path.
    var random = new byte[2048];
    new Random(0xCAFE * (effort + 1)).NextBytes(random);
    Assert.That(decompressor.Decompress(compressor.Compress(random, effort)),
      Is.EqualTo(random), $"effort {effort}: random round-trip mismatch");

    // All-zeros — exercises the long-match path.
    var zeros = new byte[4096];
    Assert.That(decompressor.Decompress(compressor.Compress(zeros, effort)),
      Is.EqualTo(zeros), $"effort {effort}: all-zeros round-trip mismatch");

    // ASCII text — mixed literal + short-match path.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "The quick brown fox jumps over the lazy dog. ", 200)));
    Assert.That(decompressor.Decompress(compressor.Compress(text, effort)),
      Is.EqualTo(text), $"effort {effort}: ASCII text round-trip mismatch");
  }

  /// <summary>
  /// Higher effort must never produce a larger output than lower effort on a
  /// large compressible input. Tested on ~8 KB of redundant ASCII so the
  /// per-pass overhead is amortised — effort 2 always returns at least as
  /// small as the effort-1 baseline (it includes effort 1 as its first pass)
  /// and effort 1's lazy parse must not lose to effort 0's greedy parse on
  /// such input.
  /// </summary>
  [Test, Category("HappyPath")]
  public void Compress_HigherEffort_NotLargerThanLowerEffort() {
    var compressor = new MsLzhCompressor();
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "Microsoft DriveSpace 3 — Windows 95 Plus! Pack compression. ", 200)));
    Assert.That(text.Length, Is.GreaterThanOrEqualTo(8 * 1024),
      "Test input must be at least 8 KB to amortise per-pass overhead.");

    var c0 = compressor.Compress(text, effort: 0);
    var c1 = compressor.Compress(text, effort: 1);
    var c2 = compressor.Compress(text, effort: 2);

    Assert.Multiple(() => {
      // Iterated parse always includes the lazy parse as its first pass.
      Assert.That(c2.Length, Is.LessThanOrEqualTo(c1.Length),
        "effort 2 must not be larger than effort 1");
      // Lazy parse is monotone on compressible input vs greedy.
      Assert.That(c1.Length, Is.LessThanOrEqualTo(c0.Length),
        "effort 1 must not be larger than effort 0 on compressible input");
    });
  }

  /// <summary>
  /// Negative effort values are clamped to 0 — passing effort = -5 must
  /// produce the same output as effort = 0.
  /// </summary>
  [Test, Category("EdgeCase")]
  public void Compress_EffortClampsNegative() {
    var compressor = new MsLzhCompressor();
    var data = "Negative effort clamps to zero."u8.ToArray();
    var zero = compressor.Compress(data, effort: 0);
    var negative = compressor.Compress(data, effort: -5);
    Assert.That(negative, Is.EqualTo(zero),
      "Negative effort must be clamped to effort 0.");
  }
}

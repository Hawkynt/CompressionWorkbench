using System.Text;
using Compression.Core.Dictionary.Lzo;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class Lzo1xTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] input = [];
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, 0);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] input = [0x42];
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallText() {
    var input = Encoding.ASCII.GetBytes("Hello, World!");
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    // Highly repetitive data compresses well with LZO1X
    var input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ABCDEFGHIJ", 100)));
    var compressed = Lzo1xCompressor.Compress(input);
    Assert.That(compressed.Length, Is.LessThan(input.Length), "Repetitive data should compress.");
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var input = new byte[1024];
    rng.NextBytes(input);
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData() {
    // Over one 256 KB LZOP block
    var pattern = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. ");
    var input = new byte[pattern.Length * 7000];
    for (var i = 0; i < 7000; ++i)
      pattern.CopyTo(input, i * pattern.Length);

    var compressed = Lzo1xCompressor.Compress(input);
    Assert.That(compressed.Length, Is.LessThan(input.Length), "Large repetitive data should compress.");
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllSameBytes() {
    // Run-length style data (overlapping copy during decompression)
    var input = new byte[10000];
    Array.Fill(input, (byte)0xAB);
    var compressed = Lzo1xCompressor.Compress(input);
    Assert.That(compressed.Length, Is.LessThan(input.Length), "All-same bytes should compress.");
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_BinaryData() {
    // Data with all 256 byte values
    var input = new byte[256 * 4];
    for (var i = 0; i < 4; ++i)
      for (var b = 0; b < 256; ++b)
        input[i * 256 + b] = (byte)b;

    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LongMatchExtension() {
    // Force a very long match that requires extended match-length encoding (>= 4+15+255 bytes)
    var input = new byte[2000];
    // First 100 bytes are pseudo-random to establish a dictionary entry
    var rng = new Random(99);
    rng.NextBytes(input.AsSpan(0, 50));
    // Repeat the first 50 bytes many times to create very long matches
    for (var i = 50; i < input.Length; i += 50)
      input.AsSpan(0, Math.Min(50, input.Length - i)).CopyTo(input.AsSpan(i));

    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_ReturnsSmallOutputForRepetitiveInput() {
    var input = new byte[65536];
    // All zeros
    var compressed = Lzo1xCompressor.Compress(input);
    // Should compress dramatically
    Assert.That(compressed.Length, Is.LessThan(input.Length / 10));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Best_RoundTrip_PatternData() {
    var input = new byte[5000];
    for (var i = 0; i < input.Length; ++i)
      input[i] = (byte)(i % 17);

    var compressed = Lzo1xCompressor.Compress(input, LzoCompressionLevel.Best);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Best_RoundTrip_RandomData() {
    var rng = new Random(42);
    var input = new byte[10000];
    rng.NextBytes(input);

    var compressed = Lzo1xCompressor.Compress(input, LzoCompressionLevel.Best);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Best_CompressesBetter_ThanFast() {
    var pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    var input = new byte[pattern.Length * 200];
    for (var i = 0; i < 200; ++i)
      Array.Copy(pattern, 0, input, i * pattern.Length, pattern.Length);

    var fast = Lzo1xCompressor.Compress(input);
    var best = Lzo1xCompressor.Compress(input, LzoCompressionLevel.Best);

    Assert.That(best.Length, Is.LessThanOrEqualTo(fast.Length),
      $"Best ({best.Length}) should be <= Fast ({fast.Length})");

    // Both should decompress correctly
    Assert.That(Lzo1xDecompressor.Decompress(fast, input.Length), Is.EqualTo(input));
    Assert.That(Lzo1xDecompressor.Decompress(best, input.Length), Is.EqualTo(input));
  }
}

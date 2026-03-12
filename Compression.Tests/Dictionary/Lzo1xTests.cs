using System.Text;
using Compression.Core.Dictionary.Lzo;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class Lzo1xTests {
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] input = [];
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, 0);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_SingleByte() {
    byte[] input = [0x42];
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_SmallText() {
    var input = Encoding.ASCII.GetBytes("Hello, World!");
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_RepetitiveData() {
    // Highly repetitive data compresses well with LZO1X
    var input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ABCDEFGHIJ", 100)));
    var compressed = Lzo1xCompressor.Compress(input);
    Assert.That(compressed.Length, Is.LessThan(input.Length), "Repetitive data should compress.");
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var input = new byte[1024];
    rng.NextBytes(input);
    var compressed = Lzo1xCompressor.Compress(input);
    var result = Lzo1xDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

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

  [Test]
  public void Compress_ReturnsSmallOutputForRepetitiveInput() {
    var input = new byte[65536];
    // All zeros
    var compressed = Lzo1xCompressor.Compress(input);
    // Should compress dramatically
    Assert.That(compressed.Length, Is.LessThan(input.Length / 10));
  }
}

using System.Text;
using Compression.Core.Dictionary.Xpress;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class XpressTests {
  // -----------------------------------------------------------------------
  // Plain XPRESS
  // -----------------------------------------------------------------------

  [Test]
  public void Plain_RoundTrip_EmptyData() {
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress([]);
    var result = XpressDecompressor.Decompress(compressed, 0);
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void Plain_RoundTrip_SingleByte() {
    byte[] input = [0x42];
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_ShortText() {
    byte[] input = Encoding.ASCII.GetBytes("Hello, World!");
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_RepetitiveData() {
    byte[] input = Encoding.ASCII.GetBytes("ABCABCABCABCABCABCABCABCABC");
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
    // Repetitive data should compress well
    Assert.That(compressed.Length, Is.LessThan(input.Length));
  }

  [Test]
  public void Plain_RoundTrip_AllSameByte() {
    byte[] input = new byte[1024];
    Array.Fill(input, (byte)0xAB);
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_RandomData() {
    var rng = new Random(12345);
    byte[] input = new byte[1000];
    rng.NextBytes(input);
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_RepeatedPhrase() {
    var text = "The quick brown fox jumps over the lazy dog. ";
    byte[] input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, 10)));
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_ExactlyOneFlagGroup() {
    // 32 literals — fills exactly one flag group
    byte[] input = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOPQRSTUVWXYZ123456");
    Assert.That(input.Length, Is.EqualTo(32));
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_MultipleFlagGroups() {
    // 200 bytes of sequential values — crosses multiple 32-item flag group boundaries
    byte[] input = new byte[200];
    for (var i = 0; i < input.Length; ++i)
      input[i] = (byte)(i & 0xFF);
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_LargeData() {
    var rng = new Random(99);
    byte[] input = new byte[32768];
    rng.NextBytes(input);
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_RoundTrip_LongMatch_ExtraLengthByte() {
    // A repeated pattern long enough to require the extra length byte (length > 9)
    byte[] input = new byte[300];
    for (var i = 0; i < input.Length; ++i)
      input[i] = (byte)(i % 13);
    var compressor = new XpressCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Plain_CompressToStream_RoundTrip() {
    byte[] input = Encoding.ASCII.GetBytes("Stream compression test. Stream compression test.");
    var compressor = new XpressCompressor();
    using var ms = new MemoryStream();
    compressor.Compress(input, ms);
    var compressed = ms.ToArray();
    var result = XpressDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  // -----------------------------------------------------------------------
  // XPRESS Huffman
  // -----------------------------------------------------------------------

  [Test]
  public void Huffman_RoundTrip_EmptyData() {
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress([]);
    var result = XpressHuffmanDecompressor.Decompress(compressed, 0);
    Assert.That(result, Is.Empty);
  }

  [Test]
  public void Huffman_RoundTrip_SingleByte() {
    byte[] input = [0x7F];
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_ShortText() {
    byte[] input = Encoding.ASCII.GetBytes("Hello, World!");
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_RepetitiveData() {
    byte[] input = Encoding.ASCII.GetBytes("ABCABCABCABCABCABCABCABCABC");
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_AllSameByte() {
    byte[] input = new byte[512];
    Array.Fill(input, (byte)0x55);
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_RandomData() {
    var rng = new Random(54321);
    byte[] input = new byte[2000];
    rng.NextBytes(input);
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_RepeatedPhrase() {
    var text = "The quick brown fox jumps over the lazy dog. ";
    byte[] input = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(text, 20)));
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_MultipleChunks() {
    // Exceeds one 64 KiB chunk
    var rng = new Random(777);
    byte[] input = new byte[XpressConstants.HuffChunkSize + 1000];
    rng.NextBytes(input);
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_ExactChunkBoundary() {
    // Exactly one full 64 KiB chunk
    byte[] input = new byte[XpressConstants.HuffChunkSize];
    for (var i = 0; i < input.Length; ++i)
      input[i] = (byte)(i * 7 + 3);
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }

  [Test]
  public void Huffman_RoundTrip_LargeRepetitiveData() {
    // Large highly repetitive input — exercises long-match paths
    byte[] input = new byte[16384];
    for (var i = 0; i < input.Length; ++i)
      input[i] = (byte)(i % 7);
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(input);
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
    Assert.That(compressed.Length, Is.LessThan(input.Length));
  }

  [Test]
  public void Huffman_CompressToStream_RoundTrip() {
    byte[] input = Encoding.ASCII.GetBytes(
      "XPRESS Huffman stream test. XPRESS Huffman stream test. XPRESS Huffman stream test.");
    var compressor = new XpressHuffmanCompressor();
    using var ms = new MemoryStream();
    compressor.Compress(input, ms);
    var compressed = ms.ToArray();
    var result = XpressHuffmanDecompressor.Decompress(compressed, input.Length);
    Assert.That(result, Is.EqualTo(input));
  }
}

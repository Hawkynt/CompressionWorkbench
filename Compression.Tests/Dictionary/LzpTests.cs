namespace Compression.Tests.Dictionary;

[TestFixture]
public class LzpTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, LZP! Lempel-Ziv Prediction is great for text data with repeating patterns."u8.ToArray();
    var compressed = Compression.Core.Dictionary.Lzp.LzpCompressor.Compress(data);
    var decompressed = Compression.Core.Dictionary.Lzp.LzpDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[4096];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)("ABCDEFGHIJ"[i % 10]);

    var compressed = Compression.Core.Dictionary.Lzp.LzpCompressor.Compress(data);
    var decompressed = Compression.Core.Dictionary.Lzp.LzpDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RandomData() {
    var data = new byte[2048];
    Random.Shared.NextBytes(data);

    var compressed = Compression.Core.Dictionary.Lzp.LzpCompressor.Compress(data);
    var decompressed = Compression.Core.Dictionary.Lzp.LzpDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_EmptyData() {
    var data = Array.Empty<byte>();
    var compressed = Compression.Core.Dictionary.Lzp.LzpCompressor.Compress(data);
    var decompressed = Compression.Core.Dictionary.Lzp.LzpDecompressor.Decompress(compressed);
    Assert.That(decompressed, Is.Empty);
  }
}

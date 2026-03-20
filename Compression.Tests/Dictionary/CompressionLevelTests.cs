using Compression.Core.Dictionary.Brotli;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.Dictionary.Lzo;
using Compression.Core.Dictionary.Lzx;
using Compression.Core.Dictionary.Xpress;

namespace Compression.Tests.Dictionary;

/// <summary>
/// Tests that compression level enums produce valid output at each level.
/// </summary>
[TestFixture]
public class CompressionLevelTests {
  private static readonly byte[] TestData = "The quick brown fox jumps over the lazy dog. The quick brown fox jumps over the lazy dog again."u8.ToArray();

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(LzmaCompressionLevel.Fast)]
  [TestCase(LzmaCompressionLevel.Normal)]
  [TestCase(LzmaCompressionLevel.Best)]
  public void Lzma_AllLevels_RoundTrip(LzmaCompressionLevel level) {
    using var compressed = new MemoryStream();
    var encoder = new LzmaEncoder(dictionarySize: 1 << 16, level: level);
    encoder.Encode(compressed, TestData);
    var compressedBytes = compressed.ToArray();

    using var decompStream = new MemoryStream(compressedBytes);
    var decoder = new LzmaDecoder(decompStream, encoder.Properties, TestData.Length);
    var result = decoder.Decode();

    Assert.That(result, Is.EqualTo(TestData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(LzmaCompressionLevel.Fast)]
  [TestCase(LzmaCompressionLevel.Normal)]
  [TestCase(LzmaCompressionLevel.Best)]
  public void Lzma2_AllLevels_RoundTrip(LzmaCompressionLevel level) {
    using var compressed = new MemoryStream();
    var encoder = new Lzma2Encoder(dictionarySize: 1 << 16, level: level);
    encoder.Encode(compressed, TestData);
    compressed.Position = 0;

    var decoder = new Lzma2Decoder(compressed, 1 << 16);
    var result = decoder.Decode();

    Assert.That(result, Is.EqualTo(TestData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(LzxCompressionLevel.Fast)]
  [TestCase(LzxCompressionLevel.Normal)]
  [TestCase(LzxCompressionLevel.Best)]
  public void Lzx_AllLevels_RoundTrip(LzxCompressionLevel level) {
    var compressor = new LzxCompressor(windowBits: 15, level: level);
    var compressed = compressor.Compress(TestData);

    using var ms = new MemoryStream(compressed);
    var decompressor = new LzxDecompressor(ms, windowBits: 15);
    var result = decompressor.Decompress(TestData.Length);

    Assert.That(result, Is.EqualTo(TestData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(XpressCompressionLevel.Fast)]
  [TestCase(XpressCompressionLevel.Normal)]
  [TestCase(XpressCompressionLevel.Best)]
  public void Xpress_AllLevels_RoundTrip(XpressCompressionLevel level) {
    var compressor = new XpressCompressor(level);
    var compressed = compressor.Compress(TestData);

    var result = XpressDecompressor.Decompress(compressed, TestData.Length);
    Assert.That(result, Is.EqualTo(TestData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Brotli_Uncompressed_RoundTrip() {
    var compressed = BrotliCompressor.Compress(TestData, BrotliCompressionLevel.Uncompressed);
    var result = BrotliDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(TestData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(BrotliCompressionLevel.Fast)]
  [TestCase(BrotliCompressionLevel.Default)]
  [TestCase(BrotliCompressionLevel.Best)]
  public void Brotli_CompressedLevels_RoundTrip(BrotliCompressionLevel level) {
    var compressed = BrotliCompressor.Compress(TestData, level);
    var result = BrotliDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(TestData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(Lz4CompressionLevel.Fast)]
  [TestCase(Lz4CompressionLevel.Hc)]
  [TestCase(Lz4CompressionLevel.Max)]
  public void Lz4_AllLevels_RoundTrip(Lz4CompressionLevel level) {
    var compressed = Lz4BlockCompressor.Compress(TestData, level);
    var result = new byte[TestData.Length];
    Lz4BlockDecompressor.Decompress(compressed, result);
    Assert.That(result, Is.EqualTo(TestData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(LzoCompressionLevel.Fast)]
  [TestCase(LzoCompressionLevel.Best)]
  public void Lzo_AllLevels_RoundTrip(LzoCompressionLevel level) {
    var compressed = Lzo1xCompressor.Compress(TestData, level);
    var result = Lzo1xDecompressor.Decompress(compressed, TestData.Length);
    Assert.That(result, Is.EqualTo(TestData));
  }
}

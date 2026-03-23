using Compression.Core.Deflate;
using System.IO.Compression;

namespace Compression.Tests.Deflate;

[TestFixture]
public class DeflateCompressorTests {
  /// <summary>
  /// Helper: Decompress using .NET's built-in DeflateStream to verify interop.
  /// </summary>
  private static byte[] DecompressWithSystem(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
    using var output = new MemoryStream();
    ds.CopyTo(output);
    return output.ToArray();
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_None_Empty() {
    byte[] data = [];
    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.None);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_None_SmallData() {
    var data = "Hello, World!"u8.ToArray();
    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.None);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_None_SystemDecompress() {
    var data = "Hello, World!"u8.ToArray();
    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.None);
    var result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_RoundTrip_ShortText(DeflateCompressionLevel level) {
    var data = "Hello, World!"u8.ToArray();
    var compressed = DeflateCompressor.Compress(data, level);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_SystemInterop_ShortText(DeflateCompressionLevel level) {
    var data = "Hello, World!"u8.ToArray();
    var compressed = DeflateCompressor.Compress(data, level);
    var result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_RoundTrip_RepetitiveData(DeflateCompressionLevel level) {
    var pattern = "ABCABCABCABCABC"u8.ToArray();
    var data = new byte[pattern.Length * 100];
    for (var i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var compressed = DeflateCompressor.Compress(data, level);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));

    // Compressed should be smaller for repetitive data
    if (level != DeflateCompressionLevel.None)
      Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_SingleByte() {
    byte[] data = [0x42];
    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_AllZeros(DeflateCompressionLevel level) {
    var data = new byte[1024];
    var compressed = DeflateCompressor.Compress(data, level);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));

    // Highly compressible
    Assert.That(compressed.Length, Is.LessThan(data.Length / 2));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  public void Compress_RandomData(DeflateCompressionLevel level) {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);

    var compressed = DeflateCompressor.Compress(data, level);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  public void Compress_SystemInterop_RandomData(DeflateCompressionLevel level) {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);

    var compressed = DeflateCompressor.Compress(data, level);
    var result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_BinaryData_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;

    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_Streaming() {
    var data = "Hello, World! This is streaming compression."u8.ToArray();
    using var ms = new MemoryStream();
    var compressor = new DeflateCompressor(ms, DeflateCompressionLevel.Fast);

    // Write in chunks
    compressor.Write(data.AsSpan(0, 10));
    compressor.Write(data.AsSpan(10));
    compressor.Finish();

    var compressed = ms.ToArray();
    var result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_Maximum_ProducesSmallerOrEqualOutput() {
    var pattern = "ABCABCABCABCABC"u8.ToArray();
    var data = new byte[pattern.Length * 100];
    for (var i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var bestCompressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Best);
    var maxCompressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);

    Assert.That(maxCompressed.Length, Is.LessThanOrEqualTo(bestCompressed.Length),
      $"Maximum ({maxCompressed.Length}) should be <= Best ({bestCompressed.Length})");
  }

  [Category("ThemVsUs")]
  [Category("RoundTrip")]
  [Test]
  public void Compress_Maximum_SystemInterop() {
    var data = "The quick brown fox jumps over the lazy dog. Repeated for compression."u8.ToArray();
    var compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);
    var result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }
}

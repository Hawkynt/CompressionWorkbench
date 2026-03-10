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

  [Test]
  public void Compress_None_Empty() {
    byte[] data = [];
    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.None);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Compress_None_SmallData() {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.None);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Compress_None_SystemDecompress() {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.None);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_RoundTrip_ShortText(DeflateCompressionLevel level) {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = DeflateCompressor.Compress(data, level);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_SystemInterop_ShortText(DeflateCompressionLevel level) {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = DeflateCompressor.Compress(data, level);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_RoundTrip_RepetitiveData(DeflateCompressionLevel level) {
    byte[] pattern = "ABCABCABCABCABC"u8.ToArray();
    byte[] data = new byte[pattern.Length * 100];
    for (int i = 0; i < 100; i++)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = DeflateCompressor.Compress(data, level);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));

    // Compressed should be smaller for repetitive data
    if (level != DeflateCompressionLevel.None)
      Assert.That(compressed.Length, Is.LessThan(data.Length));
  }

  [Test]
  public void Compress_SingleByte() {
    byte[] data = [0x42];
    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  [TestCase(DeflateCompressionLevel.Best)]
  [TestCase(DeflateCompressionLevel.Maximum)]
  public void Compress_AllZeros(DeflateCompressionLevel level) {
    byte[] data = new byte[1024];
    byte[] compressed = DeflateCompressor.Compress(data, level);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));

    // Highly compressible
    Assert.That(compressed.Length, Is.LessThan(data.Length / 2));
  }

  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  public void Compress_RandomData(DeflateCompressionLevel level) {
    var rng = new Random(42);
    byte[] data = new byte[4096];
    rng.NextBytes(data);

    byte[] compressed = DeflateCompressor.Compress(data, level);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [TestCase(DeflateCompressionLevel.Fast)]
  [TestCase(DeflateCompressionLevel.Default)]
  public void Compress_SystemInterop_RandomData(DeflateCompressionLevel level) {
    var rng = new Random(42);
    byte[] data = new byte[4096];
    rng.NextBytes(data);

    byte[] compressed = DeflateCompressor.Compress(data, level);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Compress_BinaryData_AllByteValues() {
    byte[] data = new byte[256];
    for (int i = 0; i < 256; i++)
      data[i] = (byte)i;

    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Compress_Streaming() {
    byte[] data = "Hello, World! This is streaming compression."u8.ToArray();
    using var ms = new MemoryStream();
    var compressor = new DeflateCompressor(ms, DeflateCompressionLevel.Fast);

    // Write in chunks
    compressor.Write(data.AsSpan(0, 10));
    compressor.Write(data.AsSpan(10));
    compressor.Finish();

    byte[] compressed = ms.ToArray();
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Compress_Maximum_ProducesSmallerOrEqualOutput() {
    byte[] pattern = "ABCABCABCABCABC"u8.ToArray();
    byte[] data = new byte[pattern.Length * 100];
    for (int i = 0; i < 100; i++)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] bestCompressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Best);
    byte[] maxCompressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);

    Assert.That(maxCompressed.Length, Is.LessThanOrEqualTo(bestCompressed.Length),
      $"Maximum ({maxCompressed.Length}) should be <= Best ({bestCompressed.Length})");
  }

  [Test]
  public void Compress_Maximum_SystemInterop() {
    byte[] data = "The quick brown fox jumps over the lazy dog. Repeated for compression."u8.ToArray();
    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }
}

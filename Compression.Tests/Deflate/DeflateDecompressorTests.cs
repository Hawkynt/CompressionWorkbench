using Compression.Core.Deflate;
using System.IO.Compression;

namespace Compression.Tests.Deflate;

[TestFixture]
public class DeflateDecompressorTests {
  /// <summary>
  /// Helper: Compress data using .NET's built-in DeflateStream.
  /// </summary>
  private static byte[] CompressWithSystem(byte[] data, System.IO.Compression.CompressionLevel level = System.IO.Compression.CompressionLevel.Optimal) {
    using var ms = new MemoryStream();
    using (var ds = new DeflateStream(ms, level, leaveOpen: true)) {
      ds.Write(data, 0, data.Length);
    }
    return ms.ToArray();
  }

  [Test]
  public void Decompress_UncompressedBlock() {
    // Build a hand-crafted uncompressed block:
    // BFINAL=1, BTYPE=00, then LEN=5, NLEN=~5, then "Hello"
    byte[] data = [.. "Hello"u8];
    ushort len = (ushort)data.Length;
    ushort nlen = (ushort)(~len);

    using var ms = new MemoryStream();
    // First byte: BFINAL=1 (bit 0), BTYPE=00 (bits 1-2) → 0b001 = 0x01
    ms.WriteByte(0x01);
    // LEN little-endian
    ms.WriteByte((byte)(len & 0xFF));
    ms.WriteByte((byte)(len >> 8));
    // NLEN little-endian
    ms.WriteByte((byte)(nlen & 0xFF));
    ms.WriteByte((byte)(nlen >> 8));
    // Raw data
    ms.Write(data);

    byte[] compressed = ms.ToArray();
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_EmptyData() {
    byte[] data = [];
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_SingleByte() {
    byte[] data = [0x42];
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_ShortText() {
    byte[] data = "Hello, World!"u8.ToArray();
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_RepeatedText() {
    byte[] data = "ABCABCABCABCABCABCABCABCABCABCABCABCABCABCABCABC"u8.ToArray();
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_AllZeros() {
    byte[] data = new byte[1024];
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_AllZeros_Large() {
    byte[] data = new byte[65536];
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_RandomData() {
    var rng = new Random(42);
    byte[] data = new byte[4096];
    rng.NextBytes(data);
    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_LargeRepetitive() {
    // Create highly compressible data with repeated pattern
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 200];
    for (int i = 0; i < 200; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_StaticHuffman_FromSystem() {
    // Use Fastest compression level which tends to use static Huffman
    byte[] data = "Hello"u8.ToArray();
    byte[] compressed = CompressWithSystem(data, System.IO.Compression.CompressionLevel.Fastest);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_DynamicHuffman_FromSystem() {
    // Optimal/SmallestSize tends to produce dynamic Huffman blocks
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 50];
    for (int i = 0; i < 50; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = CompressWithSystem(data, System.IO.Compression.CompressionLevel.SmallestSize);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_BoundarySizes() {
    foreach (int size in new[] { 32767, 32768, 32769, 65536 }) {
      var rng = new Random(size);
      byte[] data = new byte[size];
      rng.NextBytes(data);
      byte[] compressed = CompressWithSystem(data);
      byte[] result = DeflateDecompressor.Decompress(compressed);

      Assert.That(result, Is.EqualTo(data), $"Failed for size {size}");
    }
  }

  [Test]
  public void Decompress_StreamingApi() {
    byte[] data = "Hello, World! This is a test of the streaming decompression API."u8.ToArray();
    byte[] compressed = CompressWithSystem(data);

    using var ms = new MemoryStream(compressed);
    var decompressor = new DeflateDecompressor(ms);
    using var result = new MemoryStream();
    byte[] buffer = new byte[8]; // Small buffer to force multiple reads

    int bytesRead;
    while ((bytesRead = decompressor.Decompress(buffer, 0, buffer.Length)) > 0)
      result.Write(buffer, 0, bytesRead);

    Assert.That(result.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Decompress_NoCompression_FromSystem() {
    // NoCompression should produce uncompressed blocks
    byte[] data = "Test data for no compression."u8.ToArray();
    byte[] compressed = CompressWithSystem(data, System.IO.Compression.CompressionLevel.NoCompression);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Decompress_BinaryData() {
    // Data with all byte values 0-255
    byte[] data = new byte[256];
    for (int i = 0; i < 256; ++i)
      data[i] = (byte)i;

    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }
}

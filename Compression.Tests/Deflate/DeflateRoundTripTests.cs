using Compression.Core.Deflate;
using System.IO.Compression;

namespace Compression.Tests.Deflate;

[TestFixture]
public class DeflateRoundTripTests {
  private static byte[] CompressWithSystem(byte[] data) {
    using var ms = new MemoryStream();
    using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) {
      ds.Write(data, 0, data.Length);
    }
    return ms.ToArray();
  }

  private static byte[] DecompressWithSystem(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var ds = new DeflateStream(ms, CompressionMode.Decompress);
    using var output = new MemoryStream();
    ds.CopyTo(output);
    return output.ToArray();
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(100)]
  [TestCase(1000)]
  [TestCase(32767)]
  [TestCase(32768)]
  [TestCase(32769)]
  [TestCase(65536)]
  public void RoundTrip_VariousSizes_RandomData(int size) {
    var rng = new Random(size + 1);
    byte[] data = new byte[size];
    rng.NextBytes(data);

    foreach (var level in new[] { DeflateCompressionLevel.None, DeflateCompressionLevel.Fast, DeflateCompressionLevel.Default, DeflateCompressionLevel.Maximum }) {
      byte[] compressed = DeflateCompressor.Compress(data, level);
      byte[] result = DeflateDecompressor.Decompress(compressed);
      Assert.That(result, Is.EqualTo(data), $"Round-trip failed for size {size}, level {level}");
    }
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(100)]
  [TestCase(1000)]
  [TestCase(32768)]
  [TestCase(65536)]
  public void CrossInterop_OursToSystem(int size) {
    var rng = new Random(size + 1);
    byte[] data = new byte[size];
    rng.NextBytes(data);

    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data), $"System failed to decompress our output, size {size}");
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(100)]
  [TestCase(1000)]
  [TestCase(32768)]
  [TestCase(65536)]
  public void CrossInterop_SystemToOurs(int size) {
    var rng = new Random(size + 1);
    byte[] data = new byte[size];
    rng.NextBytes(data);

    byte[] compressed = CompressWithSystem(data);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data), $"We failed to decompress system output, size {size}");
  }

  [Test]
  public void RoundTrip_LargeRepetitive() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 500];
    for (int i = 0; i < 500; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));

    // Verify good compression ratio
    Assert.That(compressed.Length, Is.LessThan(data.Length / 10));
  }

  [Test]
  public void RoundTrip_LargeRepetitive_SystemInterop() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 500];
    for (int i = 0; i < 500; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Default);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(100)]
  [TestCase(1000)]
  public void CrossInterop_Maximum_OursToSystem(int size) {
    var rng = new Random(size + 1);
    byte[] data = new byte[size];
    rng.NextBytes(data);

    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);
    byte[] result = DecompressWithSystem(compressed);
    Assert.That(result, Is.EqualTo(data), $"System failed to decompress Maximum output, size {size}");
  }

  [Test]
  public void RoundTrip_Maximum_LargeRepetitive() {
    byte[] pattern = "The quick brown fox jumps over the lazy dog. "u8.ToArray();
    byte[] data = new byte[pattern.Length * 100];
    for (int i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] compressed = DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);
    byte[] result = DeflateDecompressor.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));

    byte[] resultSys = DecompressWithSystem(compressed);
    Assert.That(resultSys, Is.EqualTo(data));
  }
}

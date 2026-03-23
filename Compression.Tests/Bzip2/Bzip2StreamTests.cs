using Compression.Core.Streams;
using FileFormat.Bzip2;

namespace Compression.Tests.Bzip2;

[TestFixture]
public class Bzip2StreamTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] data = [];
    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TextData() {
    var data = "Hello, bzip2 World! Testing the Burrows-Wheeler compression."u8.ToArray();
    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var pattern = "the quick brown fox jumps over the lazy dog. "u8.ToArray();
    var data = new byte[pattern.Length * 100];
    for (var i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_HighlyRepetitive() {
    var data = new byte[4096];

    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData() {
    var rng = new Random(123);
    var data = new byte[51200]; // 50KB
    for (var i = 0; i < data.Length; ++i) {
      if (i % 100 < 50)
        data[i] = (byte)(i % 26 + 'a');
      else
        data[i] = (byte)rng.Next(256);
    }

    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllBlockSizes() {
    var data = "Testing all block sizes."u8.ToArray();

    for (var blockSize = 1; blockSize <= 9; ++blockSize) {
      var compressed = CompressWithOurs(data, blockSize);
      var result = DecompressWithOurs(compressed);
      Assert.That(result, Is.EqualTo(data), $"Failed for blockSize={blockSize}");
    }
  }

  [Category("ThemVsUs")]
  [Test]
  public void StreamHeader_Format() {
    byte[] data = [1, 2, 3];
    var compressed = CompressWithOurs(data, blockSize100k: 9);

    Assert.That(compressed[0], Is.EqualTo((byte)'B'));
    Assert.That(compressed[1], Is.EqualTo((byte)'Z'));
    Assert.That(compressed[2], Is.EqualTo((byte)'h'));
    Assert.That(compressed[3], Is.EqualTo((byte)'9'));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_RepetitiveData_CompressesWell() {
    var data = new byte[4096];
    Array.Fill(data, (byte)'A');

    var compressed = CompressWithOurs(data);
    var ratio = (double)compressed.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.1), $"Compression ratio {ratio:P} too high");
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void BlockCrc_IsCorrect() {
    // Compress known data — this tests that the compressor computes and writes CRCs
    // and that the decompressor verifies them successfully
    var data = "CRC verification test data."u8.ToArray();
    var compressed = CompressWithOurs(data);
    var result = DecompressWithOurs(compressed);
    Assert.That(result, Is.EqualTo(data)); // CRC checked during decompression
  }

  private static byte[] CompressWithOurs(byte[] data, int blockSize100k = 9) {
    using var ms = new MemoryStream();
    using (var bz = new Bzip2Stream(ms, CompressionStreamMode.Compress,
      blockSize100k, leaveOpen: true)) {
      bz.Write(data, 0, data.Length);
    }
    return ms.ToArray();
  }

  private static byte[] DecompressWithOurs(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var bz = new Bzip2Stream(ms, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    bz.CopyTo(output);
    return output.ToArray();
  }
}

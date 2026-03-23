using FileFormat.Zstd;
using Compression.Core.Streams;

namespace Compression.Tests.Zstd;

[TestFixture]
public class ZstdStreamTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] data = [];
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TextData() {
    var data = System.Text.Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "Zstandard compression test data.");
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[5000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 10);
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var data = new byte[1024];
    new Random(42).NextBytes(data);
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData() {
    // > 128KB to force multiple blocks
    var data = new byte[200_000];
    var rng = new Random(123);
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(rng.Next(0, 26) + 'a');
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Test]
  public void FrameHeader_MagicBytes() {
    byte[] data = [1, 2, 3];
    var ms = new MemoryStream();
    using (var zstd = new ZstdStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(data, 0, data.Length);

    ms.Position = 0;
    var magic = (uint)(ms.ReadByte() | (ms.ReadByte() << 8) | (ms.ReadByte() << 16) | (ms.ReadByte() << 24));
    Assert.That(magic, Is.EqualTo(0xFD2FB528u));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void ContentChecksum_Verified() {
    // Compress data with checksum, verify it decompresses correctly
    var data = System.Text.Encoding.UTF8.GetBytes("Checksum verification test data");
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_RepetitiveData_CompressesWell() {
    var data = new byte[10000];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 4);

    var ms = new MemoryStream();
    using (var zstd = new ZstdStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(data, 0, data.Length);

    var ratio = (double)ms.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.5)); // Should compress well
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RepeatOffsets_WorkCorrectly() {
    // Data with recurring patterns at same offsets
    var pattern = System.Text.Encoding.UTF8.GetBytes("ABCDEF");
    var data = new byte[pattern.Length * 100];
    for (var i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RawBlock_Fallback() {
    // Highly random/incompressible data
    var data = new byte[500];
    new Random(999).NextBytes(data);
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllZeros() {
    var data = new byte[4096]; // all zeros
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  private static byte[] CompressDecompress(byte[] data) {
    var compressed = new MemoryStream();
    using (var zstd = new ZstdStream(compressed, CompressionStreamMode.Compress, leaveOpen: true)) {
      if (data.Length > 0)
        zstd.Write(data, 0, data.Length);
    }

    compressed.Position = 0;
    var decompressed = new MemoryStream();
    using (var zstd = new ZstdStream(compressed, CompressionStreamMode.Decompress)) {
      zstd.CopyTo(decompressed);
    }

    return decompressed.ToArray();
  }
}

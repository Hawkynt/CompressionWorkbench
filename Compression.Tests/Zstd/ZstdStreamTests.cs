using FileFormat.Zstd;
using Compression.Core.Streams;

namespace Compression.Tests.Zstd;

[TestFixture]
public class ZstdStreamTests {
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] data = [];
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_TextData() {
    byte[] data = System.Text.Encoding.UTF8.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "Zstandard compression test data.");
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RepetitiveData() {
    byte[] data = new byte[5000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 10);
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_RandomData() {
    byte[] data = new byte[1024];
    new Random(42).NextBytes(data);
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_LargeData() {
    // > 128KB to force multiple blocks
    byte[] data = new byte[200_000];
    var rng = new Random(123);
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(rng.Next(0, 26) + 'a');
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void FrameHeader_MagicBytes() {
    byte[] data = [1, 2, 3];
    var ms = new MemoryStream();
    using (var zstd = new ZstdStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(data, 0, data.Length);

    ms.Position = 0;
    uint magic = (uint)(ms.ReadByte() | (ms.ReadByte() << 8) | (ms.ReadByte() << 16) | (ms.ReadByte() << 24));
    Assert.That(magic, Is.EqualTo(0xFD2FB528u));
  }

  [Test]
  public void ContentChecksum_Verified() {
    // Compress data with checksum, verify it decompresses correctly
    byte[] data = System.Text.Encoding.UTF8.GetBytes("Checksum verification test data");
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void Compress_RepetitiveData_CompressesWell() {
    byte[] data = new byte[10000];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 4);

    var ms = new MemoryStream();
    using (var zstd = new ZstdStream(ms, CompressionStreamMode.Compress, leaveOpen: true))
      zstd.Write(data, 0, data.Length);

    double ratio = (double)ms.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.5)); // Should compress well
  }

  [Test]
  public void RepeatOffsets_WorkCorrectly() {
    // Data with recurring patterns at same offsets
    byte[] pattern = System.Text.Encoding.UTF8.GetBytes("ABCDEF");
    byte[] data = new byte[pattern.Length * 100];
    for (int i = 0; i < 100; i++)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RawBlock_Fallback() {
    // Highly random/incompressible data
    byte[] data = new byte[500];
    new Random(999).NextBytes(data);
    byte[] result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Test]
  public void RoundTrip_AllZeros() {
    byte[] data = new byte[4096]; // all zeros
    byte[] result = CompressDecompress(data);
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

using System.Text;
using FileFormat.Zlib;

namespace Compression.Tests.Zlib;

[TestFixture]
public class ZlibTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    var data = Encoding.UTF8.GetBytes("Hello, zlib world! AAAAAAAAAA");
    var compressed = ZlibStream.Compress(data);
    var result = ZlibStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    var compressed = ZlibStream.Compress([]);
    var result = ZlibStream.Decompress(compressed);
    Assert.That(result, Is.Empty);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeRepetitive() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 251);
    var compressed = ZlibStream.Compress(data);
    var result = ZlibStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_StreamApi() {
    var data = Encoding.UTF8.GetBytes("Stream-based zlib round-trip test data BBBBBBB");

    using var compressedMs = new MemoryStream();
    ZlibStream.Compress(new MemoryStream(data), compressedMs);

    compressedMs.Seek(0, SeekOrigin.Begin);
    using var decompressedMs = new MemoryStream();
    ZlibStream.Decompress(compressedMs, decompressedMs);

    Assert.That(decompressedMs.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var data = new byte[8192];
    rng.NextBytes(data);
    var compressed = ZlibStream.Compress(data);
    var result = ZlibStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_BadChecksum_Throws() {
    var data = Encoding.UTF8.GetBytes("checksum test");
    var compressed = ZlibStream.Compress(data);

    // Corrupt the Adler-32 trailer (last 4 bytes)
    compressed[^1] ^= 0xFF;

    Assert.Throws<InvalidDataException>(() => ZlibStream.Decompress(compressed));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidHeader_Throws() {
    // Two bytes that fail the mod-31 check
    var bad = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    Assert.Throws<InvalidDataException>(() => ZlibStream.Decompress(bad));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_TooShort_Throws() {
    Assert.Throws<InvalidDataException>(() => ZlibStream.Decompress(new byte[] { 0x78 }));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Compress_HeaderValid() {
    var compressed = ZlibStream.Compress("test"u8);
    // CMF=0x78 (method=8, window=15), FLG check: (CMF*256+FLG) % 31 == 0
    Assert.That((compressed[0] * 256 + compressed[1]) % 31, Is.EqualTo(0));
    Assert.That(compressed[0] & 0x0F, Is.EqualTo(8)); // Deflate method
  }
}

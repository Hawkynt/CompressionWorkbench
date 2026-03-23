namespace Compression.Tests.ApLib;

[TestFixture]
public class ApLibTests {

  [Test]
  public void RoundTrip_SimpleText() {
    var original = "Hello, aPLib compression! This is a test of the aPLib algorithm."u8.ToArray();
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_AllByteValues() {
    var original = new byte[256];
    for (var i = 0; i < 256; i++)
      original[i] = (byte)i;
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_RepetitiveData() {
    var original = new byte[2048];
    Array.Fill(original, (byte)'A');
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void Magic_IsAP32() {
    var original = "Test"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FileFormat.ApLib.ApLibStream.Compress(input, compressed);

    var data = compressed.ToArray();
    Assert.That(data.Length, Is.GreaterThanOrEqualTo(24));
    Assert.That(data[0], Is.EqualTo((byte)'A'));
    Assert.That(data[1], Is.EqualTo((byte)'P'));
    Assert.That(data[2], Is.EqualTo((byte)'3'));
    Assert.That(data[3], Is.EqualTo((byte)'2'));
  }

  [Test]
  public void RoundTrip_SingleByte() {
    var original = new byte[] { 0x42 };
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void RoundTrip_LargeData() {
    var rng = new Random(12345);
    var original = new byte[50 * 1024];
    rng.NextBytes(original);
    // Insert some repetitive blocks for compression.
    for (var i = 0; i < 5000; i++)
      original[25000 + i] = (byte)(i % 4);
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test]
  public void Header_HasCorrectCrc() {
    var original = "CRC32 check data"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FileFormat.ApLib.ApLibStream.Compress(input, compressed);

    var data = compressed.ToArray();
    // Read original CRC from header (bytes 16..20, little-endian).
    var headerCrc = (uint)(data[16] | (data[17] << 8) | (data[18] << 16) | (data[19] << 24));
    var expectedCrc = Compression.Core.Checksums.Crc32.Compute(original);
    Assert.That(headerCrc, Is.EqualTo(expectedCrc));
  }

  private static byte[] RoundTrip(byte[] original) {
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FileFormat.ApLib.ApLibStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    FileFormat.ApLib.ApLibStream.Decompress(compressed, output);
    return output.ToArray();
  }
}

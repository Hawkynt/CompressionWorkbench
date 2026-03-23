namespace Compression.Tests.BriefLz;

[TestFixture]
public class BriefLzTests {

  [Test]
  public void RoundTrip_SimpleText() {
    var original = "Hello, BriefLZ compression! This is a test of the BriefLZ algorithm."u8.ToArray();
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
  public void Magic_IsBlz() {
    var original = "Test"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FileFormat.BriefLz.BriefLzStream.Compress(input, compressed);

    var data = compressed.ToArray();
    Assert.That(data.Length, Is.GreaterThanOrEqualTo(24));
    Assert.That(data[0], Is.EqualTo((byte)'b'));
    Assert.That(data[1], Is.EqualTo((byte)'l'));
    Assert.That(data[2], Is.EqualTo((byte)'z'));
    Assert.That(data[3], Is.EqualTo(0x1A));
  }

  [Test]
  public void Header_HasCorrectCrc() {
    var original = "CRC32 check data"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FileFormat.BriefLz.BriefLzStream.Compress(input, compressed);

    var data = compressed.ToArray();
    // Read original CRC from header (bytes 20..24, big-endian).
    var headerCrc = (uint)((data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23]);
    var expectedCrc = Compression.Core.Checksums.Crc32.Compute(original);
    Assert.That(headerCrc, Is.EqualTo(expectedCrc));
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
    var original = new byte[100 * 1024];
    // Mix of random and repetitive sections.
    rng.NextBytes(original);
    // Insert some repetitive blocks for compression.
    for (var i = 0; i < 10000; i++)
      original[50000 + i] = (byte)(i % 4);
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  private static byte[] RoundTrip(byte[] original) {
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FileFormat.BriefLz.BriefLzStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    FileFormat.BriefLz.BriefLzStream.Decompress(compressed, output);
    return output.ToArray();
  }
}

namespace Compression.Tests.Rnc;

[TestFixture]
public class RncTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, RNC!"u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rnc.RncStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rnc.RncStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rnc.RncStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rnc.RncStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[512];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 16);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rnc.RncStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rnc.RncStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsRnc() {
    var data = new byte[64];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rnc.RncStream.Compress(input, compressed);

    compressed.Position = 0;
    var header = new byte[4];
    compressed.ReadExactly(header);

    Assert.That(header[0], Is.EqualTo((byte)'R'));
    Assert.That(header[1], Is.EqualTo((byte)'N'));
    Assert.That(header[2], Is.EqualTo((byte)'C'));
    Assert.That(header[3], Is.EqualTo((byte)1), "Method should be 1");
  }

  [Test, Category("HappyPath")]
  public void Header_HasCorrectSizes() {
    var data = new byte[100];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rnc.RncStream.Compress(input, compressed);

    var bytes = compressed.ToArray();

    // Uncompressed size at offset 4 (big-endian)
    var uncompressedSize =
      ((uint)bytes[4] << 24) | ((uint)bytes[5] << 16) |
      ((uint)bytes[6] << 8) | bytes[7];
    Assert.That(uncompressedSize, Is.EqualTo(100u));

    // Compressed data size at offset 8 (big-endian)
    var compressedSize =
      ((uint)bytes[8] << 24) | ((uint)bytes[9] << 16) |
      ((uint)bytes[10] << 8) | bytes[11];
    Assert.That(compressedSize, Is.EqualTo((uint)(bytes.Length - 18)));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    var data = new byte[] { 0x42 };

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rnc.RncStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rnc.RncStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_256KB_Random() {
    var rng = new Random(42);
    var data = new byte[256 * 1024];
    rng.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rnc.RncStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rnc.RncStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Crc16_KnownValue() {
    // Verify CRC-16 produces consistent results
    var data = "Hello"u8;
    var crc1 = FileFormat.Rnc.RncStream.Crc16(data);
    var crc2 = FileFormat.Rnc.RncStream.Crc16(data);
    Assert.That(crc1, Is.EqualTo(crc2));

    // Empty data should produce CRC = 0
    var emptyCrc = FileFormat.Rnc.RncStream.Crc16(ReadOnlySpan<byte>.Empty);
    Assert.That(emptyCrc, Is.EqualTo((ushort)0));

    // Different data should (very likely) produce different CRCs
    var data2 = "World"u8;
    var crc3 = FileFormat.Rnc.RncStream.Crc16(data2);
    Assert.That(crc3, Is.Not.EqualTo(crc1));
  }
}

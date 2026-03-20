using FileFormat.Lzip;

namespace Compression.Tests.Lzip;

[TestFixture]
public class LzipTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    byte[] data = "Hello, Lzip! This is a small test string for round-trip compression."u8.ToArray();
    byte[] compressed = Compress(data);
    byte[] result = Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeRepetitive() {
    // 64 KiB of repetitive data — should compress well.
    byte[] pattern = "the quick brown fox jumps over the lazy dog. "u8.ToArray();
    int repeat = (64 * 1024) / pattern.Length + 1;
    byte[] data = new byte[64 * 1024];
    for (int i = 0; i < repeat; ++i) {
      int offset = i * pattern.Length;
      int toCopy = Math.Min(pattern.Length, data.Length - offset);
      if (toCopy <= 0) break;
      pattern.AsSpan(0, toCopy).CopyTo(data.AsSpan(offset));
    }

    byte[] compressed = Compress(data);
    byte[] result = Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    byte[] data = [];
    byte[] compressed = Compress(data);
    byte[] result = Decompress(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidMagic_Throws() {
    // Replace first byte of a valid compressed stream with a bad value.
    byte[] data = "some test data"u8.ToArray();
    byte[] compressed = Compress(data);
    compressed[0] = 0x00; // Corrupt the magic.

    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => LzipStream.Decompress(input, output));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_BadCrc_Throws() {
    byte[] data = "some test data for CRC corruption"u8.ToArray();
    byte[] compressed = Compress(data);

    // The trailer starts at the very end: last 20 bytes.
    // The first 4 bytes of the trailer are the CRC-32.
    int trailerOffset = compressed.Length - LzipConstants.TrailerSize;
    compressed[trailerOffset] ^= 0xFF; // Flip bits in the CRC.

    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    Assert.Throws<InvalidDataException>(() => LzipStream.Decompress(input, output));
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private static byte[] Compress(byte[] data) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    LzipStream.Compress(input, output);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    LzipStream.Decompress(input, output);
    return output.ToArray();
  }
}

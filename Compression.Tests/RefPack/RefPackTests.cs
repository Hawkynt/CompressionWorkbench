using FileFormat.RefPack;

namespace Compression.Tests.RefPack;

[TestFixture]
public class RefPackTests {

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SimpleText() {
    var data = "Hello, RefPack compression! This is a test of the RefPack format."u8.ToArray();

    using var compressedStream = new MemoryStream();
    using (var inputStream = new MemoryStream(data))
      RefPackStream.Compress(inputStream, compressedStream);

    compressedStream.Position = 0;
    using var decompressedStream = new MemoryStream();
    RefPackStream.Decompress(compressedStream, decompressedStream);

    Assert.That(decompressedStream.ToArray(), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;

    var compressed = RefPackStream.Compress((ReadOnlySpan<byte>)data);
    var decompressed = RefPackStream.Decompress((ReadOnlySpan<byte>)compressed);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    // Highly repetitive data should compress well
    var data = new byte[4096];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 7);

    var compressed = RefPackStream.Compress((ReadOnlySpan<byte>)data);

    // Verify compression actually reduced size
    Assert.That(compressed.Length, Is.LessThan(data.Length), "Repetitive data should compress");

    var decompressed = RefPackStream.Decompress((ReadOnlySpan<byte>)compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void Header_HasFBSignature() {
    var data = "Test data for header check"u8.ToArray();
    var compressed = RefPackStream.Compress((ReadOnlySpan<byte>)data);

    // 5-byte header: flags(0x10), 0xFB, size[3]
    Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(5));
    Assert.That(compressed[0], Is.EqualTo(0x10), "Flags byte should be 0x10");
    Assert.That(compressed[1], Is.EqualTo(0xFB), "Signature byte should be 0xFB");

    // Verify encoded uncompressed size
    var encodedSize = (compressed[2] << 16) | (compressed[3] << 8) | compressed[4];
    Assert.That(encodedSize, Is.EqualTo(data.Length));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [0x42];

    var compressed = RefPackStream.Compress((ReadOnlySpan<byte>)data);
    var decompressed = RefPackStream.Decompress((ReadOnlySpan<byte>)compressed);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData() {
    // 100 KB of patterned data
    var rng = new Random(12345);
    var data = new byte[100 * 1024];
    rng.NextBytes(data);

    var compressed = RefPackStream.Compress((ReadOnlySpan<byte>)data);
    var decompressed = RefPackStream.Decompress((ReadOnlySpan<byte>)compressed);

    Assert.That(decompressed, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_GameLikeData() {
    // Simulated structured game data with repeating patterns,
    // similar to what EA game files contain
    var rng = new Random(54321);
    var data = new byte[8192];
    var pos = 0;

    while (pos < data.Length) {
      // Write a "record header" pattern (repeating structure)
      var recordType = (byte)rng.Next(4);
      if (pos < data.Length) data[pos++] = 0xDB; // marker
      if (pos < data.Length) data[pos++] = 0xBF; // marker
      if (pos < data.Length) data[pos++] = recordType;
      if (pos < data.Length) data[pos++] = 0x00; // padding

      // Write some semi-random payload with local repetition
      var payloadLen = rng.Next(16, 64);
      var pattern = new byte[rng.Next(3, 8)];
      rng.NextBytes(pattern);

      for (var i = 0; i < payloadLen && pos < data.Length; ++i)
        data[pos++] = pattern[i % pattern.Length];
    }

    var compressed = RefPackStream.Compress((ReadOnlySpan<byte>)data);

    // Structured/repetitive game data should compress
    Assert.That(compressed.Length, Is.LessThan(data.Length), "Game-like structured data should compress");

    var decompressed = RefPackStream.Decompress((ReadOnlySpan<byte>)compressed);
    Assert.That(decompressed, Is.EqualTo(data));
  }
}

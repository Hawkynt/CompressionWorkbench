using FileFormat.Lzfse;

namespace Compression.Tests.Lzfse;

[TestFixture]
public class LzfseTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var original = "Hello, LZFSE compression! This is a test of Apple's LZFSE format."u8.ToArray();
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var original = new byte[4096];
    for (var i = 0; i < original.Length; i++)
      original[i] = (byte)(i % 7);

    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    // Repetitive data should compress well.
    Assert.That(compressed.Length, Is.LessThan(original.Length), "Repetitive data should compress");

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var original = new byte[256];
    for (var i = 0; i < 256; i++)
      original[i] = (byte)i;
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    var original = new byte[] { 0x42 };
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeData() {
    var rng = new Random(12345);
    var original = new byte[100 * 1024];
    rng.NextBytes(original);
    // Insert some repetitive blocks for compression.
    for (var i = 0; i < 10000; i++)
      original[50000 + i] = (byte)(i % 4);
    var decompressed = RoundTrip(original);
    Assert.That(decompressed, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void Magic_HasLzfseBlockMagic() {
    var original = "Test data for magic check with enough content to compress"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    var data = compressed.ToArray();
    // First 4 bytes should be a block magic: either bvxn (0x6E787662) or bvx- (0x2D787662).
    Assert.That(data.Length, Is.GreaterThanOrEqualTo(4));
    var magic = BitConverter.ToUInt32(data, 0);
    Assert.That(magic, Is.AnyOf(0x6E787662u, 0x2D787662u),
      "First block should be LZVN (bvxn) or uncompressed (bvx-)");

    // Stream should end with bvx$ (0x24787662).
    var endMagic = BitConverter.ToUInt32(data, data.Length - 4);
    Assert.That(endMagic, Is.EqualTo(0x24787662u), "Stream should end with bvx$ end-of-stream marker");
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_EmptyInput() {
    var original = Array.Empty<byte>();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  private static byte[] RoundTrip(byte[] original) {
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    LzfseStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    LzfseStream.Decompress(compressed, output);
    return output.ToArray();
  }
}

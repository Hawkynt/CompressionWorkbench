using FileFormat.Freeze;

namespace Compression.Tests.Freeze;

[TestFixture]
public class FreezeTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var original = "Hello, Freeze compression! This is a test of the Freeze 2.0 format."u8.ToArray();
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
    FreezeStream.Compress(input, compressed);

    // Repetitive data should compress well.
    Assert.That(compressed.Length, Is.LessThan(original.Length), "Repetitive data should compress");

    compressed.Position = 0;
    using var output = new MemoryStream();
    FreezeStream.Decompress(compressed, output);
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
  public void Magic_IsFreeze2() {
    var original = "Test data for magic check"u8.ToArray();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FreezeStream.Compress(input, compressed);

    var data = compressed.ToArray();
    // Freeze 2.0 magic: 0x1F 0x9F, followed by 4-byte LE original size.
    Assert.That(data.Length, Is.GreaterThanOrEqualTo(6));
    Assert.That(data[0], Is.EqualTo(0x1F), "First magic byte should be 0x1F");
    Assert.That(data[1], Is.EqualTo(0x9F), "Second magic byte should be 0x9F");

    // Verify encoded uncompressed size (4 bytes LE).
    var encodedSize = BitConverter.ToUInt32(data, 2);
    Assert.That(encodedSize, Is.EqualTo((uint)original.Length));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_EmptyInput() {
    var original = Array.Empty<byte>();
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FreezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    FreezeStream.Decompress(compressed, output);
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }

  private static byte[] RoundTrip(byte[] original) {
    using var input = new MemoryStream(original);
    using var compressed = new MemoryStream();
    FreezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var output = new MemoryStream();
    FreezeStream.Decompress(compressed, output);
    return output.ToArray();
  }
}

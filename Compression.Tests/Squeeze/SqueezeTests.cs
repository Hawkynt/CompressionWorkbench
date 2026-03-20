namespace Compression.Tests.Squeeze;

[TestFixture]
public class SqueezeTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, Squeeze! This is a test of Huffman compression."u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (int i = 0; i < 256; i++)
      data[i] = (byte)i;

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_Is0xFF76() {
    var data = "test"u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x76));
    Assert.That(compressed.ReadByte(), Is.EqualTo(0xFF));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[2048];
    Array.Fill(data, (byte)'A');

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Squeeze.SqueezeStream.Compress(input, compressed);

    // Should compress well
    Assert.That(compressed.Length, Is.LessThan(data.Length));

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Squeeze.SqueezeStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

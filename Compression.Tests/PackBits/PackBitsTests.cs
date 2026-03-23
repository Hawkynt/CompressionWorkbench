namespace Compression.Tests.PackBits;

[TestFixture]
public class PackBitsTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SimpleText() {
    var data = "Hello, PackBits! This is a test of run-length encoding."u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PackBits.PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PackBits.PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++)
      data[i] = (byte)i;

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PackBits.PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PackBits.PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[2048];
    Array.Fill(data, (byte)'A');

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PackBits.PackBitsStream.Compress(input, compressed);

    // Highly repetitive data should compress well.
    Assert.That(compressed.Length, Is.LessThan(data.Length));

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PackBits.PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AlternatingPattern() {
    var data = new byte[1024];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 2 == 0 ? 'A' : 'B');

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PackBits.PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PackBits.PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsPKBT() {
    var data = "test"u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PackBits.PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'P'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'K'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'B'));
    Assert.That(compressed.ReadByte(), Is.EqualTo((int)'T'));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void RoundTrip_SingleByte() {
    var data = new byte[] { 0x42 };

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PackBits.PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PackBits.PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeData() {
    // 100 KB of mixed content: repeating pattern with some variation.
    var data = new byte[100 * 1024];
    var rng = new Random(12345);
    for (var i = 0; i < data.Length; i++) {
      // Mix runs and random bytes.
      if (i % 256 < 64)
        data[i] = 0xAA; // runs of identical bytes
      else
        data[i] = (byte)rng.Next(256);
    }

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PackBits.PackBitsStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PackBits.PackBitsStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

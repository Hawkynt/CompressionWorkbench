namespace Compression.Tests.IcePacker;

[TestFixture]
public class IcePackerTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SmallData() {
    var data = "Hello, ICE Packer! Atari ST demo scene rules!"u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.IcePacker.IcePackerStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.IcePacker.IcePackerStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[4096];
    for (var i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 8);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.IcePacker.IcePackerStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.IcePacker.IcePackerStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsIce() {
    var data = new byte[256];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.IcePacker.IcePackerStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x49)); // 'I'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x63)); // 'c'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x65)); // 'e'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x21)); // '!'
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RandomData() {
    var data = new byte[8192];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.IcePacker.IcePackerStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.IcePacker.IcePackerStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

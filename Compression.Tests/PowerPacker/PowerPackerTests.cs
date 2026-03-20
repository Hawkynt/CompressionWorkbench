namespace Compression.Tests.PowerPacker;

[TestFixture]
public class PowerPackerTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SmallData() {
    var data = "Hello, PowerPacker! This is a test of the PP20 format."u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PowerPacker.PowerPackerStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PowerPacker.PowerPackerStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    var data = new byte[4096];
    for (int i = 0; i < data.Length; i++)
      data[i] = (byte)(i % 16);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PowerPacker.PowerPackerStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PowerPacker.PowerPackerStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsPP20() {
    var data = new byte[256];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PowerPacker.PowerPackerStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x50)); // 'P'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x50)); // 'P'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x32)); // '2'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x30)); // '0'
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RandomData() {
    var data = new byte[8192];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.PowerPacker.PowerPackerStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.PowerPacker.PowerPackerStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

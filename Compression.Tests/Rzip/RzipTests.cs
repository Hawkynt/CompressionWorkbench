namespace Compression.Tests.Rzip;

[TestFixture]
public class RzipTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SmallData() {
    var data = "Hello, RZIP! Long-distance redundancy elimination."u8.ToArray();
    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RepetitiveData() {
    // Data with long-distance repetitions (ideal for rzip)
    var block = new byte[1024];
    Random.Shared.NextBytes(block);
    var data = new byte[block.Length * 4];
    for (var i = 0; i < 4; i++)
      Buffer.BlockCopy(block, 0, data, i * block.Length, block.Length);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsRZIP() {
    var data = new byte[256];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x52)); // 'R'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x5A)); // 'Z'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x49)); // 'I'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x50)); // 'P'
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_RandomData() {
    var data = new byte[8192];
    Random.Shared.NextBytes(data);

    using var compressed = new MemoryStream();
    using (var input = new MemoryStream(data))
      FileFormat.Rzip.RzipStream.Compress(input, compressed);

    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    FileFormat.Rzip.RzipStream.Decompress(compressed, decompressed);

    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

using FileFormat.Bsc;
namespace Compression.Tests.Bsc;

public class BscTests {
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(256)]
  [TestCase(4096)]
  [TestCase(65536)]
  public void RoundTrip(int size) {
    var data = new byte[size];
    Random.Shared.NextBytes(data);
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BscStream.Compress(input, compressed);
    compressed.Position = 0;

    // Verify magic
    var magic = new byte[4];
    compressed.ReadExactly(magic);
    Assert.That(magic, Is.EqualTo(new byte[] { 0x62, 0x73, 0x63, 0x31 }));
    compressed.Position = 0;

    using var decompressed = new MemoryStream();
    BscStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

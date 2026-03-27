using FileFormat.Balz;
namespace Compression.Tests.Balz;

[TestFixture]
public class BalzTests {

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
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Repetitive() {
    var data = new byte[10000];
    var pattern = "The quick brown fox jumps over the lazy dog. "u8;
    for (var i = 0; i < data.Length; i++) data[i] = pattern[i % pattern.Length];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    BalzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Header_IsBigEndianSize() {
    var data = new byte[1000];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BalzStream.Compress(input, compressed);
    compressed.Position = 0;
    var b0 = compressed.ReadByte();
    var b1 = compressed.ReadByte();
    var b2 = compressed.ReadByte();
    var b3 = compressed.ReadByte();
    var size = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
    Assert.That(size, Is.EqualTo(1000));
  }
}

using FileFormat.Csc;
namespace Compression.Tests.Csc;

[TestFixture]
public class CscTests {

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
    CscStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    CscStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CscStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    CscStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Repetitive() {
    var data = new byte[10000];
    var pattern = "The quick brown fox jumps over the lazy dog. "u8;
    for (var i = 0; i < data.Length; i++) data[i] = pattern[i % pattern.Length];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CscStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    CscStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Header_Is14BytesMinimum() {
    // 10-byte property header + 4-byte uncompressed size = 14 bytes minimum
    var data = new byte[100];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    CscStream.Compress(input, compressed);
    Assert.That(compressed.Length, Is.GreaterThan(14));
  }
}

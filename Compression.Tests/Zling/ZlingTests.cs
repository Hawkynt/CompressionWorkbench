using FileFormat.Zling;
namespace Compression.Tests.Zling;

[TestFixture]
public class ZlingTests {

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
    ZlingStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    ZlingStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_Empty() {
    var data = Array.Empty<byte>();
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    ZlingStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    ZlingStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_Repetitive() {
    var data = new byte[10000];
    var pattern = "The quick brown fox jumps over the lazy dog. "u8;
    for (var i = 0; i < data.Length; i++) data[i] = pattern[i % pattern.Length];
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    ZlingStream.Compress(input, compressed);
    compressed.Position = 0;
    using var decompressed = new MemoryStream();
    ZlingStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void BlockFormat_HasFlag() {
    var data = "hello world"u8.ToArray();
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    ZlingStream.Compress(input, compressed);
    compressed.Position = 0;
    // A sub-block starts with flag 1; the input block ends with flag 0.
    var bytes = compressed.ToArray();
    Assert.That(bytes[0], Is.EqualTo(1));
    Assert.That(bytes[^1], Is.EqualTo(0));
  }
}

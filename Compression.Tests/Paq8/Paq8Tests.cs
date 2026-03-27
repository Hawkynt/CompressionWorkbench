using FileFormat.Paq8;
namespace Compression.Tests.Paq8;

public class Paq8Tests {
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
    Paq8Stream.Compress(input, compressed);
    compressed.Position = 0;

    // Verify magic
    var header = new byte[7];
    compressed.ReadExactly(header);
    Assert.That(System.Text.Encoding.ASCII.GetString(header), Is.EqualTo("paq8l -"));
    compressed.Position = 0;

    using var decompressed = new MemoryStream();
    Paq8Stream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

using FileFormat.Mcm;
namespace Compression.Tests.Mcm;

public class McmTests {
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
    McmStream.Compress(input, compressed);
    compressed.Position = 0;

    // Verify magic
    var magic = new byte[10];
    compressed.ReadExactly(magic);
    Assert.That(System.Text.Encoding.ASCII.GetString(magic), Is.EqualTo("MCMARCHIVE"));
    compressed.Position = 0;

    using var decompressed = new MemoryStream();
    McmStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

using FileFormat.Lizard;
namespace Compression.Tests.Lizard;

public class LizardTests {
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
    LizardStream.Compress(input, compressed);
    compressed.Position = 0;

    // Verify magic
    Span<byte> magic = stackalloc byte[4];
    compressed.ReadExactly(magic);
    Assert.That(magic[0], Is.EqualTo(0x06));
    Assert.That(magic[1], Is.EqualTo(0x22));
    Assert.That(magic[2], Is.EqualTo(0x4D));
    Assert.That(magic[3], Is.EqualTo(0x18));
    compressed.Position = 0;

    using var decompressed = new MemoryStream();
    LizardStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }
}

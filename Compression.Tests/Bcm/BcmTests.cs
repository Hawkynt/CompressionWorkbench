#pragma warning disable CS1591
using FileFormat.Bcm;
namespace Compression.Tests.Bcm;

[TestFixture]
public class BcmTests {

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
    BcmStream.Compress(input, compressed);
    compressed.Position = 0;

    // Verify magic
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x42)); // 'B'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x43)); // 'C'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x4D)); // 'M'
    Assert.That(compressed.ReadByte(), Is.EqualTo(0x21)); // '!'
    compressed.Position = 0;

    using var decompressed = new MemoryStream();
    BcmStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Compress_Repetitive_Data() {
    var data = new byte[10000];
    Array.Fill(data, (byte)'A');
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    BcmStream.Compress(input, compressed);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }
}

#pragma warning disable CS1591

using FileFormat.QuickLz;
namespace Compression.Tests.QuickLz;

public class QuickLzTests {
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
    QuickLzStream.Compress(input, compressed);
    compressed.Position = 0;

    // Verify flag byte has bit 6 set
    var flags = compressed.ReadByte();
    Assert.That(flags & 0x40, Is.EqualTo(0x40));
    compressed.Position = 0;

    using var decompressed = new MemoryStream();
    QuickLzStream.Decompress(compressed, decompressed);
    Assert.That(decompressed.ToArray(), Is.EqualTo(data));
  }

  [Test]
  public void Compress_Repetitive_Achieves_Compression() {
    var data = new byte[10000];
    Array.Fill(data, (byte)'A');
    using var input = new MemoryStream(data);
    using var compressed = new MemoryStream();
    QuickLzStream.Compress(input, compressed);
    Assert.That(compressed.Length, Is.LessThan(data.Length));
  }
}

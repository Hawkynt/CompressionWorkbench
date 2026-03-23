using Compression.Core.Streams;
using FileFormat.Compress;

namespace Compression.Tests.Compress;

[TestFixture]
public class CompressStreamTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    byte[] data = [];
    var compressed = CompressData(data);
    var result = DecompressData(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    var compressed = CompressData(data);
    var result = DecompressData(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TextData() {
    var data = "Hello, Unix compress (.Z) world!"u8.ToArray();
    var compressed = CompressData(data);
    var result = DecompressData(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var pattern = "ABCDEFGHIJ"u8.ToArray();
    var data = new byte[pattern.Length * 100];
    for (var i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var compressed = CompressData(data);
    var result = DecompressData(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var compressed = CompressData(data);
    var result = DecompressData(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Header_HasMagicBytes() {
    byte[] data = [1, 2, 3];
    var compressed = CompressData(data);

    Assert.That(compressed[0], Is.EqualTo(0x1F));
    Assert.That(compressed[1], Is.EqualTo(0x9D));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Header_FlagsContainMaxBitsAndBlockMode() {
    byte[] data = [1, 2, 3];
    var compressed = CompressData(data, maxBits: 14, blockMode: true);

    var flags = compressed[2];
    Assert.That(flags & 0x1F, Is.EqualTo(14)); // max bits
    Assert.That(flags & 0x80, Is.EqualTo(0x80)); // block mode flag
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_NoBlockMode() {
    var data = "Testing without block mode."u8.ToArray();
    var compressed = CompressData(data, blockMode: false);
    var result = DecompressData(compressed);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Test]
  public void RepetitiveData_CompressesWell() {
    var data = new byte[4096];
    Array.Fill(data, (byte)'A');

    var compressed = CompressData(data);
    Assert.That(compressed.Length, Is.LessThan(data.Length / 2));
  }

  private static byte[] CompressData(byte[] data, int maxBits = 16, bool blockMode = true) {
    using var ms = new MemoryStream();
    using (var cs = new CompressStream(ms, CompressionStreamMode.Compress,
      maxBits, blockMode, leaveOpen: true)) {
      cs.Write(data, 0, data.Length);
    }
    return ms.ToArray();
  }

  private static byte[] DecompressData(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var cs = new CompressStream(ms, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    cs.CopyTo(output);
    return output.ToArray();
  }
}

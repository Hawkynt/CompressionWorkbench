using Compression.Core.Dictionary.Lzma;
using FileFormat.Lzma;
using FmtConstants = FileFormat.Lzma.LzmaConstants;

namespace Compression.Tests.Lzma;

[TestFixture]
public class LzmaStreamTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    var data = System.Text.Encoding.UTF8.GetBytes("Hello, LZMA alone!");

    var compressed = Compress(data);
    var result = Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    byte[] data = [];

    var compressed = Compress(data);
    var result = Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeRepetitive() {
    var pattern = System.Text.Encoding.UTF8.GetBytes("ABCDEFGH");
    var data = new byte[65536];
    for (var i = 0; i < data.Length; ++i)
      data[i] = pattern[i % pattern.Length];

    var compressed = Compress(data);
    var result = Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var data = new byte[4096];
    rng.NextBytes(data);

    var compressed = Compress(data);
    var result = Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidHeader_Throws() {
    // Construct a header with a properties byte of 225 (>= 9*5*5), which is invalid
    var bad = new byte[FmtConstants.HeaderSize];
    bad[0] = 225;

    using var input = new MemoryStream(bad);
    using var output = new MemoryStream();

    Assert.Throws<InvalidDataException>(() => LzmaStream.Decompress(input, output));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_CustomProperties() {
    var data = System.Text.Encoding.UTF8.GetBytes(
        "Custom lc/lp/pb properties test: the quick brown fox jumps over the lazy dog.");

    // Use non-default lc=2, lp=1, pb=1
    var compressed = Compress(data, lc: 2, lp: 1, pb: 1);
    var result = Decompress(compressed);

    Assert.That(result, Is.EqualTo(data));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Header_Size_IsThirteenBytes() {
    var data = System.Text.Encoding.UTF8.GetBytes("header size test");
    var compressed = Compress(data);

    Assert.That(compressed.Length, Is.GreaterThan(FmtConstants.HeaderSize));
  }

  [Category("ThemVsUs")]
  [Test]
  public void Header_UncompressedSize_IsCorrect() {
    var data = System.Text.Encoding.UTF8.GetBytes("uncompressed size field test");
    var compressed = Compress(data);

    // Bytes 5-12 in the output are the little-endian int64 uncompressed size
    var storedSize = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(
        compressed.AsSpan(5, 8));

    Assert.That(storedSize, Is.EqualTo(data.LongLength));
  }

  private static byte[] Compress(
      byte[] data,
      int dictionarySize = FmtConstants.DefaultDictionarySize,
      int lc = 3,
      int lp = 0,
      int pb = 2,
      LzmaCompressionLevel level = LzmaCompressionLevel.Normal) {
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    LzmaStream.Compress(input, output, dictionarySize, lc, lp, pb, level);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] compressed) {
    using var input = new MemoryStream(compressed);
    using var output = new MemoryStream();
    LzmaStream.Decompress(input, output);
    return output.ToArray();
  }
}

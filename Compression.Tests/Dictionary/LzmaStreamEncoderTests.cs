using Compression.Core.Dictionary.Lzma;

namespace Compression.Tests.Dictionary;

/// <summary>
/// The streaming LZMA encoder: for the same input and properties its bytes equal the in-memory
/// encoder's — across window slides, dictionaries that are and are not powers of two, levels and
/// the end marker — and they decode back.
/// </summary>
[TestFixture]
public class LzmaStreamEncoderTests {
  private static byte[] Input(int size, int seed) {
    var random = new Random(seed);
    var data = new byte[size];
    for (var i = 0; i < size;) {
      if (random.Next(3) == 0 && i > 300) {
        // A repeat from up to 200 KiB back: crosses small dictionaries and window slides.
        var from = i - 1 - random.Next(Math.Min(i - 1, 200_000));
        var len = Math.Min(random.Next(2, 400), size - i);
        for (var k = 0; k < len; ++k)
          data[i + k] = data[from + k];
        i += len;
      } else
        data[i++] = (byte)(random.Next(4) == 0 ? random.Next() : 'a' + random.Next(6));
    }
    return data;
  }

  public static IEnumerable<TestCaseData> Cases() {
    foreach (var size in new[] { 0, 1, 2, 300, 70_000, 3_500_000 })
      foreach (var dictionary in new[] { 4096, 1 << 16, 3 << 16, 1 << 23 })
        yield return new TestCaseData(size, dictionary, LzmaCompressionLevel.Normal, false);
    yield return new TestCaseData(2_000_000, 1 << 16, LzmaCompressionLevel.Fast, true);
    yield return new TestCaseData(2_000_000, 1 << 16, LzmaCompressionLevel.Best, false);
  }

  [TestCaseSource(nameof(Cases))]
  public void Given_AnyInput_When_EncodedAsAStream_Then_TheBytesEqualTheInMemoryEncoder(int size, int dictionary, LzmaCompressionLevel level, bool endMarker) {
    var data = Input(size, size ^ dictionary);

    using var expected = new MemoryStream();
    new LzmaEncoder(dictionary, level: level).Encode(expected, data, endMarker);
    using var actual = new MemoryStream();
    var streaming = new LzmaStreamEncoder(dictionary, level: level);
    streaming.Encode(actual, new MemoryStream(data), data.Length, endMarker);

    Assert.That(actual.ToArray(), Is.EqualTo(expected.ToArray()));
    Assert.That(streaming.Properties, Is.EqualTo(new LzmaEncoder(dictionary, level: level).Properties));
    actual.Position = 0;
    Assert.That(new LzmaDecoder(actual, streaming.Properties, size).Decode(), Is.EqualTo(data));
  }

  [Test]
  [Category("EdgeCase")]
  public void Given_AnInputShorterThanDeclared_When_Encoded_Then_ItIsRefused() {
    Assert.That(() => new LzmaStreamEncoder().Encode(new MemoryStream(), new MemoryStream(new byte[10]), 20), Throws.TypeOf<EndOfStreamException>());
  }
}

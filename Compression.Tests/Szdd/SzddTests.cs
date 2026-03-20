using FileFormat.Szdd;

namespace Compression.Tests.Szdd;

[TestFixture]
public class SzddTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    byte[] input = "Hello, SZDD World!"u8.ToArray();
    byte[] compressed = SzddStream.Compress(input);
    byte[] result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    byte[] input = [];
    byte[] compressed = SzddStream.Compress(input);
    // Must at minimum contain a valid 14-byte header.
    Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(14));
    byte[] result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeRepetitive() {
    // 16 KB of repeating pattern — should compress well.
    var pattern = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"u8.ToArray();
    var input = new byte[16 * 1024];
    for (int i = 0; i < input.Length; ++i)
      input[i] = pattern[i % pattern.Length];

    byte[] compressed = SzddStream.Compress(input);
    Assert.That(compressed.Length, Is.LessThan(input.Length),
      "Repetitive data should compress to less than the original size.");
    byte[] result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var input = new byte[4096];
    rng.NextBytes(input);
    byte[] compressed = SzddStream.Compress(input);
    byte[] result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidMagic_Throws() {
    byte[] bad = new byte[20];
    bad[0] = 0xDE; bad[1] = 0xAD; bad[2] = 0xBE; bad[3] = 0xEF;
    Assert.Throws<InvalidDataException>(() => SzddStream.Decompress(bad));
  }

  [Category("HappyPath")]
  [Test]
  public void GetMissingChar_ReturnsCorrectChar() {
    byte[] input = "SETUP"u8.ToArray();
    // Simulate a .EX_ file — missing char is 'e'.
    byte[] compressed = SzddStream.Compress(input, missingChar: 'e');
    using var ms = new MemoryStream(compressed);
    char missing = SzddStream.GetMissingChar(ms);
    Assert.That(missing, Is.EqualTo('e'));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Stream_Overloads() {
    byte[] input = "Stream overload test."u8.ToArray();
    using var inputStream = new MemoryStream(input);
    using var compressedStream = new MemoryStream();
    SzddStream.Compress(inputStream, compressedStream, missingChar: 'x');

    compressedStream.Position = 0;
    using var outputStream = new MemoryStream();
    SzddStream.Decompress(compressedStream, outputStream);

    Assert.That(outputStream.ToArray(), Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Test]
  public void GetMissingChar_DefaultUnderscore() {
    byte[] input = "test"u8.ToArray();
    byte[] compressed = SzddStream.Compress(input); // default missingChar = '_'
    using var ms = new MemoryStream(compressed);
    char missing = SzddStream.GetMissingChar(ms);
    Assert.That(missing, Is.EqualTo('_'));
  }
}

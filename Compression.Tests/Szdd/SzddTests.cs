using FileFormat.Szdd;

namespace Compression.Tests.Szdd;

[TestFixture]
public class SzddTests {
  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallData() {
    var input = "Hello, SZDD World!"u8.ToArray();
    var compressed = SzddStream.Compress(input);
    var result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  // ── Old "SZ " (QBasic / pre-SZDD) COMPRESS variant ────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void QBasic_RoundTrip_TextWithMatches() {
    var input = "MZ........the quick brown fox the quick brown fox the quick brown fox"u8.ToArray();
    var compressed = SzddStream.CompressQBasic(input);
    // 12-byte "SZ " header: magic at 0-7, u32 length at 8-11, stream at 12.
    Assert.That(compressed[0], Is.EqualTo(0x53)); // 'S'
    Assert.That(compressed[1], Is.EqualTo(0x5A)); // 'Z'
    Assert.That(compressed[2], Is.EqualTo(0x20)); // ' '
    Assert.That(compressed[7], Is.EqualTo(0xD1)); // trailing 0xD1 distinguishes from SZDD
    var result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void QBasic_RoundTrip_Empty() {
    byte[] input = [];
    var compressed = SzddStream.CompressQBasic(input);
    Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(12));
    Assert.That(SzddStream.Decompress(compressed), Is.EqualTo(input));
  }

  // Validates the real-format literal path the user observed: a first control
  // byte 0xFF (8 set bits = 8 literals) over the 12-byte "SZ " header yields the
  // 8 literal bytes verbatim — e.g. an "MZ" EXE header.
  [Category("Boundary")]
  [Test]
  public void QBasic_FirstControl0xFF_YieldsEightLiterals() {
    var literals = new byte[] { (byte)'M', (byte)'Z', 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };
    var blob = new byte[12 + 1 + literals.Length];
    new byte[] { 0x53, 0x5A, 0x20, 0x88, 0xF0, 0x27, 0x33, 0xD1 }.CopyTo(blob, 0);
    blob[8] = (byte)literals.Length; // u32 LE uncompressed length = 8
    blob[12] = 0xFF;                  // control: all 8 items are literals
    literals.CopyTo(blob, 13);
    var result = SzddStream.Decompress(blob);
    Assert.That(result, Is.EqualTo(literals));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Empty() {
    byte[] input = [];
    var compressed = SzddStream.Compress(input);
    // Must at minimum contain a valid 14-byte header.
    Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(14));
    var result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeRepetitive() {
    // 16 KB of repeating pattern — should compress well.
    var pattern = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"u8.ToArray();
    var input = new byte[16 * 1024];
    for (var i = 0; i < input.Length; ++i)
      input[i] = pattern[i % pattern.Length];

    var compressed = SzddStream.Compress(input);
    Assert.That(compressed.Length, Is.LessThan(input.Length),
      "Repetitive data should compress to less than the original size.");
    var result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var input = new byte[4096];
    rng.NextBytes(input);
    var compressed = SzddStream.Compress(input);
    var result = SzddStream.Decompress(compressed);
    Assert.That(result, Is.EqualTo(input));
  }

  [Category("Exception")]
  [Test]
  public void Decompress_InvalidMagic_Throws() {
    var bad = new byte[20];
    bad[0] = 0xDE; bad[1] = 0xAD; bad[2] = 0xBE; bad[3] = 0xEF;
    Assert.Throws<InvalidDataException>(() => SzddStream.Decompress(bad));
  }

  [Category("HappyPath")]
  [Test]
  public void GetMissingChar_ReturnsCorrectChar() {
    var input = "SETUP"u8.ToArray();
    // Simulate a .EX_ file — missing char is 'e'.
    var compressed = SzddStream.Compress(input, missingChar: 'e');
    using var ms = new MemoryStream(compressed);
    var missing = SzddStream.GetMissingChar(ms);
    Assert.That(missing, Is.EqualTo('e'));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_Stream_Overloads() {
    var input = "Stream overload test."u8.ToArray();
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
    var input = "test"u8.ToArray();
    var compressed = SzddStream.Compress(input); // default missingChar = '_'
    using var ms = new MemoryStream(compressed);
    var missing = SzddStream.GetMissingChar(ms);
    Assert.That(missing, Is.EqualTo('_'));
  }
}

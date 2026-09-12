using Compression.Registry;
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

  // ── Size-optimal parser ───────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_AdvertisesOptimize() {
    var descriptor = new SzddFormatDescriptor();

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    Assert.That(descriptor.Methods.Single().SupportsOptimize, Is.True);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [TestCaseSource(nameof(OptimizerCorpus))]
  public void CompressOptimal_IsNoLargerThanGreedy_AndRoundTrips(byte[] input) {
    var greedy = SzddStream.Compress(input);
    var optimal = CompressOptimal(input);

    Assert.That(optimal.Length, Is.LessThanOrEqualTo(greedy.Length),
      "The optimal parse must never encode more bytes than the greedy one.");
    Assert.That(SzddStream.Decompress(optimal), Is.EqualTo(input),
      "Optimally parsed output must be read by the ordinary SZDD decoder.");
  }

  [Category("HappyPath")]
  [Test]
  public void CompressOptimal_BeatsGreedy_OnOverlappingPhrases() {
    // Phrases of differing lengths recur in random order, so a longest-match-now
    // choice repeatedly hides a better token immediately after it. The planner
    // sees past that; the greedy parse does not.
    var input = PhraseCorpus(seed: 47);

    var greedy = SzddStream.Compress(input).Length;
    var optimal = CompressOptimal(input).Length;

    Assert.That(optimal, Is.LessThan(greedy),
      $"Optimal parse produced {optimal} bytes, greedy {greedy}.");
    Assert.That(SzddStream.Decompress(CompressOptimal(input)), Is.EqualTo(input));
  }

  private static byte[] PhraseCorpus(int seed) {
    var rng = new Random(seed);
    var phrases = new List<byte[]>();
    for (var i = 0; i < 12; ++i) {
      var phrase = new byte[rng.Next(3, 14)];
      rng.NextBytes(phrase);
      phrases.Add(phrase);
    }

    var buffer = new List<byte>();
    while (buffer.Count < 6000) {
      buffer.AddRange(phrases[rng.Next(phrases.Count)]);
      if (rng.Next(4) == 0)
        buffer.Add((byte)rng.Next(256));
    }

    return [.. buffer];
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void CompressOptimal_Empty() {
    var optimal = CompressOptimal([]);

    Assert.That(optimal.Length, Is.EqualTo(14), "Only the SZDD header is emitted.");
    Assert.That(SzddStream.Decompress(optimal), Is.Empty);
  }

  private static IEnumerable<byte[]> OptimizerCorpus() {
    yield return "Hello, SZDD World!"u8.ToArray();
    yield return [(byte)'A'];
    yield return [.. Enumerable.Repeat((byte)'Z', 5000)];
    yield return [.. Enumerable.Range(0, 4096).Select(i => (byte)i)];
    yield return [.. Enumerable.Range(0, 20000).Select(i => (byte)((i * 31 + i / 7) & 0xff))];

    var rng = new Random(4711);
    yield return [.. Enumerable.Range(0, 30000).Select(_ => (byte)rng.Next(256))];
  }

  private static byte[] CompressOptimal(byte[] input) {
    IStreamFormatOperations operations = new SzddFormatDescriptor();
    using var source = new MemoryStream(input, writable: false);
    using var destination = new MemoryStream();
    operations.CompressOptimal(source, destination);
    return destination.ToArray();
  }
}

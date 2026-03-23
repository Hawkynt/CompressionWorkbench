using Compression.Core.Dictionary.Lzma;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class LzmaTests {
  [Category("HappyPath")]
  [Test]
  public void Properties_Encode_Decode() {
    var encoder = new LzmaEncoder(dictionarySize: 1 << 20, lc: 3, lp: 0, pb: 2);
    var props = encoder.Properties;

    Assert.That(props.Length, Is.EqualTo(5));

    // Verify properties byte: (pb * 5 + lp) * 9 + lc = (2*5+0)*9+3 = 93
    Assert.That(props[0], Is.EqualTo(93));

    // Verify dictionary size (1 << 20 = 1048576 = 0x100000)
    var dictSize = props[1] | (props[2] << 8) | (props[3] << 16) | (props[4] << 24);
    Assert.That(dictSize, Is.EqualTo(1 << 20));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    var result = CompressDecompress([], useEndMarker: true);
    Assert.That(result, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] data = [42];
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_TextData() {
    var data = "Hello, LZMA World! This is a test of the LZMA compression algorithm."u8.ToArray();
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var pattern = "the quick brown fox jumps over the lazy dog. "u8.ToArray();
    var data = new byte[pattern.Length * 100];
    for (var i = 0; i < 100; ++i)
      Array.Copy(pattern, 0, data, i * pattern.Length, pattern.Length);

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var data = new byte[1024];
    rng.NextBytes(data);

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_HighlyRepetitive() {
    var data = new byte[4096];
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeData() {
    var rng = new Random(123);
    var data = new byte[10240];
    // Mix of patterns and random
    for (var i = 0; i < data.Length; ++i) {
      if (i % 100 < 50)
        data[i] = (byte)(i % 26 + 'a');
      else
        data[i] = (byte)rng.Next(256);
    }

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_CustomProperties() {
    var data = "Testing with custom LZMA properties."u8.ToArray();

    // lc=0, lp=0, pb=0
    var result1 = CompressDecompress(data, lc: 0, lp: 0, pb: 0);
    Assert.That(result1, Is.EqualTo(data));

    // lc=4, lp=2, pb=1
    var result2 = CompressDecompress(data, lc: 4, lp: 2, pb: 1);
    Assert.That(result2, Is.EqualTo(data));
  }

  [Category("Exception")]
  [Test]
  public void Decode_InvalidProperties_Throws() {
    byte[] badProps = [225, 0, 0, 0, 0]; // 225 >= 9*5*5 = 225, so borderline
    using var ms = new MemoryStream([0, 0, 0, 0, 0]);

    // Properties byte 225 = 9*5*5, which is out of range
    Assert.Throws<InvalidDataException>(() =>
      _ = new LzmaDecoder(ms, badProps));
  }

  [Category("HappyPath")]
  [Test]
  public void Compress_RepetitiveData_CompressesWell() {
    var data = new byte[4096];
    Array.Fill(data, (byte)'A');

    using var compressed = new MemoryStream();
    var encoder = new LzmaEncoder(dictionarySize: 1 << 16);
    encoder.Encode(compressed, data);

    var ratio = (double)compressed.Length / data.Length;
    Assert.That(ratio, Is.LessThan(0.1), $"Compression ratio {ratio:P} too high for repetitive data");
  }

  [Category("ThemVsUs")]
  [Test]
  public void StateTransitions_AreCorrect() {
    // After literal from initial state
    Assert.That(LzmaConstants.StateUpdateLiteral(0), Is.EqualTo(0));
    Assert.That(LzmaConstants.StateUpdateLiteral(7), Is.EqualTo(4));

    // After match
    Assert.That(LzmaConstants.StateUpdateMatch(0), Is.EqualTo(7));
    Assert.That(LzmaConstants.StateUpdateMatch(7), Is.EqualTo(10));

    // After rep
    Assert.That(LzmaConstants.StateUpdateRep(0), Is.EqualTo(8));
    Assert.That(LzmaConstants.StateUpdateRep(8), Is.EqualTo(11));

    // After short rep
    Assert.That(LzmaConstants.StateUpdateShortRep(0), Is.EqualTo(9));
    Assert.That(LzmaConstants.StateUpdateShortRep(9), Is.EqualTo(11));

    // IsLiteral
    Assert.That(LzmaConstants.StateIsLiteral(6), Is.True);
    Assert.That(LzmaConstants.StateIsLiteral(7), Is.False);
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithKnownSize() {
    var data = "Known-size LZMA test data."u8.ToArray();
    var result = CompressDecompress(data, writeEndMarker: false);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ShortRepMatches() {
    // Pattern designed to trigger short rep (1-byte rep0 copies):
    // "ABABABAB..." — each B is a short rep of the previous B at distance 2
    var data = new byte[512];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i % 2 == 0 ? 'A' : 'B');

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MultipleRepDistances() {
    // Build data that exercises rep0/rep1/rep2/rep3 shuffling:
    // Repeat 4 distinct patterns, then re-reference them in mixed order
    var p1 = "HELLO WORLD! "u8.ToArray();
    var p2 = "GOODBYE MOON! "u8.ToArray();
    var p3 = "TESTING 123! "u8.ToArray();
    var p4 = "PATTERN FOUR! "u8.ToArray();

    using var ms = new MemoryStream();
    // Establish 4 patterns
    ms.Write(p1); ms.Write(p2); ms.Write(p3); ms.Write(p4);
    // Re-reference them in reverse (triggers rep distance swapping)
    ms.Write(p4); ms.Write(p3); ms.Write(p2); ms.Write(p1);
    // And again in mixed order
    ms.Write(p2); ms.Write(p4); ms.Write(p1); ms.Write(p3);
    ms.Write(p1); ms.Write(p1); ms.Write(p1); // repeated same (short rep path)

    var data = ms.ToArray();
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_LargeDistanceMatches() {
    // Place a pattern early, fill with unrelated data, then repeat it
    // This exercises large distance encoding (posSlot > EndPosModelIndex)
    var rng = new Random(777);
    var data = new byte[32768];
    rng.NextBytes(data);

    // Place an identifiable pattern at start and repeat it far away
    var pattern = "THIS_IS_A_MARKER_PATTERN_FOR_LARGE_DIST"u8.ToArray();
    pattern.CopyTo(data.AsSpan(0));
    pattern.CopyTo(data.AsSpan(16384));
    pattern.CopyTo(data.AsSpan(24576));

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_MaxLengthMatch() {
    // Data with a very long repetition to exercise max match length (273)
    var pattern = new byte[273];
    for (var i = 0; i < pattern.Length; ++i)
      pattern[i] = (byte)(i % 7 + 'a');

    var data = new byte[pattern.Length * 20];
    for (var i = 0; i < 20; ++i)
      pattern.CopyTo(data.AsSpan(i * pattern.Length));

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllByteValues() {
    // All 256 byte values — exercises literal coding with diverse contexts
    var data = new byte[512];
    for (var i = 0; i < 256; ++i)
      data[i] = (byte)i;
    // Repeat to give some match opportunities
    Array.Copy(data, 0, data, 256, 256);

    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  [Category("Boundary")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SmallDictionarySize() {
    // Exercise with a very small dictionary (4KB minimum)
    var data = "Small dictionary test data with some repetition. "u8.ToArray();
    var bigData = new byte[data.Length * 50];
    for (var i = 0; i < 50; ++i)
      data.CopyTo(bigData.AsSpan(i * data.Length));

    var encoder = new LzmaEncoder(dictionarySize: 4096);
    using var compressed = new MemoryStream();
    encoder.Encode(compressed, bigData);

    compressed.Position = 0;
    var decoder = new LzmaDecoder(compressed, encoder.Properties);
    var result = decoder.Decode();
    Assert.That(result, Is.EqualTo(bigData));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_AllStateTransitionPaths() {
    // Crafted data to trigger all 12 LZMA states:
    // States 0-6 are "literal" states, 7-11 are "match" states
    // Mix of: literals, normal matches, rep matches, short reps
    using var ms = new MemoryStream();
    // Unique prefix to start in literal states
    ms.Write("UNIQUE_PREFIX_"u8);
    // Repetitive block to trigger matches
    for (var i = 0; i < 50; ++i)
      ms.Write("XYZ"u8);
    // Different pattern for normal match -> literal -> rep transitions
    ms.Write("ABCDEFGH"u8);
    ms.Write("IJKLMNOP"u8);
    ms.Write("ABCDEFGH"u8); // match back
    ms.Write("Q"u8);        // literal after match
    ms.Write("ABCDEFGH"u8); // rep match
    ms.Write("R"u8);        // literal after rep
    // Short rep patterns (single-byte from rep0)
    for (var i = 0; i < 100; ++i)
      ms.Write("AA"u8);

    var data = ms.ToArray();
    var result = CompressDecompress(data);
    Assert.That(result, Is.EqualTo(data));
  }

  private static byte[] CompressDecompress(byte[] data,
    int lc = 3, int lp = 0, int pb = 2, bool writeEndMarker = true,
    bool useEndMarker = false) {
    var encoder = new LzmaEncoder(dictionarySize: 1 << 20, lc: lc, lp: lp, pb: pb);

    using var compressed = new MemoryStream();
    encoder.Encode(compressed, data, writeEndMarker);

    compressed.Position = 0;

    long uncompressedSize = writeEndMarker || useEndMarker ? -1 : data.Length;
    var decoder = new LzmaDecoder(compressed, encoder.Properties, uncompressedSize);
    return decoder.Decode();
  }
}

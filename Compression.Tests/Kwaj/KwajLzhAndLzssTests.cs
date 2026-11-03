using Compression.Core.Dictionary.KwajLzh;
using FileFormat.Kwaj;

namespace Compression.Tests.Kwaj;

/// <summary>KWAJ methods 2 (LZSS) and 3 (LZ + Huffman).</summary>
[TestFixture]
public sealed class KwajLzhAndLzssTests {

  private static byte[] Compress(byte[] data, int method) {
    using var output = new MemoryStream();
    KwajStream.Compress(new MemoryStream(data), output, method);
    return output.ToArray();
  }

  private static byte[] Decompress(byte[] kwaj) {
    using var output = new MemoryStream();
    KwajStream.Decompress(new MemoryStream(kwaj), output);
    return output.ToArray();
  }

  private static byte[] Text(int length) {
    var sentence = "The quick brown fox jumps over the lazy dog; KWAJ was COMPRESS.EXE's format. "u8;
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = sentence[i % sentence.Length];
    return data;
  }

  private static byte[] Random(int length, int seed) {
    var data = new byte[length];
    new Random(seed).NextBytes(data);
    return data;
  }

  public static IEnumerable<TestCaseData> Payloads() {
    foreach (var n in new[] { 0, 1, 2, 3, 17, 18, 31, 32, 33, 64, 4095, 4096, 4097, 20000 }) {
      yield return new TestCaseData(Text(n)).SetName($"Text_{n}");
      yield return new TestCaseData(Random(n, n)).SetName($"Random_{n}");
    }
    yield return new TestCaseData(new byte[5000]).SetName("Zeros_5000");
    yield return new TestCaseData(Enumerable.Repeat((byte)' ', 300).ToArray()).SetName("Spaces_300");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void Method2Lzss_RoundTrips(byte[] data)
    => Assert.That(Decompress(Compress(data, KwajConstants.MethodLzss)), Is.EqualTo(data));

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void Method3Lzh_RoundTrips(byte[] data)
    => Assert.That(Decompress(Compress(data, KwajConstants.MethodLzHuffman)), Is.EqualTo(data));

  [Test, Category("RoundTrip")]
  public void Method3Lzh_EveryLengthUpTo300_RoundTrips() {
    // The stream has no end marker; every tail alignment must still stop exactly.
    var source = Random(300, 1);
    for (var n = 0; n <= 300; ++n) {
      var data = source[..n];
      var encoded = KwajLzhBuildingBlock.Encode(data);
      Assert.That(KwajLzhBuildingBlock.Decode(encoded), Is.EqualTo(data), $"length {n}");
    }
  }

  [Test, Category("RoundTrip")]
  public void Method3Lzh_EveryTextLengthUpTo300_RoundTrips() {
    var source = Text(300);
    for (var n = 0; n <= 300; ++n) {
      var data = source[..n];
      Assert.That(KwajLzhBuildingBlock.Decode(KwajLzhBuildingBlock.Encode(data)), Is.EqualTo(data), $"length {n}");
    }
  }

  [Test, Category("HappyPath")]
  public void Method3Lzh_CompressesText() {
    var data = Text(50_000);
    Assert.That(KwajLzhBuildingBlock.Encode(data).Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("Spec")]
  public void Method3Lzh_AllFlatTables_DecodesLiteralRun() {
    // Hand-assembled stream: six type-0 nibbles (all tables flat: 4/4/5/6/8 bits),
    // then MATCHLEN1 symbol 0 (0000), LITLEN symbol 1 (00001) = 2 literals, 'H' 'i'.
    // 24 + 4 + 5 + 16 = 49 bits; the last byte's 7 padding bits cannot hold a token.
    var w = new List<int>();
    void Bits(int v, int n) { for (var i = n - 1; i >= 0; --i) w.Add((v >> i) & 1); }
    Bits(0, 24);
    Bits(0, 4);
    Bits(1, 5);
    Bits('H', 8);
    Bits('i', 8);
    while (w.Count % 8 != 0) w.Add(1);
    var bytes = new byte[w.Count / 8];
    for (var i = 0; i < w.Count; ++i) bytes[i >> 3] |= (byte)(w[i] << (7 - (i & 7)));
    Assert.That(KwajLzhBuildingBlock.Decode(bytes), Is.EqualTo("Hi"u8.ToArray()));
  }

  [Test, Category("Spec")]
  public void Method3Lzh_MatchFromPrefilledSpaces() {
    // MATCHLEN1 symbol 3 (len 5), OFFSET symbol 0 + 6 raw bits = 4: copies five of the
    // initial spaces. Flat tables throughout.
    var w = new List<int>();
    void Bits(int v, int n) { for (var i = n - 1; i >= 0; --i) w.Add((v >> i) & 1); }
    Bits(0, 24);
    Bits(3, 4);
    Bits(0, 6);
    Bits(4, 6);
    while (w.Count % 8 != 0) w.Add(1);
    var bytes = new byte[w.Count / 8];
    for (var i = 0; i < w.Count; ++i) bytes[i >> 3] |= (byte)(w[i] << (7 - (i & 7)));
    Assert.That(KwajLzhBuildingBlock.Decode(bytes), Is.EqualTo("     "u8.ToArray()));
  }

  [Test, Category("Exception")]
  public void Method3Lzh_IncompleteHuffmanTable_IsRejected() {
    // Type 3 for MATCHLEN1 with every length 1: 16 one-bit codes over-subscribe the tree.
    var w = new List<int>();
    void Bits(int v, int n) { for (var i = n - 1; i >= 0; --i) w.Add((v >> i) & 1); }
    Bits(3, 4);
    Bits(0, 20);
    for (var i = 0; i < 16; ++i) Bits(1, 4);
    while (w.Count % 8 != 0) w.Add(0);
    w.AddRange(Enumerable.Repeat(0, 64));
    var bytes = new byte[w.Count / 8];
    for (var i = 0; i < w.Count; ++i) bytes[i >> 3] |= (byte)(w[i] << (7 - (i & 7)));
    Assert.Throws<InvalidDataException>(() => KwajLzhBuildingBlock.Decode(bytes));
  }

  [TestCase(0), TestCase(10), Category("Exception")]
  public void Method3Lzh_IncompleteOrOversubscribedLongCodes_AreRejected(int extraLength) {
    // Two one-bit codes already cover the entire tree. A further ten-bit code
    // must be rejected even when the short-code lookup is completely filled.
    // With no extra code, a lone one-bit code is instead an incomplete tree.
    byte[] stream = new byte[19];
    stream[0] = 0x30; // Explicit MATCHLEN1 lengths; all other tables are flat.
    stream[3] = extraLength == 0 ? (byte)0x10 : (byte)0x11;
    stream[4] = (byte)(extraLength << 4);
    Assert.Throws<InvalidDataException>(() => KwajLzhBuildingBlock.Decode(stream));
  }

  [Test, Category("Boundary")]
  public void Method3Lzh_EmptyAndTruncatedInput_YieldNothing() {
    Assert.Multiple(() => {
      Assert.That(KwajLzhBuildingBlock.Decode([]), Is.Empty);
      Assert.That(KwajLzhBuildingBlock.Decode([0x00]), Is.Empty);
    });
  }

  [Test, Category("Spec")]
  public void Method2Lzss_UsesTheQBasicRingOrigin() {
    // Flag 0x00: the first item is a match of length 3 at ring position 0xFEE = 4078
    // (4096 - 18), which still holds the pre-filled spaces.
    var kwaj = Compress([], KwajConstants.MethodStore);
    kwaj[8] = (byte)KwajConstants.MethodLzss;
    byte[] body = [0x00, 0xEE, 0xF0];
    Assert.That(Decompress([.. kwaj, .. body]), Is.EqualTo("   "u8.ToArray()));
  }
}

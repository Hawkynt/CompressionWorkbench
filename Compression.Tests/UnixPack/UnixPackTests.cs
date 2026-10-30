using Compression.Core.Entropy.UnixPack;
using Compression.Registry;
using FileFormat.UnixPack;

namespace Compression.Tests.UnixPack;

[TestFixture]
public class UnixPackTests {

  // "aab": 'a' at length 1 (code 1), 'b' and EOF at length 2 (codes 00 and 01).
  private static readonly byte[] AabVector = [0x1F, 0x1E, 0, 0, 0, 3, 2, 1, 0, (byte)'a', (byte)'b', 0xC4];

  [Test, Category("Spec")]
  public void Decode_HandAssembledVector() => Assert.That(UnixPackBuildingBlock.Decode(AabVector), Is.EqualTo("aab"u8.ToArray()));

  public static IEnumerable<TestCaseData> Payloads() {
    yield return new TestCaseData(Array.Empty<byte>()).SetName("Empty");
    yield return new TestCaseData(new byte[] { 7 }).SetName("OneByte");
    yield return new TestCaseData(new byte[1000]).SetName("OneSymbolRepeated");
    yield return new TestCaseData("ab"u8.ToArray()).SetName("TwoSymbols");
    yield return new TestCaseData(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()).SetName("All256Once");
    var rnd = new byte[50_000];
    new Random(3).NextBytes(rnd);
    yield return new TestCaseData(rnd).SetName("Random50k");
    var text = new byte[40_000];
    var sentence = "pack squeezes text with one Huffman code; "u8;
    for (var i = 0; i < text.Length; ++i) text[i] = sentence[i % sentence.Length];
    yield return new TestCaseData(text).SetName("Text40k");
    // Fibonacci frequencies force deep codes, exercising the 24-bit length limit.
    var fib = new List<byte>();
    long a = 1, b = 1;
    for (var s = 0; s < 30; ++s) {
      for (var k = 0; k < a; ++k) fib.Add((byte)s);
      (a, b) = (b, a + b);
    }
    yield return new TestCaseData(fib.ToArray()).SetName("FibonacciSkew");
  }

  [TestCaseSource(nameof(Payloads)), Category("RoundTrip")]
  public void RoundTrip(byte[] data) {
    var packed = UnixPackBuildingBlock.Encode(data);
    Assert.Multiple(() => {
      Assert.That(packed[..2], Is.EqualTo(new byte[] { 0x1F, 0x1E }));
      Assert.That(packed[6], Is.InRange(1, 24), "the original pack never writes codes over 24 bits");
      Assert.That(UnixPackBuildingBlock.Decode(packed), Is.EqualTo(data));
    });
  }

  [Test, Category("HappyPath")]
  public void Encode_CompressesSkewedText() {
    var data = new byte[10_000];
    for (var i = 0; i < data.Length; ++i) data[i] = (byte)(i % 7 == 0 ? 'x' : 'e');
    Assert.That(UnixPackBuildingBlock.Encode(data).Length, Is.LessThan(data.Length / 4));
  }

  [Test, Category("Exception")]
  public void Decode_BadMagic_Throws()
    => Assert.Throws<InvalidDataException>(() => UnixPackBuildingBlock.Decode([0x1F, 0x9D, 0, 0, 0, 0, 1, 0]));

  [Test, Category("Exception")]
  public void Decode_LengthMismatch_Throws() {
    var bad = (byte[])AabVector.Clone();
    bad[5] = 4;
    Assert.Throws<InvalidDataException>(() => UnixPackBuildingBlock.Decode(bad));
  }

  [Test, Category("Exception")]
  public void Decode_TooManyLeaves_Throws() {
    // Length 1 cannot hold 3 leaves.
    Assert.Throws<InvalidDataException>(() => UnixPackBuildingBlock.Decode([0x1F, 0x1E, 0, 0, 0, 1, 1, 1, 0x41, 0x42, 0xFF]));
  }

  [Test, Category("Exception")]
  public void Decode_MissingEndCode_Throws() {
    var truncated = AabVector[..^1];
    Assert.Throws<InvalidDataException>(() => UnixPackBuildingBlock.Decode(truncated));
  }

  [Test, Category("Boundary")]
  public void Decode_CodeLengthZeroOrOver25_Throws() {
    Assert.Throws<InvalidDataException>(() => UnixPackBuildingBlock.Decode([0x1F, 0x1E, 0, 0, 0, 0, 0]));
    Assert.Throws<InvalidDataException>(() => UnixPackBuildingBlock.Decode([0x1F, 0x1E, 0, 0, 0, 0, 26]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsDetectedByMagic_AndRoundTrips() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var d = FormatRegistry.GetById("UnixPack");
    Assert.That(d, Is.Not.Null);
    Assert.That(d!.Extensions, Is.Empty, ".z belongs to Unix compress");
    var ops = (IStreamFormatOperations)new UnixPackFormatDescriptor();
    using var packed = new MemoryStream();
    ops.Compress(new MemoryStream("descriptor round trip"u8.ToArray()), packed);
    packed.Position = 0;
    using var plain = new MemoryStream();
    ops.Decompress(packed, plain);
    Assert.That(plain.ToArray(), Is.EqualTo("descriptor round trip"u8.ToArray()));
  }
}

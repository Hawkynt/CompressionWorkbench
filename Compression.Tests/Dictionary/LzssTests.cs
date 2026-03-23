using System.Text;
using Compression.Core.Dictionary.Lzss;
using Compression.Core.Dictionary.MatchFinders;

namespace Compression.Tests.Dictionary;

[TestFixture]
public class LzssTests {
  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_EmptyData() {
    var outputStream = new MemoryStream();
    var encoder = new LzssEncoder(outputStream);
    var finder = new HashChainMatchFinder(4096);

    encoder.Encode([], finder);

    outputStream.Position = 0;
    var decoder = new LzssDecoder(outputStream);
    var result = decoder.Decode(0);

    Assert.That(result, Is.Empty);
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SingleByte() {
    byte[] input = [0x42];

    var outputStream = new MemoryStream();
    var encoder = new LzssEncoder(outputStream);
    var finder = new HashChainMatchFinder(4096);

    encoder.Encode(input, finder);

    outputStream.Position = 0;
    var decoder = new LzssDecoder(outputStream);
    var result = decoder.Decode(input.Length);

    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_ShortText() {
    var input = Encoding.ASCII.GetBytes("Hello World!");

    var outputStream = new MemoryStream();
    var encoder = new LzssEncoder(outputStream);
    var finder = new HashChainMatchFinder(4096);

    encoder.Encode(input, finder);

    outputStream.Position = 0;
    var decoder = new LzssDecoder(outputStream);
    var result = decoder.Decode(input.Length);

    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepetitiveData() {
    var input = Encoding.ASCII.GetBytes("ABCABCABCABCABCABC");

    var outputStream = new MemoryStream();
    var encoder = new LzssEncoder(outputStream);
    var finder = new HashChainMatchFinder(4096);

    encoder.Encode(input, finder);

    outputStream.Position = 0;
    var decoder = new LzssDecoder(outputStream);
    var result = decoder.Decode(input.Length);

    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RandomData() {
    var rng = new Random(42);
    var input = new byte[500];
    rng.NextBytes(input);

    var outputStream = new MemoryStream();
    var encoder = new LzssEncoder(outputStream);
    var finder = new HashChainMatchFinder(4096);

    encoder.Encode(input, finder);

    outputStream.Position = 0;
    var decoder = new LzssDecoder(outputStream);
    var result = decoder.Decode(input.Length);

    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_RepeatedText() {
    var input = Encoding.ASCII.GetBytes(
      "The quick brown fox jumps over the lazy dog. " +
      "The quick brown fox jumps over the lazy dog.");

    var outputStream = new MemoryStream();
    var encoder = new LzssEncoder(outputStream);
    var finder = new HashChainMatchFinder(4096);

    encoder.Encode(input, finder);

    outputStream.Position = 0;
    var decoder = new LzssDecoder(outputStream);
    var result = decoder.Decode(input.Length);

    Assert.That(result, Is.EqualTo(input));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_CustomBitWidths() {
    var input = Encoding.ASCII.GetBytes("AAAAABBBBBAAAAAABBBBB");

    var distanceBits = 10;
    var lengthBits = 6;

    var outputStream = new MemoryStream();
    var encoder = new LzssEncoder(outputStream, distanceBits, lengthBits);
    var finder = new HashChainMatchFinder(1 << distanceBits);

    encoder.Encode(input, finder);

    outputStream.Position = 0;
    var decoder = new LzssDecoder(outputStream, distanceBits, lengthBits);
    var result = decoder.Decode(input.Length);

    Assert.That(result, Is.EqualTo(input));
  }
}

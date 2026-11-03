using System.Text;
using Compression.Registry;
using FileFormat.UuEncoding;

namespace Compression.Tests.UuEncoding;

[TestFixture]
public class XxEncodingTests {

  private static string EncodeToText(byte[] data, string name = "f") {
    using var encoded = new MemoryStream();
    XxEncoder.Encode(new MemoryStream(data), encoded, name);
    return Encoding.ASCII.GetString(encoded.ToArray());
  }

  private static byte[] Decode(string text) {
    var (_, _, data) = XxEncoder.Decode(new MemoryStream(Encoding.ASCII.GetBytes(text)));
    return data;
  }

  [Test, Category("Compatibility")]
  public void Encode_Cat_MatchesTheCanonicalVector() {
    // "Cat" = 0x43 0x61 0x74 -> sextets 16, 54, 5, 52 -> 'E' 'q' '3' 'o'; length 3 -> '1'.
    Assert.That(EncodeToText("Cat"u8.ToArray(), "cat.txt"), Is.EqualTo("begin 644 cat.txt\n1Eq3o\n+\nend\n"));
  }

  [Test, Category("Compatibility")]
  public void Encode_FullLine_UsesLengthCharacterH() {
    var text = EncodeToText(new byte[45]);
    var bodyLine = text.Split('\n')[1];
    Assert.Multiple(() => {
      Assert.That(bodyLine[0], Is.EqualTo('h'), "45 is alphabet index 45 = 'h'");
      Assert.That(bodyLine, Has.Length.EqualTo(61));
      Assert.That(bodyLine[1..], Is.EqualTo(new string('+', 60)));
    });
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  [TestCase(44)]
  [TestCase(45)]
  [TestCase(46)]
  [TestCase(90)]
  [TestCase(1000)]
  [Category("RoundTrip")]
  public void RoundTrip_LengthBoundaries(int length) {
    var data = new byte[length];
    new Random(length).NextBytes(data);
    Assert.That(Decode(EncodeToText(data)), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_AllByteValues_PreservesNameAndMode() {
    var data = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
    using var encoded = new MemoryStream();
    XxEncoder.Encode(new MemoryStream(data), encoded, "all.bin", mode: 0x1ED);
    encoded.Position = 0;
    var (name, mode, decoded) = XxEncoder.Decode(encoded);
    Assert.Multiple(() => {
      Assert.That(decoded, Is.EqualTo(data));
      Assert.That(name, Is.EqualTo("all.bin"));
      Assert.That(mode, Is.EqualTo(0x1ED), "octal 755");
    });
  }

  [Test, Category("HappyPath")]
  public void Decode_SkipsMailHeadersAndToleratesCrLf() {
    var text = "From: someone\r\nSubject: file\r\n\r\nbegin 600 cat.txt\r\n1Eq3o\r\n+\r\nend\r\n";
    Assert.That(Decode(text), Is.EqualTo("Cat"u8.ToArray()));
  }

  [Test, Category("HappyPath")]
  public void Decode_MissingTerminator_StopsAtEndOfInput() {
    Assert.That(Decode("begin 644 x\n1Eq3o\n"), Is.EqualTo("Cat"u8.ToArray()));
  }

  [Test, Category("Exception")]
  public void Decode_NoBeginLine_Throws() {
    Assert.Throws<InvalidDataException>(() => Decode("1Eq3o\n+\nend\n"));
  }

  [Test, Category("Exception")]
  public void Decode_UuencodedBody_IsRejectedNotMisdecoded() {
    // uuencode's "Cat" line uses '%' — outside the XX alphabet.
    Assert.Throws<InvalidDataException>(() => Decode("begin 644 x\n#0V%T\n`\nend\n"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_IsRegisteredByExtensionOnly() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var d = FormatRegistry.GetById("XxEncoding");
    Assert.That(d, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(d!.Extensions, Does.Contain(".xx").And.Contain(".xxe"));
      Assert.That(d.MagicSignatures, Is.Empty, "the 'begin ' header belongs to uuencode's magic");
    });
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_StreamRoundTrip() {
    var ops = (IStreamFormatOperations)new XxEncodingFormatDescriptor();
    var data = "stream round trip through the descriptor"u8.ToArray();
    using var encoded = new MemoryStream();
    ops.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    ops.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }
}

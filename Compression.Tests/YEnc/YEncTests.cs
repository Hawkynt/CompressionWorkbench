namespace Compression.Tests.YEnc;

[TestFixture]
public class YEncTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SimpleData() {
    var data = "Hello yEnc test data!"u8.ToArray();
    using var encoded = new MemoryStream();
    FileFormat.YEnc.YEncEncoder.Encode(encoded, "test.bin", data);
    encoded.Position = 0;
    var (fileName, size, crc, decoded) = FileFormat.YEnc.YEncDecoder.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
    Assert.That(fileName, Is.EqualTo("test.bin"));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_AllByteValues() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    using var encoded = new MemoryStream();
    FileFormat.YEnc.YEncEncoder.Encode(encoded, "binary.dat", data);
    encoded.Position = 0;
    var (_, _, _, decoded) = FileFormat.YEnc.YEncDecoder.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Encode_HasYbeginYend() {
    using var encoded = new MemoryStream();
    FileFormat.YEnc.YEncEncoder.Encode(encoded, "test.bin", "data"u8.ToArray());
    encoded.Position = 0;
    var text = new StreamReader(encoded).ReadToEnd();
    Assert.That(text, Does.StartWith("=ybegin "));
    Assert.That(text, Does.Contain("=yend "));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_LargeData() {
    var data = new byte[10000];
    new Random(42).NextBytes(data);
    using var encoded = new MemoryStream();
    FileFormat.YEnc.YEncEncoder.Encode(encoded, "large.bin", data);
    encoded.Position = 0;
    var (_, _, _, decoded) = FileFormat.YEnc.YEncDecoder.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test, Category("ThemVsUs")]
  public void Given_RawEightBitBody_When_Decoded_Then_BytesAboveAsciiSurvive() {
    // yEnc bodies are raw bytes: 0xAA is 0x80 + 42 and must decode to 0x80, not to U+FFFD.
    using var input = new MemoryStream();
    input.Write("=ybegin line=128 size=2 name=a.bin\r\n"u8);
    input.Write([0xAA, 0x29]); // 0x80, 0xFF
    input.Write("\r\n=yend size=2\r\n"u8);
    input.Position = 0;

    var (_, _, _, decoded) = FileFormat.YEnc.YEncDecoder.Decode(input);

    Assert.That(decoded, Is.EqualTo(new byte[] { 0x80, 0xFF }));
  }

  [Test, Category("ThemVsUs")]
  public void Given_ByteAboveAscii_When_Encoded_Then_BodyCarriesOneRawByte() {
    using var encoded = new MemoryStream();
    FileFormat.YEnc.YEncEncoder.Encode(encoded, "a.bin", [0x80]);
    var bytes = encoded.ToArray();

    var bodyStart = Array.IndexOf(bytes, (byte)'\n') + 1;
    Assert.That(bytes[bodyStart], Is.EqualTo(0xAA));
    Assert.That(bytes[bodyStart + 1], Is.EqualTo((byte)'\r'));
  }

  [Test, Category("EdgeCase")]
  public void Given_NonAsciiFileName_When_RoundTripped_Then_NameSurvives() {
    using var encoded = new MemoryStream();
    FileFormat.YEnc.YEncEncoder.Encode(encoded, "\u00FCber-\u65E5\u672C.bin", [1, 2, 3]);
    encoded.Position = 0;

    var (fileName, _, _, decoded) = FileFormat.YEnc.YEncDecoder.Decode(encoded);

    Assert.That(fileName, Is.EqualTo("\u00FCber-\u65E5\u672C.bin"));
    Assert.That(decoded, Is.EqualTo(new byte[] { 1, 2, 3 }));
  }
}

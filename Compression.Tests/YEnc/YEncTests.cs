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
}

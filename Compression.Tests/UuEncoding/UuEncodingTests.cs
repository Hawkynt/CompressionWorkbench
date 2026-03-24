namespace Compression.Tests.UuEncoding;

[TestFixture]
public class UuEncodingTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SimpleData() {
    var data = "Hello, UUEncoding test data!"u8.ToArray();
    using var encoded = new MemoryStream();
    FileFormat.UuEncoding.UuEncoder.Encode(new MemoryStream(data), encoded, "test.bin");
    encoded.Position = 0;
    var (fileName, _, decoded) = FileFormat.UuEncoding.UuEncoder.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
    Assert.That(fileName, Is.EqualTo("test.bin"));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_BinaryData() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    using var encoded = new MemoryStream();
    FileFormat.UuEncoding.UuEncoder.Encode(new MemoryStream(data), encoded, "binary.dat");
    encoded.Position = 0;
    var (_, _, decoded) = FileFormat.UuEncoding.UuEncoder.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Encode_ProducesBeginEnd() {
    using var encoded = new MemoryStream();
    FileFormat.UuEncoding.UuEncoder.Encode(new MemoryStream("test"u8.ToArray()), encoded, "file.txt");
    encoded.Position = 0;
    var text = new StreamReader(encoded).ReadToEnd();
    Assert.That(text, Does.StartWith("begin "));
    Assert.That(text, Does.Contain("end"));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_EmptyData() {
    using var encoded = new MemoryStream();
    FileFormat.UuEncoding.UuEncoder.Encode(new MemoryStream([]), encoded, "empty.bin");
    encoded.Position = 0;
    var (_, _, decoded) = FileFormat.UuEncoding.UuEncoder.Decode(encoded);
    Assert.That(decoded, Is.Empty);
  }
}

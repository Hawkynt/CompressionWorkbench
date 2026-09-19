using System.Text;
using Compression.Registry;
using FileFormat.UuEncoding;

namespace Compression.Tests.UuEncoding;

[TestFixture]
public class UuEncodingTests {

  [Test, Category("RoundTrip")]
  public void RoundTrip_SimpleData() {
    var data = "Hello, UUEncoding test data!"u8.ToArray();
    using var encoded = new MemoryStream();
    UuEncoder.Encode(new MemoryStream(data), encoded, "test.bin");
    encoded.Position = 0;
    var (fileName, _, decoded) = UuEncoder.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
    Assert.That(fileName, Is.EqualTo("test.bin"));
  }

  [Test, Category("RoundTrip")]
  public void RoundTrip_BinaryData() {
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    using var encoded = new MemoryStream();
    UuEncoder.Encode(new MemoryStream(data), encoded, "binary.dat");
    encoded.Position = 0;
    var (_, _, decoded) = UuEncoder.Decode(encoded);
    Assert.That(decoded, Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void Encode_ProducesBeginEnd() {
    using var encoded = new MemoryStream();
    UuEncoder.Encode(new MemoryStream("test"u8.ToArray()), encoded, "file.txt");
    encoded.Position = 0;
    var text = new StreamReader(encoded).ReadToEnd();
    Assert.That(text, Does.StartWith("begin "));
    Assert.That(text, Does.Contain("end"));
  }

  [Test, Category("EdgeCase")]
  public void RoundTrip_EmptyData() {
    using var encoded = new MemoryStream();
    UuEncoder.Encode(new MemoryStream([]), encoded, "empty.bin");
    encoded.Position = 0;
    var (_, _, decoded) = UuEncoder.Decode(encoded);
    Assert.That(decoded, Is.Empty);
  }

  [Test, Category("Compatibility")]
  public void Base64Encoder_MatchesPublishedLibarchiveVector() {
    byte[] data = [(byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', (byte)'8', 0];
    using var encoded = new MemoryStream();
    UuEncoder.EncodeBase64(new MemoryStream(data), encoded);

    var text = Encoding.ASCII.GetString(encoded.ToArray());
    Assert.That(text, Is.EqualTo("begin-base64 644 -\nMTIzNDU2NzgA\n====\n"));
  }

  [Test, Category("RoundTrip")]
  public void Base64RoundTrip_BinaryData() {
    var data = new byte[1024];
    new Random(0xB64).NextBytes(data);

    // The mode is a permission word, and the header carries its octal text.
    // C# has no octal literal, so the value is spelled in hex: 0x180 is octal 600.
    const int Mode = 0x180;

    using var encoded = new MemoryStream();
    UuEncoder.EncodeBase64(new MemoryStream(data), encoded, "payload.bin", Mode);
    encoded.Position = 0;
    var (name, mode, decoded) = UuEncoder.Decode(encoded);

    Assert.Multiple(() => {
      Assert.That(name, Is.EqualTo("payload.bin"));
      Assert.That(Encoding.ASCII.GetString(encoded.ToArray()), Does.StartWith("begin-base64 600 payload.bin\n"));
      Assert.That(mode, Is.EqualTo(Mode));
      Assert.That(decoded, Is.EqualTo(data));
    });
  }

  [Test, Category("Boundary")]
  public void Base64Encoder_WrapsAt76Characters() {
    var data = new byte[58];
    new Random(0xB65).NextBytes(data);
    using var encoded = new MemoryStream();
    UuEncoder.EncodeBase64(new MemoryStream(data), encoded);

    var lines = Encoding.ASCII.GetString(encoded.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Assert.Multiple(() => {
      Assert.That(lines[0], Does.StartWith("begin-base64 "));
      Assert.That(lines[1].Length, Is.EqualTo(76));
      Assert.That(lines[2].Length, Is.EqualTo(4));
      Assert.That(lines[^1], Is.EqualTo("===="));
    });
  }

  [Test, Category("Registry")]
  public void Descriptors_ExposeDistinctClassicAndBase64Signatures() {
    var uu = new UuEncodingFormatDescriptor();
    var b64 = new B64EncodingFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(uu.MagicSignatures, Has.Count.EqualTo(1));
      Assert.That(b64.MagicSignatures, Has.Count.EqualTo(1));
      Assert.That(b64.Methods.Single().Name, Is.EqualTo("b64encode"));
      Assert.That(b64, Is.AssignableTo<IFormatOptionsSchema>());
    });
  }

  [Test, Category("Registry")]
  public void Base64Descriptor_HonorsNameAndModeOptions() {
    var descriptor = new B64EncodingFormatDescriptor();
    using var encoded = new MemoryStream();
    descriptor.Compress(
      new MemoryStream("x"u8.ToArray()),
      encoded,
      new FormatCreateOptions {
        FormatSpecific = new() {
          ["Name"] = "custom.bin",
          ["Mode"] = "600",
        },
      });

    var text = Encoding.ASCII.GetString(encoded.ToArray());
    Assert.That(text, Does.StartWith("begin-base64 600 custom.bin\n"));
  }
}

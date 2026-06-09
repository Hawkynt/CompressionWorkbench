using Compression.Registry;
using FileFormat.YEnc;

namespace Compression.Tests.YEnc;

/// <summary>
/// WORM contract tests for yEnc through the
/// <see cref="IStreamFormatOperations"/> descriptor surface.
/// </summary>
[TestFixture]
public class YEncWormTests {

  [Test, Category("HappyPath")]
  public void Descriptor_DeclaresWormCapability() {
    var d = new YEncFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Wrapper));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Compress_EmitsYbeginAndYend() {
    var d = new YEncFormatDescriptor();
    var data = "WORM payload for yEnc"u8.ToArray();
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    var text = new StreamReader(encoded, leaveOpen: true).ReadToEnd();
    Assert.That(text, Does.StartWith("=ybegin "));
    Assert.That(text, Does.Contain("=yend "));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_PreservesPayload() {
    var d = new YEncFormatDescriptor();
    var data = new byte[1024];
    new Random(11).NextBytes(data);
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_AllByteValues() {
    var d = new YEncFormatDescriptor();
    var data = new byte[256];
    for (var i = 0; i < 256; i++) data[i] = (byte)i;
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }
}

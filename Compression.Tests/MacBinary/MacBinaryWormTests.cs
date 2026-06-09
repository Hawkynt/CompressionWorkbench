using Compression.Registry;
using FileFormat.MacBinary;

namespace Compression.Tests.MacBinary;

/// <summary>
/// WORM contract tests for MacBinary through the
/// <see cref="IStreamFormatOperations"/> descriptor surface.
/// </summary>
[TestFixture]
public class MacBinaryWormTests {

  [Test, Category("HappyPath")]
  public void Descriptor_DeclaresWormCapability() {
    var d = new MacBinaryFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Wrapper));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Compress_ProducesMacBinaryWrapper() {
    var d = new MacBinaryFormatDescriptor();
    var data = "WORM payload for MacBinary"u8.ToArray();
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    Assert.That(encoded.Length, Is.GreaterThanOrEqualTo(128));
    encoded.Position = 0;
    Assert.That(MacBinaryReader.IsMacBinary(encoded), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_DataForkPreserved() {
    var d = new MacBinaryFormatDescriptor();
    var data = new byte[1000];
    new Random(23).NextBytes(data);
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_EmptyPayload() {
    var d = new MacBinaryFormatDescriptor();
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream([]), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.Empty);
  }
}

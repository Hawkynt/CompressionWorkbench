using Compression.Registry;
using FileFormat.BinHex;

namespace Compression.Tests.BinHex;

/// <summary>
/// WORM contract tests for BinHex 4.0 through the
/// <see cref="IStreamFormatOperations"/> descriptor surface.
/// </summary>
[TestFixture]
public class BinHexWormTests {

  [Test, Category("HappyPath")]
  public void Descriptor_DeclaresWormCapability() {
    var d = new BinHexFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Wrapper));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Compress_ProducesBinHexHeader() {
    var d = new BinHexFormatDescriptor();
    var data = "WORM payload for BinHex"u8.ToArray();
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var reader = new StreamReader(encoded, leaveOpen: true);
    var firstLine = reader.ReadLine();
    Assert.That(firstLine, Does.Contain("BinHex"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_DataForkPreserved() {
    var d = new BinHexFormatDescriptor();
    var data = new byte[300];
    new Random(13).NextBytes(data);
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_RleEscapeByte() {
    var d = new BinHexFormatDescriptor();
    var data = new byte[] { 0x90, 0x90, 0x00, 0x90, 0x41, 0x41, 0x41, 0x90 };
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }
}

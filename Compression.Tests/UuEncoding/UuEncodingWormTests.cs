using Compression.Registry;
using FileFormat.UuEncoding;

namespace Compression.Tests.UuEncoding;

/// <summary>
/// WORM contract tests for UuEncoding through the
/// <see cref="IStreamFormatOperations"/> descriptor surface — the layer the
/// CLI/UI uses to wrap a single payload as a fresh archive.
/// </summary>
[TestFixture]
public class UuEncodingWormTests {

  [Test, Category("HappyPath")]
  public void Descriptor_DeclaresWormCapability() {
    var d = new UuEncodingFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Wrapper));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Compress_ProducesUuStream() {
    var d = new UuEncodingFormatDescriptor();
    var data = "WORM payload for UuEncoding"u8.ToArray();
    using var input = new MemoryStream(data);
    using var output = new MemoryStream();
    d.Compress(input, output);
    output.Position = 0;
    using var reader = new StreamReader(output, leaveOpen: true);
    Assert.That(reader.ReadLine(), Does.StartWith("begin "));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_PreservesPayload() {
    var d = new UuEncodingFormatDescriptor();
    var data = new byte[512];
    new Random(7).NextBytes(data);
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream(data), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.EqualTo(data));
  }

  [Test, Category("EdgeCase"), Category("RoundTrip")]
  public void Descriptor_RoundTrip_EmptyPayload() {
    var d = new UuEncodingFormatDescriptor();
    using var encoded = new MemoryStream();
    d.Compress(new MemoryStream([]), encoded);
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    d.Decompress(encoded, decoded);
    Assert.That(decoded.ToArray(), Is.Empty);
  }
}

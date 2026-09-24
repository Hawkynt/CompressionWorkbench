using Compression.Registry;
using FileFormat.Yaz0;

namespace Compression.Tests.Operations;

[TestFixture]
public class CompressionOptimizationCapabilityTests {
  [Test, Category("RoundTrip")]
  public void Yaz0_OptimizeCompression_DecodesThenReencodesPayload() {
    var descriptor = new Yaz0FormatDescriptor();
    var payload = Enumerable.Range(0, 4096)
      .Select(index => (byte)((index / 32) & 0xFF))
      .ToArray();

    using var source = new MemoryStream();
    descriptor.Compress(new MemoryStream(payload, writable: false), source);
    source.Position = 0;

    using var optimized = new MemoryStream();
    ((ICompressionOptimizable)descriptor).OptimizeCompression(source, optimized);

    optimized.Position = 0;
    using var decoded = new MemoryStream();
    descriptor.Decompress(optimized, decoded);

    Assert.That(decoded.ToArray(), Is.EqualTo(payload));
    Assert.That(optimized.ToArray().AsSpan(0, 4).ToArray(), Is.EqualTo("Yaz0"u8.ToArray()));
  }
}

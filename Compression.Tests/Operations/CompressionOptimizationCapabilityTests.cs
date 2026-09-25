using Compression.Registry;
using FileFormat.Yaz0;
using FileFormat.Zip;

namespace Compression.Tests.Operations;

[TestFixture]
public class CompressionOptimizationCapabilityTests {
  [Test, Category("RoundTrip")]
  public void Zip_OptimizeCompression_PreservesLogicalEntriesAndTimestamps() {
    var descriptor = new ZipFormatDescriptor();
    var timestamp = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Local);
    ArchiveInputInfo[] inputs = [
      new("", "empty/", true) { LastModified = timestamp },
      ArchiveInputInfo.InMemory("alpha.txt", Enumerable.Repeat((byte)'A', 8192).ToArray())
        with { LastModified = timestamp },
      ArchiveInputInfo.InMemory("nested/beta.bin", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray())
        with { LastModified = timestamp },
    ];

    using var source = new MemoryStream();
    descriptor.Create(source, inputs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> {
        ["Method"] = "store",
        ["Level"] = "0",
      },
    });
    source.Position = 0;
    var before = SemanticPreservationManifest.Capture(source, descriptor);

    source.Position = 0;
    using var optimized = new MemoryStream();
    ((ICompressionOptimizable)descriptor).OptimizeCompression(source, optimized);

    optimized.Position = 0;
    var after = SemanticPreservationManifest.Capture(optimized, descriptor);
    before.VerifyEquivalent(after);
    Assert.That(optimized.Length, Is.LessThan(source.Length));
  }

  [Test, Category("Negative")]
  public void Zip_OptimizeCompression_RejectsEncryptedArchiveWithoutPassword() {
    var descriptor = new ZipFormatDescriptor();
    using var source = new MemoryStream();
    descriptor.Create(source,
      [ArchiveInputInfo.InMemory("secret.txt", "classified"u8.ToArray())],
      new FormatCreateOptions { Password = "test-password", EncryptionMethod = "zipcrypto" });

    source.Position = 0;
    using var optimized = new MemoryStream();

    Assert.That(
      () => ((ICompressionOptimizable)descriptor).OptimizeCompression(source, optimized),
      Throws.TypeOf<NotSupportedException>());
  }

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

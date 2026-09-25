using Compression.Registry;
using FileFormat.Yaz0;
using FileFormat.Zip;

namespace Compression.Tests.Operations;

[TestFixture]
public class CompressionOptimizationCapabilityTests {
  [Test, Category("RoundTrip")]
  public void TarGz_OptimizeCompression_LeavesDecodedTarByteIdentical() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var descriptor = FormatRegistry.GetById("TarGz");
    Assert.That(descriptor, Is.InstanceOf<ICompressionOptimizable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());

    var creator = (IArchiveCreatable)descriptor!;
    var compression = (ICompressionOptimizable)descriptor;
    ArchiveInputInfo[] inputs = [
      new("", "empty/", true) { LastModified = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc) },
      ArchiveInputInfo.InMemory("payload.txt", Enumerable.Repeat((byte)'A', 8192).ToArray()),
    ];

    using var source = new MemoryStream();
    creator.Create(source, inputs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockingFactor"] = "1" },
    });

    var gzip = FormatRegistry.GetStreamOps("Gzip")!;
    source.Position = 0;
    using var beforeTar = new MemoryStream();
    gzip.Decompress(source, beforeTar);

    source.Position = 0;
    using var optimized = new MemoryStream();
    compression.OptimizeCompression(source, optimized);

    optimized.Position = 0;
    using var afterTar = new MemoryStream();
    gzip.Decompress(optimized, afterTar);

    Assert.That(afterTar.ToArray(), Is.EqualTo(beforeTar.ToArray()),
      "Compress on a compound TAR must only re-encode the outer stream; the TAR representation itself must not be rebuilt.");
  }

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
  public void Zip_OptimizeCompression_RejectsSameInputAndOutputStream() {
    var descriptor = new ZipFormatDescriptor();
    using var archive = new MemoryStream();
    descriptor.Create(
      archive,
      [ArchiveInputInfo.InMemory("payload.txt", "payload"u8.ToArray())],
      new FormatCreateOptions());
    archive.Position = 0;

    Assert.That(
      () => ((ICompressionOptimizable)descriptor).OptimizeCompression(archive, archive),
      Throws.TypeOf<ArgumentException>());
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

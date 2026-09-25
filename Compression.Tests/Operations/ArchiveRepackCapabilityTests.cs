using Compression.Registry;
using FileFormat.Zip;
using FileFormat.SevenZip;

namespace Compression.Tests.Operations;

[TestFixture]
public class ArchiveRepackCapabilityTests {
  [Test, Category("RoundTrip")]
  public void SevenZip_Repack_PreservesEmptyDirectoriesPayloadsAndTimestamps() {
    var descriptor = new SevenZipFormatDescriptor();
    var timestamp = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);
    ArchiveInputInfo[] inputs = [
      new("", "empty/", true) { LastModified = timestamp },
      ArchiveInputInfo.InMemory("alpha.txt", "alpha payload"u8.ToArray()) with { LastModified = timestamp },
      ArchiveInputInfo.InMemory("nested/beta.bin", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray())
        with { LastModified = timestamp },
    ];

    using var source = new MemoryStream();
    descriptor.Create(source, inputs, new FormatCreateOptions());
    source.Position = 0;
    var before = SemanticPreservationManifest.Capture(source, descriptor);

    source.Position = 0;
    using var repacked = new MemoryStream();
    ((IArchiveRepackable)descriptor).Repack(source, repacked);

    repacked.Position = 0;
    var after = SemanticPreservationManifest.Capture(repacked, descriptor);
    before.VerifyEquivalent(after);

    repacked.Position = 0;
    var entries = descriptor.List(repacked, null);
    Assert.That(entries.Any(entry => entry.IsDirectory && entry.Name.TrimEnd('/') == "empty"), Is.True);
    Assert.That(entries.Where(entry => !entry.IsDirectory).All(entry => entry.LastModified == timestamp), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Zip_Repack_PreservesEmptyDirectoriesPayloadsAndTimestamps() {
    var descriptor = new ZipFormatDescriptor();
    var timestamp = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Local);
    ArchiveInputInfo[] inputs = [
      new("", "empty/", true) { LastModified = timestamp },
      ArchiveInputInfo.InMemory("alpha.txt", "alpha payload"u8.ToArray()) with { LastModified = timestamp },
      ArchiveInputInfo.InMemory("nested/beta.bin", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray())
        with { LastModified = timestamp },
    ];

    using var source = new MemoryStream();
    descriptor.Create(source, inputs, new FormatCreateOptions());
    source.Position = 0;
    var before = SemanticPreservationManifest.Capture(source, descriptor);

    source.Position = 0;
    using var repacked = new MemoryStream();
    ((IArchiveRepackable)descriptor).Repack(source, repacked);

    repacked.Position = 0;
    var after = SemanticPreservationManifest.Capture(repacked, descriptor);
    before.VerifyEquivalent(after);

    repacked.Position = 0;
    var entries = descriptor.List(repacked, null);
    Assert.That(entries.Any(entry => entry.IsDirectory && entry.Name.TrimEnd('/') == "empty"), Is.True);
    Assert.That(entries.Where(entry => !entry.IsDirectory).All(entry => entry.LastModified == timestamp), Is.True);
  }
}

using Compression.Registry;
using FileFormat.Zip;
using FileFormat.SevenZip;
using FileFormat.Tar;
using FileFormat.Cb7;

namespace Compression.Tests.Operations;

[TestFixture]
public class ArchiveRepackCapabilityTests {
  [Test, Category("RoundTrip")]
  public void Cb7_Repack_PreservesEmptyDirectoriesPayloadsAndTimestamps() {
    var descriptor = new Cb7FormatDescriptor();
    var timestamp = new DateTime(2024, 7, 8, 9, 10, 12, DateTimeKind.Utc);
    ArchiveInputInfo[] inputs = [
      new("", "empty/", true) { LastModified = timestamp },
      ArchiveInputInfo.InMemory("001.png", "page-one"u8.ToArray()) with { LastModified = timestamp },
      ArchiveInputInfo.InMemory("chapter/002.png", "page-two"u8.ToArray()) with { LastModified = timestamp },
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
  }

  [Test, Category("RoundTrip")]
  public void Tar_Repack_PreservesLinksOwnershipModesAndPayloads() {
    var descriptor = new TarFormatDescriptor();
    var timestamp = DateTimeOffset.FromUnixTimeSeconds(1_714_973_290);

    using var source = new MemoryStream();
    using (var writer = new TarWriter(source, leaveOpen: true, format: TarHeaderFormat.Pax, blockingFactor: 1)) {
      writer.AddEntry(new TarEntry {
        Name = "data/",
        TypeFlag = TarConstants.TypeDirectory,
        Mode = 0x1ED,
        Uid = 1001,
        Gid = 1002,
        UserName = "alice",
        GroupName = "staff",
        ModifiedTime = timestamp,
      }, []);

      writer.AddEntry(new TarEntry {
        Name = "data/payload.txt",
        TypeFlag = TarConstants.TypeRegular,
        Mode = 0x1A4,
        Uid = 1001,
        Gid = 1002,
        UserName = "alice",
        GroupName = "staff",
        ModifiedTime = timestamp,
      }, "payload"u8.ToArray());

      writer.AddEntry(new TarEntry {
        Name = "latest",
        TypeFlag = TarConstants.TypeSymLink,
        LinkName = "data/payload.txt",
        Mode = 0x1FF,
        Uid = 1001,
        Gid = 1002,
        UserName = "alice",
        GroupName = "staff",
        ModifiedTime = timestamp,
      }, []);

      writer.AddEntry(new TarEntry {
        Name = "payload-hardlink",
        TypeFlag = TarConstants.TypeHardLink,
        LinkName = "data/payload.txt",
        Mode = 0x1A4,
        Uid = 1001,
        Gid = 1002,
        UserName = "alice",
        GroupName = "staff",
        ModifiedTime = timestamp,
      }, []);

      writer.Finish();
    }

    source.Position = 0;
    var before = SemanticPreservationManifest.Capture(source, descriptor);
    source.Position = 0;

    using var repacked = new MemoryStream();
    ((IArchiveRepackable)descriptor).Repack(source, repacked);

    repacked.Position = 0;
    var after = SemanticPreservationManifest.Capture(repacked, descriptor);
    before.VerifyEquivalent(after);

    repacked.Position = 0;
    using var reader = new TarReader(repacked);
    var entries = new List<TarEntry>();
    while (reader.GetNextEntry() is { } entry) {
      entries.Add(entry);
      reader.Skip();
    }

    Assert.Multiple(() => {
      var file = entries.Single(entry => entry.Name == "data/payload.txt");
      Assert.That(file.Mode, Is.EqualTo(0x1A4));
      Assert.That(file.Uid, Is.EqualTo(1001));
      Assert.That(file.Gid, Is.EqualTo(1002));
      Assert.That(file.UserName, Is.EqualTo("alice"));
      Assert.That(file.GroupName, Is.EqualTo("staff"));

      var symlink = entries.Single(entry => entry.Name == "latest");
      Assert.That(symlink.TypeFlag, Is.EqualTo(TarConstants.TypeSymLink));
      Assert.That(symlink.LinkName, Is.EqualTo("data/payload.txt"));

      var hardlink = entries.Single(entry => entry.Name == "payload-hardlink");
      Assert.That(hardlink.TypeFlag, Is.EqualTo(TarConstants.TypeHardLink));
      Assert.That(hardlink.LinkName, Is.EqualTo("data/payload.txt"));
    });
  }

  [Test, Category("RoundTrip")]
  public void TarGz_Repack_PreservesRichTarMetadata() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var descriptor = FormatRegistry.GetById("TarGz")!;
    var creator = (IArchiveCreatable)descriptor;
    var repackable = (IArchiveRepackable)descriptor;
    var operations = (IArchiveFormatOperations)descriptor;

    // Build the rich TAR first, then wrap it with gzip so the source carries
    // metadata that ArchiveInputInfo alone cannot represent.
    var tar = new TarFormatDescriptor();
    using var rawTar = new MemoryStream();
    using (var writer = new TarWriter(rawTar, leaveOpen: true, format: TarHeaderFormat.Pax, blockingFactor: 1)) {
      writer.AddEntry(new TarEntry {
        Name = "payload.txt",
        TypeFlag = TarConstants.TypeRegular,
        Mode = 0x1A0,
        Uid = 42,
        Gid = 43,
        UserName = "user",
        GroupName = "group",
        ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_714_973_290),
      }, "payload"u8.ToArray());
      writer.AddEntry(new TarEntry {
        Name = "link",
        TypeFlag = TarConstants.TypeSymLink,
        LinkName = "payload.txt",
        Mode = 0x1FF,
        Uid = 42,
        Gid = 43,
        UserName = "user",
        GroupName = "group",
        ModifiedTime = DateTimeOffset.FromUnixTimeSeconds(1_714_973_290),
      }, []);
      writer.Finish();
    }

    var gzip = FormatRegistry.GetStreamOps("Gzip")!;
    rawTar.Position = 0;
    using var source = new MemoryStream();
    gzip.Compress(rawTar, source);

    source.Position = 0;
    var before = SemanticPreservationManifest.Capture(source, operations);
    source.Position = 0;

    using var repacked = new MemoryStream();
    repackable.Repack(source, repacked);

    repacked.Position = 0;
    var after = SemanticPreservationManifest.Capture(repacked, operations);
    before.VerifyEquivalent(after);
  }

  [Test, Category("RoundTrip")]
  public void TarGz_Repack_PreservesLogicalTarModel() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var descriptor = FormatRegistry.GetById("TarGz");
    Assert.That(descriptor, Is.InstanceOf<IArchiveRepackable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveFormatOperations>());

    var operations = (IArchiveFormatOperations)descriptor!;
    var creator = (IArchiveCreatable)descriptor;
    var repackable = (IArchiveRepackable)descriptor;
    var timestamp = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);
    ArchiveInputInfo[] inputs = [
      new("", "empty/", true) { LastModified = timestamp },
      ArchiveInputInfo.InMemory("alpha.txt", "alpha payload"u8.ToArray()) with { LastModified = timestamp },
    ];

    using var source = new MemoryStream();
    creator.Create(source, inputs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockingFactor"] = "1" },
    });
    source.Position = 0;
    var before = SemanticPreservationManifest.Capture(source, operations);

    source.Position = 0;
    using var repacked = new MemoryStream();
    repackable.Repack(source, repacked);

    repacked.Position = 0;
    var after = SemanticPreservationManifest.Capture(repacked, operations);
    before.VerifyEquivalent(after);
  }

  [Test, Category("RoundTrip")]
  public void Tar_Repack_PreservesEmptyDirectoriesPayloadsAndTimestamps() {
    var descriptor = new TarFormatDescriptor();
    var timestamp = new DateTime(2024, 5, 6, 7, 8, 10, DateTimeKind.Utc);
    ArchiveInputInfo[] inputs = [
      new("", "empty/", true) { LastModified = timestamp },
      ArchiveInputInfo.InMemory("alpha.txt", "alpha payload"u8.ToArray()) with { LastModified = timestamp },
      ArchiveInputInfo.InMemory("nested/beta.bin", Enumerable.Range(0, 256).Select(i => (byte)i).ToArray())
        with { LastModified = timestamp },
    ];

    using var source = new MemoryStream();
    descriptor.Create(source, inputs, new FormatCreateOptions {
      FormatSpecific = new Dictionary<string, string> { ["BlockingFactor"] = "1" },
    });
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
    Assert.That(entries.All(entry => entry.LastModified == timestamp), Is.True);
  }

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

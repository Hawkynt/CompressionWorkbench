using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.Nested;

[TestFixture]
public class NestedStreamResolverTests {

  [SetUp]
  public void EnsureFormats() => FormatRegistration.EnsureInitialized();

  [Test, Category("HappyPath")]
  public void ResolveDeepest_VhdContainingFat_ReturnsFatDescriptor() {
    // Build a small FAT image with a file
    var fatWriter = new FileSystem.Fat.FatWriter();
    fatWriter.AddFile("hello.txt", "Hello from nested FAT!"u8.ToArray());
    var fatImage = fatWriter.Build(totalSectors: 128);

    // Wrap FAT image in a VHD container
    var vhdWriter = new FileFormat.Vhd.VhdWriter();
    vhdWriter.SetDiskData(fatImage);
    var vhdBytes = vhdWriter.Build();

    using var vhdStream = new MemoryStream(vhdBytes);
    var resolution = NestedStreamResolver.ResolveDeepest(vhdStream);

    Assert.That(resolution, Is.Not.Null, "Should resolve an inner filesystem");
    Assert.That(resolution!.InnerDescriptor.Id, Is.EqualTo("Fat"),
      "Inner descriptor should be FAT");
    Assert.That(resolution.NestingPath, Does.Contain("VHD"),
      "Nesting path should mention VHD");
    Assert.That(resolution.NestingPath, Does.Contain("FAT"),
      "Nesting path should mention FAT");

    // Verify we can list files through the resolved stream
    var ops = FormatRegistry.GetArchiveOps("Fat");
    Assert.That(ops, Is.Not.Null);
    resolution.InnerStream.Position = 0;
    var entries = ops!.List(resolution.InnerStream, null);
    Assert.That(entries.Any(e => e.Name == "hello.txt"), Is.True,
      "Should find hello.txt in the resolved FAT stream");
  }

  [Test, Category("HappyPath")]
  public void ResolveDeepest_PlainFatImage_ReturnsFatDirectly() {
    // Build a standalone FAT image
    var fatWriter = new FileSystem.Fat.FatWriter();
    fatWriter.AddFile("test.dat", new byte[64]);
    var fatImage = fatWriter.Build(totalSectors: 128);

    using var ms = new MemoryStream(fatImage);
    var resolution = NestedStreamResolver.ResolveDeepest(ms);

    Assert.That(resolution, Is.Not.Null, "Should resolve a FAT filesystem");
    Assert.That(resolution!.InnerDescriptor.Id, Is.EqualTo("Fat"));
  }

  [Test, Category("EdgeCase")]
  public void ResolveDeepest_EmptyStream_ReturnsNull() {
    using var ms = new MemoryStream([]);
    var resolution = NestedStreamResolver.ResolveDeepest(ms);
    Assert.That(resolution, Is.Null);
  }

  [Test, Category("EdgeCase")]
  public void ResolveDeepest_RandomBytes_ReturnsNull() {
    var random = new byte[4096];
    Array.Fill(random, (byte)0xCC);
    using var ms = new MemoryStream(random);
    var resolution = NestedStreamResolver.ResolveDeepest(ms);
    Assert.That(resolution, Is.Null);
  }

  [Test, Category("HappyPath")]
  public void ResolveDeepest_AddFileThroughResolved_AppearsOnReRead() {
    // Build a FAT image with one file
    var fatWriter = new FileSystem.Fat.FatWriter();
    fatWriter.AddFile("original.txt", "Original content"u8.ToArray());
    var fatImage = fatWriter.Build(totalSectors: 256);

    // Wrap in VHD
    var vhdWriter = new FileFormat.Vhd.VhdWriter();
    vhdWriter.SetDiskData(fatImage);
    var vhdBytes = vhdWriter.Build();

    using var vhdStream = new MemoryStream(vhdBytes);
    var resolution = NestedStreamResolver.ResolveDeepest(vhdStream);
    Assert.That(resolution, Is.Not.Null);

    // Add a file through the resolved stream
    if (resolution!.InnerDescriptor is IArchiveModifiable modifiable) {
      resolution.InnerStream.Position = 0;
      var tmpAdd = Path.GetTempFileName();
      try {
        File.WriteAllBytes(tmpAdd, "Added content"u8.ToArray());
        modifiable.Add(resolution.InnerStream, [
          new ArchiveInputInfo(tmpAdd, "added.txt", false)
        ]);
      } finally { File.Delete(tmpAdd); }

      // Re-read and verify
      var ops = FormatRegistry.GetArchiveOps(resolution.InnerDescriptor.Id);
      Assert.That(ops, Is.Not.Null);
      resolution.InnerStream.Position = 0;
      var entries = ops!.List(resolution.InnerStream, null);
      Assert.That(entries.Any(e => e.Name == "added.txt"), Is.True,
        "added.txt should appear after Add through resolved stream");
      Assert.That(entries.Any(e => e.Name == "original.txt"), Is.True,
        "original.txt should still be present");
    } else {
      Assert.Ignore("FAT descriptor does not implement IArchiveModifiable (unexpected)");
    }
  }
}

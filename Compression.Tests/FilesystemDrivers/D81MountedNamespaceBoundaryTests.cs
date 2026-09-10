using Compression.Lib;
using Compression.Registry;
using FileSystem.D81;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
public sealed class D81MountedNamespaceBoundaryTests {
  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [Test]
  public void PartitionEntry_FailsMountedProbeInsteadOfFlatteningNamespace() {
    var writer = new D81Writer();
    writer.AddFile("PART", [1, 2, 3]);
    var imageBytes = writer.Build("PART", "81");

    var directoryOffset = ((40 - 1) * 40 + 3) * 256;
    imageBytes[directoryOffset + 2] = 0x85; // closed CBM partition/subdirectory entry

    using var image = new MemoryStream(imageBytes, writable: true);
    var profile = FormatRegistry.ProbeFilesystem("D81", image);
    Assert.Multiple(() => {
      Assert.That(profile.CanMount, Is.False);
      Assert.That(profile.CanMountWritable, Is.False);
      Assert.That(profile.Limitations.Any(x => x.Contains("partition", StringComparison.OrdinalIgnoreCase)), Is.True);
    });
  }
}

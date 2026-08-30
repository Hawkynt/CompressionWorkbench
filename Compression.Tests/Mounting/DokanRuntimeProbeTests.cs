using Compression.Mounting.Dokan;
using Compression.Registry;

namespace Compression.Tests.Mounting;

[TestFixture]
public sealed class DokanRuntimeProbeTests {
  [Test]
  public void BackendNeverAdvertisesUnimplementedMountModes() {
    var backend = new DokanFilesystemMountBackend(
      new DokanRuntimeStatus(
        IsAvailable: true,
        LibraryVersion: 210,
        DriverVersion: 210,
        LibraryPath: "dokan2.dll",
        UnavailableReason: null
      )
    );

    var profile = backend.GetProfile();

    Assert.Multiple(() => {
      Assert.That(profile.Id, Is.EqualTo("dokan"));
      Assert.That(profile.IsAvailable, Is.True);
      Assert.That(profile.SupportsReadOnly, Is.False);
      Assert.That(profile.SupportsReadWrite, Is.False);
      Assert.That(profile.RequiredReadCapabilities, Is.EqualTo(FilesystemDriverCapabilities.None));
      Assert.That(profile.RequiredWriteCapabilities, Is.EqualTo(FilesystemDriverCapabilities.None));
      Assert.That(
        profile.Limitations.Any(static limitation => limitation.Contains("intentionally disabled", StringComparison.Ordinal)),
        Is.True
      );
    });
  }

  [Test]
  public void RuntimeAvailabilityRequiresBothLibraryAndDriverVersions() {
    var status = DokanRuntimeProbe.Probe();

    Assert.Multiple(() => {
      Assert.That(status.IsAvailable, Is.EqualTo(
        OperatingSystem.IsWindows() && status.LibraryVersion != 0 && status.DriverVersion != 0
      ));
      if (status.IsAvailable) {
        Assert.That(status.LibraryPath, Is.Not.Null.And.Not.Empty);
        Assert.That(status.UnavailableReason, Is.Null);
      } else {
        Assert.That(status.UnavailableReason, Is.Not.Null.And.Not.Empty);
      }
    });
  }
}

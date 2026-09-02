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
      // Read-only mounting is implemented and MountAsync serves it; read-write
      // is not, and MountAsync refuses every access mode but ReadOnly. The
      // profile has to say exactly that — the point of this test is that the
      // advertised modes never run ahead of the callbacks behind them.
      Assert.That(profile.SupportsReadOnly, Is.True);
      Assert.That(profile.SupportsReadWrite, Is.False);
      Assert.That(profile.RequiredReadCapabilities, Is.EqualTo(FilesystemDriverCapabilities.None));
      Assert.That(profile.RequiredWriteCapabilities, Is.EqualTo(FilesystemDriverCapabilities.None));
      Assert.That(
        profile.Limitations.Any(static limitation =>
          limitation.Contains("Read-write Dokan mounting is disabled", StringComparison.Ordinal)),
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

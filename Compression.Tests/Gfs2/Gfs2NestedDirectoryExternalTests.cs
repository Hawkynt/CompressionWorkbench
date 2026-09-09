using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// Independent gates for GFS2 ExHash output. The reference checker validates
/// metadata/accounting, while a real Linux GFS2 mount validates that the kernel
/// can resolve the emitted hash table, leaves and nested namespace.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class Gfs2NestedDirectoryExternalTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_gfs2_exhash_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void Writer_ExHashDirectory_IsAcceptedByFsckGfs2() {
    RequireGfs2Utils();
    var (imagePath, _, _) = BuildExHashImage();

    var fsck = FsInteropToolbox.RunWsl(
      $"fsck.gfs2 -n {FsInteropToolbox.WinToWsl(imagePath)}");
    Assert.That(fsck.ExitCode, Is.EqualTo(0),
      $"fsck.gfs2 rejected ExHash output:\nstdout:\n{fsck.StdOut}\nstderr:\n{fsck.StdErr}");
    Assert.That(fsck.StdOut, Does.Contain("complete").IgnoreCase,
      $"fsck.gfs2 did not report a complete check:\n{fsck.StdOut}");
    foreach (var complaint in new[] { "failed", "damage", "cannot fix", "does not match", "corrupt" })
      Assert.That(fsck.StdOut, Does.Not.Contain(complaint).IgnoreCase,
        $"fsck.gfs2 reported '{complaint}':\n{fsck.StdOut}");
  }

  [Test, Category("DriverProof")]
  public void Writer_ExHashDirectory_LinuxKernelResolvesNamespaceAndPayload() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("A Linux environment is required for the GFS2 kernel namespace oracle.");
    if (!FsInteropToolbox.WslHasPasswordlessSudo) {
      if (OperatingSystem.IsLinux())
        Assert.Fail("Linux GFS2 namespace verification requires passwordless sudo for the read-only loop mount.");
      Assert.Ignore("Passwordless sudo is required for the read-only loop mount namespace oracle.");
    }

    var driver = FsInteropToolbox.RunWsl(
      "grep -qw gfs2 /proc/filesystems 2>/dev/null || " +
      "sudo -n modprobe gfs2 >/dev/null 2>&1");
    if (driver.ExitCode != 0) {
      if (OperatingSystem.IsLinux())
        Assert.Fail("The Linux GFS2 kernel driver is unavailable; ExHash namespace verification did not run.");
      Assert.Ignore("The Linux GFS2 kernel driver is not available in this WSL kernel.");
    }

    var (imagePath, markerPath, markerPayloadPath) = BuildExHashImage();
    var image = FsInteropToolbox.WinToWsl(imagePath);
    var expected = FsInteropToolbox.WinToWsl(markerPayloadPath);
    var escapedMarker = markerPath.Replace("'", "'\\''", StringComparison.Ordinal);

    var script =
      "set -e; " +
      "MNT=$(mktemp -d); " +
      "cleanup() { sudo -n umount \"$MNT\" >/dev/null 2>&1 || true; rmdir \"$MNT\" >/dev/null 2>&1 || true; }; " +
      "trap cleanup EXIT; " +
      $"sudo -n mount -t gfs2 -o loop,ro,lockproto=lock_nolock {image} \"$MNT\"; " +
      "test -d \"$MNT/crowded\"; " +
      "test \"$(find \"$MNT/crowded\" -maxdepth 1 -type f | wc -l)\" -eq 96; " +
      $"test -f \"$MNT/{escapedMarker}\"; " +
      $"cmp \"$MNT/{escapedMarker}\" {expected}; " +
      $"printf '%s\\n' '{escapedMarker}'";

    var mounted = FsInteropToolbox.RunWsl(script);
    Assert.That(mounted.ExitCode, Is.EqualTo(0),
      $"Linux GFS2 mount could not resolve/read our ExHash namespace:\nstdout:\n{mounted.StdOut}\nstderr:\n{mounted.StdErr}");
    Assert.That(mounted.StdOut, Does.Contain(markerPath));
  }

  private (string ImagePath, string MarkerPath, string MarkerPayloadPath) BuildExHashImage() {
    var writer = new Gfs2Writer(sizeBytes: 64L * 1024 * 1024);
    string? markerPath = null;
    byte[]? markerPayload = null;

    for (var i = 0; i < 96; ++i) {
      var name = $"crowded/file-{i:D2}-{new string((char)('a' + i % 26), 32)}.bin";
      var payload = Enumerable.Range(0, 17 + i % 23).Select(n => (byte)(n * 13 + i)).ToArray();
      writer.AddFile(name, payload);
      if (i == 73) {
        markerPath = name;
        markerPayload = payload;
      }
    }

    var imagePath = Path.Combine(this._tmpDir, "exhash.gfs2");
    File.WriteAllBytes(imagePath, writer.Build());

    var markerPayloadPath = Path.Combine(this._tmpDir, "marker.bin");
    File.WriteAllBytes(markerPayloadPath, markerPayload!);
    return (imagePath, markerPath!, markerPayloadPath);
  }

  private static void RequireGfs2Utils() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("A Linux environment is required for fsck.gfs2.");
    if (!FsInteropToolbox.WslHasTool("fsck.gfs2")) {
      if (OperatingSystem.IsLinux())
        Assert.Fail("fsck.gfs2 is missing; install gfs2-utils so the ExHash conformance gate actually runs.");
      Assert.Ignore("gfs2-utils not found. Install with `sudo apt install -y gfs2-utils`.");
    }
  }
}

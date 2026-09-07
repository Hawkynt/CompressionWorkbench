using FileSystem.Gfs2;

namespace Compression.Tests.Gfs2;

/// <summary>
/// Oracle gate for the nested-directory writer. The image must be accepted by
/// the real gfs2-utils checker rather than merely agreeing with our own reader.
/// </summary>
[TestFixture]
[Category("ExternalFsInterop")]
public class Gfs2NestedDirectoryExternalTests {

  [Test, Category("HappyPath")]
  public void Writer_NestedStuffedDirectories_AreAcceptedByFsckGfs2() {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed; gfs2-utils oracle unavailable.");
    if (!FsInteropToolbox.WslHasTool("fsck.gfs2"))
      Assert.Ignore("gfs2-utils not found in WSL; install it to run the GFS2 oracle.");

    var path = Path.Combine(Path.GetTempPath(), "cwb_gfs2_nested_" + Guid.NewGuid().ToString("N") + ".gfs2");
    try {
      var writer = new Gfs2Writer();
      writer.AddFile("one/two/stuffed.bin", [1, 2, 3, 4]);
      writer.AddFile("one/other/indirect.bin", Enumerable.Range(0, 9_000).Select(i => (byte)(i * 13)).ToArray());
      File.WriteAllBytes(path, writer.Build());

      var fsck = FsInteropToolbox.RunWsl($"fsck.gfs2 -n {FsInteropToolbox.WinToWsl(path)}");
      Assert.That(fsck.ExitCode, Is.EqualTo(0),
        $"fsck.gfs2 rejected nested-directory output:\nstdout:\n{fsck.StdOut}\nstderr:\n{fsck.StdErr}");
      Assert.That(fsck.StdOut, Does.Contain("complete").IgnoreCase);
      foreach (var complaint in new[] { "failed", "damage", "cannot fix", "does not match", "corrupt" })
        Assert.That(fsck.StdOut, Does.Not.Contain(complaint).IgnoreCase,
          $"fsck.gfs2 reported '{complaint}':\n{fsck.StdOut}");
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }
}

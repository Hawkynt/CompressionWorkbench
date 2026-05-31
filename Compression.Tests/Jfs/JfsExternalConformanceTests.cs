using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Compression.Tests.Jfs;

/// <summary>
/// Validates JFS images emitted by <see cref="FileSystem.Jfs.JfsWriter"/>
/// against the reference <c>fsck.jfs</c> (jfsutils). A spec-conformant image
/// must pass <c>fsck.jfs -n</c> (read-only) with exit code 0 and the report
/// "Filesystem is clean".
/// <para>
/// The check exercises the structures fsck is strict about: the redundant
/// secondary aggregate inode map/table (their di_ixpxd and AGGREGATE_I xtree
/// must address the secondary copies that <c>s_ait2</c>/<c>s_aim2</c> point
/// at), the sorted dtree entry tables (strictly ascending UCS-2 keys), the
/// external directory B+tree leaf/internal sibling chains, the per-extent
/// di_ixpxd of fileset inodes spread across multiple inode extents, and the
/// block- and inode-allocation maps. Skips cleanly when <c>fsck.jfs</c> is
/// not installed.
/// </para>
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class JfsExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_jfsfsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── helpers (mirror OsIntegrationTests.HasCommand / RunTool) ──────────────

  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  private static bool HasCommand(string name) {
    try {
      var result = RunTool(IsWindows ? "where" : "which", name);
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {tool}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(timeoutMs)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }

  private string BuildImage(IEnumerable<(string Name, byte[] Data)> files) {
    var w = new FileSystem.Jfs.JfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var path = Path.Combine(this._tmpDir, $"jfs_{Guid.NewGuid():N}.jfs");
    using (var fs = File.Create(path))
      w.WriteTo(fs);
    return path;
  }

  // fsck.jfs -n is read-only and returns 0 on a clean filesystem. It may print
  // a non-fatal journal-replay note only under -f; plain -n stays quiet. We
  // assert on both the exit code and the clean-report line.
  private static void AssertFsckClean(string imagePath) {
    var result = RunTool("fsck.jfs", $"-n \"{imagePath}\"");
    var report = result.StdOut + result.StdErr;
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.jfs -n must exit 0 on a clean image. Output:\n{report}");
    Assert.That(report, Does.Contain("Filesystem is clean"),
      $"fsck.jfs -n must report the filesystem clean. Output:\n{report}");
    Assert.That(report, Does.Not.Contain("CANNOT CONTINUE"),
      $"fsck.jfs -n must not abort. Output:\n{report}");
  }

  // ── conformance ───────────────────────────────────────────────────────────

  [Test, Category("OsIntegration")]
  public void SingleSmallFile_PassesFsckJfs() {
    if (!HasCommand("fsck.jfs")) Assert.Ignore("fsck.jfs (jfsutils) not installed");
    var image = this.BuildImage([("readme.txt", "hello jfs\n"u8.ToArray())]);
    AssertFsckClean(image);
  }

  [Test, Category("OsIntegration")]
  public void NestedDirectoryTree_PassesFsckJfs() {
    if (!HasCommand("fsck.jfs")) Assert.Ignore("fsck.jfs (jfsutils) not installed");
    var image = this.BuildImage([
      ("readme.txt", "root\n"u8.ToArray()),
      ("sub/inner.txt", "nested\n"u8.ToArray()),
      ("sub/deep/leaf.bin", new byte[5000]),
    ]);
    AssertFsckClean(image);
  }

  // A directory whose entries span multiple inode extents (>32 inodes) and
  // multiple external dtree leaf pages (sibling chain), the structures fsck is
  // most strict about.
  [Test, Category("OsIntegration")]
  public void LargeDirectory_ExternalDtree_PassesFsckJfs() {
    if (!HasCommand("fsck.jfs")) Assert.Ignore("fsck.jfs (jfsutils) not installed");
    var files = new List<(string, byte[])>();
    for (var i = 0; i < 1000; i++)
      files.Add(($"many/f{i:D4}.dat", Encoding.UTF8.GetBytes($"item-{i}")));
    var image = this.BuildImage(files);
    AssertFsckClean(image);
  }

  // The full representative image: root files, a nested tree, a ~1000-entry
  // directory (external dtree pages), plus small and larger files.
  [Test, Category("OsIntegration")]
  public void RepresentativeImage_PassesFsckJfs() {
    if (!HasCommand("fsck.jfs")) Assert.Ignore("fsck.jfs (jfsutils) not installed");

    var files = new List<(string, byte[])> {
      ("readme.txt", "hello jfs root file\n"u8.ToArray()),
      ("big.bin", BuildPattern(20000)),
      ("sub/inner.txt", "nested content\n"u8.ToArray()),
      ("sub/deep/leaf.bin", new byte[5000]),
    };
    for (var i = 0; i < 1000; i++)
      files.Add(($"many/f{i:D4}.dat", Encoding.UTF8.GetBytes($"item-{i}")));

    var image = this.BuildImage(files);
    AssertFsckClean(image);
  }

  private static byte[] BuildPattern(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; i++) data[i] = (byte)(i * 7 + 3);
    return data;
  }
}

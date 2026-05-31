using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// Cross-validates the F2FS writer against the reference consistency checker
/// <c>fsck.f2fs</c> (f2fs-tools). The images we emit must be accepted by the
/// real tool as structurally consistent: superblock, both checkpoint packs,
/// NAT/SIT, the segment-summary area and every reachable inode/dentry must
/// agree with each other. The tests skip cleanly where <c>fsck.f2fs</c> is
/// not installed (e.g. on Windows or a stripped CI image).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class F2fsExternalConformanceTests {

  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_f2fsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("RoundTrip")]
  public void RepresentativeImage_PassesFsckF2fs() {
    if (!HasCommand("fsck.f2fs"))
      Assert.Ignore("fsck.f2fs (f2fs-tools) not installed");

    var w = new F2fsWriter();

    // Root files, small and larger (multi-block).
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("small.txt", Encoding.UTF8.GetBytes("hello world"));
    var big = new byte[20000];
    new Random(1).NextBytes(big);
    w.AddFile("data/big.bin", big);

    // A nested tree (inline-dentry directories).
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());

    // A directory with ~1000 files: a regular, multi-block dentry directory whose
    // children spill across many node/data segments.
    for (var i = 0; i < 1000; ++i)
      w.AddFile($"manyfiles/file{i:D4}", Encoding.UTF8.GetBytes($"content-{i:D4}"));

    var imgPath = Path.Combine(this._tmpDir, "representative.f2fs");
    File.WriteAllBytes(imgPath, w.Build(64));

    var result = RunTool("fsck.f2fs", $"\"{imgPath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.f2fs reported the image inconsistent.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test, Category("RoundTrip")]
  public void LargeRegularDentryDirectory_PassesFsckF2fs() {
    if (!HasCommand("fsck.f2fs"))
      Assert.Ignore("fsck.f2fs (f2fs-tools) not installed");

    // 300 children push the directory past the inline-dentry capacity into real
    // 4 KiB dentry data blocks, which must be summarised in the segment-summary area.
    var w = new F2fsWriter();
    for (var i = 0; i < 300; ++i)
      w.AddFile($"dir/file{i:D4}", Encoding.UTF8.GetBytes($"content-{i:D4}"));

    var imgPath = Path.Combine(this._tmpDir, "largedir.f2fs");
    File.WriteAllBytes(imgPath, w.Build());

    var result = RunTool("fsck.f2fs", $"\"{imgPath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.f2fs reported the large-directory image inconsistent.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test, Category("RoundTrip")]
  public void SmallInlineDirectoryImage_PassesFsckF2fs() {
    if (!HasCommand("fsck.f2fs"))
      Assert.Ignore("fsck.f2fs (f2fs-tools) not installed");

    var w = new F2fsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());
    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());

    var imgPath = Path.Combine(this._tmpDir, "small.f2fs");
    File.WriteAllBytes(imgPath, w.Build());

    var result = RunTool("fsck.f2fs", $"\"{imgPath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"fsck.f2fs reported the small image inconsistent.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // ── tool execution (mirrors OsIntegrationTests) ───────────────────

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool HasCommand(string name) {
    try {
      var shell = IsWindows ? "cmd.exe" : "/bin/sh";
      var args = IsWindows ? $"/c where {name}" : $"-c \"which {name}\"";
      var psi = new ProcessStartInfo {
        FileName = shell, Arguments = args,
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
      };
      using var proc = Process.Start(psi);
      if (proc == null) return false;
      var outp = proc.StandardOutput.ReadToEnd();
      proc.StandardError.ReadToEnd();
      proc.WaitForExit(10_000);
      return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(outp);
    } catch {
      return false;
    }
  }

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
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
}

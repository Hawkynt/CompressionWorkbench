#pragma warning disable CS1591

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Compression.Tests.Xfs;

/// <summary>
/// Cross-validates the <see cref="FileSystem.Xfs.XfsWriter"/> output against the
/// real <c>xfs_repair</c> checker from xfsprogs. The writer must emit an image
/// that <c>xfs_repair -n</c> (no-modify check) accepts without reporting any
/// corruption: clean superblock/AGF/AGI/AGFL, valid free-space and inode
/// btrees, well-formed v3 dinodes, and spec-correct dir2/dir3 directory
/// structures (short-form, single-block "XDB3", and leaf-form "XDD3" data
/// blocks plus a "XFS_DIR3_LEAF1" hash index).
///
/// <para>The test is skipped cleanly when <c>xfs_repair</c> is not installed,
/// mirroring the <c>HasCommand</c>/<c>RunTool</c> pattern used by
/// <c>OsIntegrationTests</c>.</para>
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class XfsExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_xfs_repair_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void RepresentativeImage_PassesXfsRepairNoModifyCheck() {
    if (!IsLinux) Assert.Ignore("xfs_repair is a Linux-only tool");
    if (!HasCommand("xfs_repair")) Assert.Ignore("xfs_repair (xfsprogs) not installed");

    // ── Build a representative image ──
    // root files (small + larger), a nested directory tree, and a directory
    // with ~1000 files (forces leaf-form dir2 with many data blocks).
    var w = new FileSystem.Xfs.XfsWriter();
    w.AddFile("readme.txt", "root file"u8.ToArray());

    var large = new byte[200_000];
    new Random(7).NextBytes(large);
    w.AddFile("data/large.bin", large);

    w.AddFile("docs/guide.txt", "in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep file"u8.ToArray());
    w.AddFile("docs/api/v2/notes.txt", "deeper"u8.ToArray());

    const int bigDirCount = 1000;
    for (var i = 0; i < bigDirCount; i++)
      w.AddFile($"bigdir/file{i:D4}.dat", System.Text.Encoding.ASCII.GetBytes($"payload-{i:D4}"));

    var imagePath = Path.Combine(_tmpDir, "representative.xfs");
    using (var fs = File.Create(imagePath))
      w.WriteTo(fs);

    // ── Run xfs_repair -n (no-modify check) ──
    var result = RunTool("xfs_repair", $"-n \"{imagePath}\"");

    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"xfs_repair -n must report a clean image (exit 0).\n" +
      $"stdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // ── Process / tool helpers (mirrors OsIntegrationTests) ──────────────

  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private static bool HasCommand(string name) {
    try {
      var result = RunShell($"which {name} 2>/dev/null");
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static ToolResult RunShell(string command) {
    var psi = new ProcessStartInfo {
      FileName = "/bin/sh",
      Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var proc = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start shell for: {command}");
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    if (!proc.WaitForExit(30_000)) {
      try { proc.Kill(); } catch { /* best effort */ }
    }
    return new ToolResult(stdout, stderr, proc.ExitCode);
  }

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
}

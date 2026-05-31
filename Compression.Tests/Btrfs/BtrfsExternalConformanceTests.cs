#pragma warning disable CS1591
using System.Diagnostics;
using System.Runtime.InteropServices;
using FileSystem.Btrfs;

namespace Compression.Tests.Btrfs;

/// <summary>
/// Spec-conformance of <see cref="BtrfsWriter"/> against the real
/// <c>btrfs check</c> tool from btrfs-progs. A representative image — root
/// files, a deeply nested directory tree, a directory holding ~1000 entries
/// (forcing a multi-leaf FS tree with an internal index node), and a file
/// large enough to require a regular (non-inline) data extent — is written to
/// a temp file and validated. <c>btrfs check</c> walks the superblock, chunk
/// tree, dev tree, extent tree, root tree, FS tree, and the per-block CRC-32C
/// checksums; any structural inconsistency yields a non-zero exit and error
/// lines on stdout/stderr. The test skips cleanly when btrfs-progs is absent
/// (e.g. Windows/CI without the tool).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class BtrfsExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_btrfs_chk_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("OsIntegration")]
  public void RepresentativeImage_PassesBtrfsCheckWithNoErrors() {
    if (!HasCommand("btrfs"))
      Assert.Ignore("btrfs-progs (btrfs) not installed");

    var imagePath = Path.Combine(this._tmpDir, "conformance.btrfs");
    WriteRepresentativeImage(imagePath);

    var result = RunTool("btrfs", $"check \"{imagePath}\"");

    // btrfs check returns 0 only when it found no errors. Surface the full
    // tool output on failure so a regression names the exact invariant.
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"btrfs check reported errors.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
    Assert.That(result.StdErr + result.StdOut, Does.Not.Contain("ERROR"),
      $"btrfs check emitted an ERROR line.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  [Test, Category("OsIntegration")]
  public void InlineOnlyImage_PassesBtrfsCheckWithNoErrors() {
    if (!HasCommand("btrfs"))
      Assert.Ignore("btrfs-progs (btrfs) not installed");

    var imagePath = Path.Combine(this._tmpDir, "inline.btrfs");
    var w = new BtrfsWriter();
    w.AddFile("readme.txt", "small inline payload"u8.ToArray());
    w.AddFile("docs/guide.md", new byte[2048]);   // still below one sector
    using (var fs = File.Create(imagePath)) w.WriteTo(fs);

    var result = RunTool("btrfs", $"check \"{imagePath}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"btrfs check reported errors on the inline-only image.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  // Builds the representative corpus the audit procedure calls for.
  private static void WriteRepresentativeImage(string path) {
    var w = new BtrfsWriter();

    // Root-level files.
    w.AddFile("readme.txt", "hello from the root directory"u8.ToArray());
    w.AddFile("LICENSE", new byte[321]);

    // Deeply nested tree.
    w.AddFile("a/b/c/deep.bin", new byte[1234]);
    w.AddFile("notes/today.md", "nested note content"u8.ToArray());

    // A regular (non-inline) data extent: a file at/above one sector.
    var large = new byte[9000];
    for (var i = 0; i < large.Length; i++) large[i] = (byte)(i * 13);
    w.AddFile("data/large.bin", large);

    // A directory with ~1000 entries — forces the FS tree across many leaves
    // beneath an internal index node.
    for (var i = 0; i < 1000; i++)
      w.AddFile($"many/file{i:D4}", System.Text.Encoding.ASCII.GetBytes($"payload-{i}"));

    using var fs = File.Create(path);
    w.WriteTo(fs);
  }

  // ── Process helpers (mirrors OsIntegrationTests) ───────────────────────

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

  private static bool HasCommand(string name) {
    try {
      var shell = IsWindows ? "cmd.exe" : "/bin/sh";
      var args = IsWindows ? $"/c where {name}" : $"-c \"which {name} 2>/dev/null\"";
      var psi = new ProcessStartInfo {
        FileName = shell, Arguments = args,
        RedirectStandardOutput = true, RedirectStandardError = true,
        UseShellExecute = false, CreateNoWindow = true,
      };
      using var proc = Process.Start(psi);
      if (proc == null) return false;
      var stdout = proc.StandardOutput.ReadToEnd();
      proc.WaitForExit(10_000);
      return proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
    } catch {
      return false;
    }
  }

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 120_000) {
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

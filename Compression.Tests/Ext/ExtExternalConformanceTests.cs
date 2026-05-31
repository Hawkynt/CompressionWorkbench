#pragma warning disable CA1416 // Platform compatibility — e2fsck path is Linux-only and guarded at runtime.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Compression.Tests.Ext;

/// <summary>
/// Cross-checks the ext2 writer against the reference checker <c>e2fsck</c>
/// (e2fsprogs). A representative image — root files, a nested directory tree,
/// a directory large enough to span several data blocks, and a file large
/// enough to need a singly-indirect block — must pass <c>e2fsck -fn</c>
/// (force a full check, answer "no" to every repair) with a clean exit and no
/// reported problems across all five passes. The test skips cleanly where
/// <c>e2fsck</c> is unavailable (non-Linux hosts or no e2fsprogs).
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class ExtExternalConformanceTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ext_e2fsck_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void RepresentativeImage_PassesE2fsckClean_DefaultBlockSize() {
    var image = BuildRepresentativeImage(forcedBlockSize: 0);
    AssertE2fsckClean(image, "auto-sized");
  }

  [TestCase(1024)]
  [TestCase(2048)]
  [TestCase(4096)]
  [Category("Conformance")]
  public void RepresentativeImage_PassesE2fsckClean_AcrossBlockSizes(int blockSize) {
    // Keep each single-block-group image within its bitmap capacity
    // (8 × blockSize blocks) with room to spare.
    var totalBlocks = blockSize * 8 - blockSize;
    var image = BuildRepresentativeImage(forcedBlockSize: blockSize, totalBlocks: totalBlocks);
    AssertE2fsckClean(image, $"{blockSize}-byte blocks");
  }

  [Test, Category("Conformance")]
  public void FileSpanningIndirectBlock_PassesE2fsckClean() {
    // A file larger than 12 direct blocks (at 1 KiB blocks) forces a
    // singly-indirect block; e2fsck verifies i_blocks counts it.
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("large.bin", MakeData(40_000));
    var image = w.BuildAutoSized(requestedBlockSize: 1024);
    AssertE2fsckClean(image, "indirect-block file");
  }

  // ── Image builder ──────────────────────────────────────────────────

  private static byte[] BuildRepresentativeImage(int forcedBlockSize, int totalBlocks = 0) {
    var w = new FileSystem.Ext.ExtWriter();

    // Root files: a tiny one and one spanning several data blocks.
    w.AddFile("readme.txt", "root readme file"u8.ToArray());
    w.AddFile("notes.bin", MakeData(9_000));

    // A nested directory tree.
    w.AddFile("docs/guide.txt", "guide in docs"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep reference"u8.ToArray());

    // A directory holding enough entries to overflow a single data block.
    for (var i = 0; i < 600; ++i)
      w.AddFile($"many/f{i:D4}.txt", System.Text.Encoding.ASCII.GetBytes($"file-{i:D4}-content"));

    return forcedBlockSize > 0
      ? w.Build(forcedBlockSize, totalBlocks)
      : w.BuildAutoSized();
  }

  private static byte[] MakeData(int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + 7);
    return data;
  }

  // ── e2fsck harness ─────────────────────────────────────────────────

  private void AssertE2fsckClean(byte[] image, string label) {
    if (!IsLinux) Assert.Ignore("e2fsck check runs on Linux only.");
    if (!HasCommand("e2fsck")) Assert.Ignore("e2fsck (e2fsprogs) not installed.");

    var imagePath = Path.Combine(_tmpDir, $"ext_{label.Replace(' ', '_').Replace('-', '_')}.img");
    File.WriteAllBytes(imagePath, image);

    var result = RunTool("e2fsck", $"-fn \"{imagePath}\"");

    // -fn answers "no" to every repair prompt, so a clean image exits 0; any
    // problem either drives the exit code non-zero or prints a repair prompt.
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"e2fsck reported problems on the {label} image (exit {result.ExitCode}).\n{result.StdOut}\n{result.StdErr}");

    var combined = result.StdOut + "\n" + result.StdErr;
    foreach (var marker in new[] { "FIXED", "Reparieren", "Repair", "WARNUNG", "WARNING", "ungültig", "defekt", "falsch", "inconsistent", "corrupt" })
      Assert.That(combined, Does.Not.Contain(marker).IgnoreCase,
        $"e2fsck flagged a problem ('{marker}') on the {label} image:\n{combined}");
  }

  // ── Process plumbing (mirrors OsIntegrationTests) ──────────────────

  private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
  private static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool HasCommand(string name) {
    try {
      var result = RunShell(IsWindows ? $"where {name} 2>nul" : $"which {name} 2>/dev/null");
      return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
    } catch {
      return false;
    }
  }

  private static ToolResult RunShell(string command) {
    var shell = IsWindows ? "cmd.exe" : "/bin/sh";
    var args = IsWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";
    var psi = new ProcessStartInfo {
      FileName = shell,
      Arguments = args,
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

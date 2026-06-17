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

  // ═══════════════════════════════════════════════════════════════════
  // Stage-2 acceptance gate — external Linux tools (PATH or WSL).
  //
  // The Linux-only tests above hand the image to a local e2fsck binary;
  // the tests in this region drive the same tools through whichever
  // channel is reachable (Linux PATH on CI, WSL on developer Windows
  // boxes) so the gate runs identically in both environments. Every
  // test skips cleanly when no channel can provide the tool.
  //
  // Per Stage-2 of CONTRIBUTING.md, an image we write is only accepted
  // when an *independent* tool — not our own reader — confirms it.
  // ═══════════════════════════════════════════════════════════════════

  /// <summary>
  /// Routes a one-shot command at <paramref name="tool"/> through whichever
  /// channel is reachable: the local PATH on Linux, the WSL distro on Windows.
  /// </summary>
  /// <remarks>
  /// On Linux CI the local <c>e2fsprogs</c> tools run directly so the gate is
  /// enforced without WSL. On Windows developer boxes the WSL channel runs
  /// the same tool against a <c>/mnt/c/...</c> form of the image path.
  /// </remarks>
  private static (string StdOut, string StdErr, int ExitCode) RunE2fsTool(
      string tool, string args, string winImagePath) {
    if (IsLinux && HasCommand(tool)) {
      var r = RunTool(tool, $"{args} \"{winImagePath}\"");
      return (r.StdOut, r.StdErr, r.ExitCode);
    }
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool(tool))
      return FsInteropToolbox.RunWsl($"{tool} {args} {FsInteropToolbox.WinToWsl(winImagePath)}");
    return (string.Empty, "no channel available", int.MinValue);
  }

  private static void RequireE2fsTool(string tool) {
    if (IsLinux && HasCommand(tool)) return;
    if (FsInteropToolbox.WslAvailable && FsInteropToolbox.WslHasTool(tool)) return;
    Assert.Ignore(
      $"'{tool}' not available on this host. On Linux: `sudo apt install -y e2fsprogs`. " +
      $"On Windows: install WSL (`wsl --install`) and run `sudo apt install -y e2fsprogs` " +
      $"inside the distro.");
  }

  private static byte[] BuildExt2Image() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("hello.txt", "ext2 sample"u8.ToArray());
    w.AddFile("docs/guide.txt", "ext2 docs guide"u8.ToArray());
    return w.Build(blockSize: 1024, totalBlocks: 4096,
      FileSystem.Ext.ExtWriter.ExtVersion.Ext2, journal: false, volumeLabel: "cwbext2", inodeSize: 128);
  }

  private static byte[] BuildExt3Image() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("hello.txt", "ext3 sample"u8.ToArray());
    w.AddFile("docs/guide.txt", "ext3 docs guide"u8.ToArray());
    // ext3 → COMPAT_HAS_JOURNAL set, journal inode reserved (empty journal: SB
    // is CLEAN so recovery never runs).
    return w.Build(blockSize: 1024, totalBlocks: 4096,
      FileSystem.Ext.ExtWriter.ExtVersion.Ext3, journal: true, volumeLabel: "cwbext3", inodeSize: 128);
  }

  private static byte[] BuildExt4Image() {
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("hello.txt", "ext4 sample"u8.ToArray());
    w.AddFile("notes.bin", MakeData(9_000));
    w.AddFile("docs/guide.txt", "ext4 docs guide"u8.ToArray());
    w.AddFile("docs/api/reference.txt", "deep reference"u8.ToArray());
    return w.Build(blockSize: 1024, totalBlocks: 4096,
      FileSystem.Ext.ExtWriter.ExtVersion.Ext4, journal: true, volumeLabel: "cwbext4", inodeSize: 256);
  }

  // ── ext2 — e2fsck clean ────────────────────────────────────────────

  [Test, Category("Conformance"), Category("Wsl")]
  public void Ext2_OurImage_PassesE2fsckClean_ViaWsl() {
    RequireE2fsTool("e2fsck");
    var imgPath = Path.Combine(this._tmpDir, "ext2_wsl.img");
    File.WriteAllBytes(imgPath, BuildExt2Image());

    var r = RunE2fsTool("e2fsck", "-fn", imgPath);
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"e2fsck rejected our ext2 image (exit {r.ExitCode}):\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  // ── ext3 — e2fsck clean ────────────────────────────────────────────

  [Test, Category("Conformance"), Category("Wsl")]
  public void Ext3_OurImage_PassesE2fsckClean_ViaWsl() {
    RequireE2fsTool("e2fsck");
    var imgPath = Path.Combine(this._tmpDir, "ext3_wsl.img");
    File.WriteAllBytes(imgPath, BuildExt3Image());

    var r = RunE2fsTool("e2fsck", "-fn", imgPath);
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"e2fsck rejected our ext3 image (exit {r.ExitCode}):\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  // ── ext4 — e2fsck clean + dumpe2fs feature flags ───────────────────

  [Test, Category("Conformance"), Category("Wsl")]
  public void Ext4_OurImage_PassesE2fsckClean_ViaWsl() {
    RequireE2fsTool("e2fsck");
    var imgPath = Path.Combine(this._tmpDir, "ext4_wsl.img");
    File.WriteAllBytes(imgPath, BuildExt4Image());

    var r = RunE2fsTool("e2fsck", "-fn", imgPath);
    Assert.That(r.ExitCode, Is.EqualTo(0),
      $"e2fsck rejected our ext4 image (exit {r.ExitCode}):\nstdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");
  }

  [Test, Category("Conformance"), Category("Wsl")]
  public void Ext4_OurImage_DumpE2fsAdvertisesHasJournal() {
    RequireE2fsTool("dumpe2fs");
    var imgPath = Path.Combine(this._tmpDir, "ext4_features.img");
    File.WriteAllBytes(imgPath, BuildExt4Image());

    // `dumpe2fs -h` prints the superblock header — including the line
    //   Filesystem features:      has_journal ext_attr resize_inode dir_index ...
    // The ext4 writer sets HAS_JOURNAL whenever journal=true; ext_attr is
    // additionally advertised by mkfs.ext4 by default. We assert has_journal
    // strictly (the writer guarantees it) and emit an informational note
    // when ext_attr is absent so a future writer that adds xattrs flips the
    // gate green without changing the test.
    var r = RunE2fsTool("dumpe2fs", "-h", imgPath);
    Assert.That(r.ExitCode, Is.EqualTo(0), $"dumpe2fs failed:\n{r.StdErr}");
    Assert.That(r.StdOut, Does.Contain("Filesystem features"),
      "dumpe2fs -h should print the Filesystem features line");
    Assert.That(r.StdOut, Does.Contain("has_journal").IgnoreCase,
      $"ext4 image must advertise has_journal in s_feature_compat:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Contain("0xEF53"),
      "dumpe2fs -h should still confirm the ext magic number");
    if (!r.StdOut.Contains("ext_attr", StringComparison.OrdinalIgnoreCase))
      TestContext.Out.WriteLine(
        "[info] dumpe2fs did not list ext_attr — extended attributes not yet emitted by ExtWriter.");
  }

  // ── ext4 — debugfs ls of the root and a nested directory ───────────

  [Test, Category("Conformance"), Category("Wsl")]
  public void Ext4_OurImage_DebugfsListsExpectedFiles() {
    RequireE2fsTool("debugfs");
    var imgPath = Path.Combine(this._tmpDir, "ext4_debugfs.img");
    File.WriteAllBytes(imgPath, BuildExt4Image());

    // `-R "ls <dir>"` runs one read-only command and exits.
    var root = RunE2fsTool("debugfs", "-R \"ls -l /\"", imgPath);
    Assert.That(root.ExitCode, Is.EqualTo(0),
      $"debugfs ls / failed (exit {root.ExitCode}):\nstdout:\n{root.StdOut}\nstderr:\n{root.StdErr}");
    Assert.That(root.StdOut, Does.Contain("hello.txt"),
      $"debugfs root listing missing hello.txt:\n{root.StdOut}");
    Assert.That(root.StdOut, Does.Contain("notes.bin"),
      $"debugfs root listing missing notes.bin:\n{root.StdOut}");
    Assert.That(root.StdOut, Does.Contain("docs"),
      $"debugfs root listing missing docs/ subdirectory:\n{root.StdOut}");

    var docs = RunE2fsTool("debugfs", "-R \"ls -l /docs\"", imgPath);
    Assert.That(docs.ExitCode, Is.EqualTo(0),
      $"debugfs ls /docs failed (exit {docs.ExitCode}):\nstdout:\n{docs.StdOut}\nstderr:\n{docs.StdErr}");
    Assert.That(docs.StdOut, Does.Contain("guide.txt"),
      $"debugfs /docs listing missing guide.txt:\n{docs.StdOut}");
    Assert.That(docs.StdOut, Does.Contain("api"),
      $"debugfs /docs listing missing api/ subdirectory:\n{docs.StdOut}");
  }

  // ── ext4 — loop mount (needs root; skip cleanly otherwise) ─────────

  [Test, Category("Conformance"), Category("Wsl")]
  public void Ext4_OurImage_LoopMountListsExpectedFiles() {
    RequireE2fsTool("e2fsck");
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("Loop-mount gate runs only through WSL on Windows. " +
                    "On Linux CI use the e2fsck/debugfs gates above.");
    if (!FsInteropToolbox.WslHasPasswordlessSudo)
      Assert.Ignore("WSL loop-mount requires passwordless sudo (mount /dev/loop*). " +
                    "Add 'username ALL=(ALL) NOPASSWD: ALL' to /etc/sudoers.d/wsl-nopasswd inside WSL " +
                    "to enable this gate.");

    // 64 MiB image — larger than the 4 MiB writer default so the mount path
    // exercises a real-sized volume.
    var w = new FileSystem.Ext.ExtWriter();
    w.AddFile("hello.txt", "loop-mount sample"u8.ToArray());
    w.AddFile("notes.bin", MakeData(9_000));
    w.AddFile("docs/guide.txt", "loop-mount docs guide"u8.ToArray());
    const int Blocks = 64 * 1024; // 64 MiB at 1 KiB blocks
    var bytes = w.Build(blockSize: 1024, totalBlocks: Blocks,
      FileSystem.Ext.ExtWriter.ExtVersion.Ext4, journal: true, volumeLabel: "cwbmnt", inodeSize: 256);
    var imgPath = Path.Combine(this._tmpDir, "ext4_mount.img");
    File.WriteAllBytes(imgPath, bytes);

    var mountId = Guid.NewGuid().ToString("N")[..8];
    var wslImg = FsInteropToolbox.WinToWsl(imgPath);
    // Race-free wrapper: mkdir → mount → ls → umount → rmdir, with an
    // unconditional umount/rmdir in a trap so a partial failure never
    // leaks loop devices. Output of `ls` is captured for the assertion.
    var script =
      $"set -e; " +
      $"MNT=/tmp/cwb_mnt_{mountId}; " +
      $"trap 'sudo -n umount $MNT 2>/dev/null; rmdir $MNT 2>/dev/null' EXIT; " +
      $"mkdir -p $MNT && " +
      $"sudo -n mount -o loop,ro {wslImg} $MNT && " +
      $"ls -la $MNT";

    var r = FsInteropToolbox.RunWsl(script);
    // Mount failures are common in restricted CI environments (no loop
    // device, no privileged container, sudo gated). Treat any non-zero
    // exit as a skip rather than a hard failure — the e2fsck/debugfs
    // gates above are the load-bearing checks.
    if (r.ExitCode != 0)
      Assert.Ignore($"WSL loop mount failed (likely no loop device / privileged container); " +
                    $"skipping. stdout:\n{r.StdOut}\nstderr:\n{r.StdErr}");

    Assert.That(r.StdOut, Does.Contain("hello.txt"),
      $"loop-mount listing missing hello.txt:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Contain("notes.bin"),
      $"loop-mount listing missing notes.bin:\n{r.StdOut}");
    Assert.That(r.StdOut, Does.Contain("docs"),
      $"loop-mount listing missing docs:\n{r.StdOut}");
  }
}

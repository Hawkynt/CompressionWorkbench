#pragma warning disable CA1416 // Platform compatibility — mke2fs/mount are Linux-only and guarded at runtime.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// Oracle proof of ext symbolic-link support. An ext4 image is built by
/// <c>mke2fs</c>, mounted (loopback, via sudo), populated with a real 4096-byte
/// <c>target.txt</c> plus a FAST symlink (short inline target) and a SLOW symlink
/// (target path &gt; 60 bytes, stored in a data block), then unmounted. This repo's
/// <see cref="ExtReader"/> / <see cref="ExtFormatDescriptor"/> must then report both
/// links as symlinks with the correct target, and — the headline feature —
/// <see cref="ArchiveEntryInfo.TargetSize"/> == 4096 for the link that points at
/// <c>target.txt</c>. Skips cleanly where the tools or privileged mount are
/// unavailable.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class ExtSymlinkExternalTests {

  // Longer than the 60-byte inline-symlink threshold, so ext stores it as a
  // "slow" symlink in a data block rather than inline in i_block[].
  private const string SlowTarget =
    "this_is_a_very_long_symlink_target_path_exceeding_sixty_bytes_abcdefghij.txt";

  private const string SudoPassword = "1234";

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ext_symlink_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void FastAndSlowSymlinks_DecodeWithCorrectTargetAndResolvedSize() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("ext symlink oracle runs on Linux only.");
    foreach (var tool in new[] { "mke2fs", "mount", "umount", "ln", "sudo" })
      if (!HasCommand(tool))
        Assert.Ignore($"'{tool}' not installed; cannot run the ext symlink oracle.");

    var imgPath = Path.Combine(_tmpDir, "ext_symlink.img");
    var mnt = Path.Combine(_tmpDir, "mnt");
    Directory.CreateDirectory(mnt);

    var mk = RunTool("mke2fs", $"-F -q -t ext4 -b 4096 \"{imgPath}\" 16M");
    if (mk.ExitCode != 0)
      Assert.Ignore($"mke2fs failed to build the oracle image:\n{mk.StdErr}");

    // Privileged mount → populate → unmount as one script piped a sudo password.
    var script =
      $"set -e; " +
      $"mount -o loop '{imgPath}' '{mnt}'; " +
      $"head -c 4096 /dev/zero > '{mnt}/target.txt'; " +
      $"ln -s target.txt '{mnt}/fast'; " +
      $"ln -s {SlowTarget} '{mnt}/slow'; " +
      $"sync; " +
      $"umount '{mnt}'";
    var run = RunSudoScript(script);
    if (run.ExitCode != 0)
      Assert.Ignore($"privileged loop mount/populate failed (sudo password or loop device unavailable); " +
                    $"skipping.\nstdout:\n{run.StdOut}\nstderr:\n{run.StdErr}");

    using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read);
    var listing = new ExtFormatDescriptor().List(fs, password: null);

    var target = listing.Single(e => e.Name == "target.txt");
    Assert.That(target.OriginalSize, Is.EqualTo(4096), "target.txt should be 4096 bytes");

    var fast = listing.Single(e => e.Name == "fast");
    Assert.That(fast.IsSymlink, Is.True, "fast must be recognised as a symlink");
    Assert.That(fast.LinkTarget, Is.EqualTo("target.txt"), "fast symlink target");
    Assert.That(fast.OriginalSize, Is.EqualTo("target.txt".Length),
      "the link's own size is the target-path byte length");
    Assert.That(fast.TargetSize, Is.EqualTo(4096),
      "HEADLINE: the link's resolved target size must be the pointed-to file's size");

    var slow = listing.Single(e => e.Name == "slow");
    Assert.That(slow.IsSymlink, Is.True, "slow must be recognised as a symlink");
    Assert.That(slow.LinkTarget, Is.EqualTo(SlowTarget),
      "slow (data-block) symlink target must decode fully");
    Assert.That(slow.OriginalSize, Is.EqualTo(SlowTarget.Length),
      "slow link's own size is its target-path length");
    Assert.That(slow.TargetSize, Is.Null,
      "slow points outside the listing, so the resolved size is unknown");

    // The link's own extracted content is its target path (honest on-disk bytes).
    fs.Position = 0;
    var reader = new ExtReader(fs);
    var fastEntry = reader.Entries.Single(e => e.Name == "fast");
    Assert.That(System.Text.Encoding.UTF8.GetString(reader.Extract(fastEntry)),
      Is.EqualTo("target.txt"));
  }

  // ── process plumbing ────────────────────────────────────────────────────

  private record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static bool HasCommand(string name) {
    var psi = new ProcessStartInfo {
      FileName = "/bin/sh", Arguments = $"-c \"command -v {name}\"",
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    try {
      using var p = Process.Start(psi)!;
      var o = p.StandardOutput.ReadToEnd();
      p.WaitForExit(10_000);
      return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(o);
    } catch { return false; }
  }

  private static ToolResult RunTool(string tool, string args, int timeoutMs = 60_000) {
    var psi = new ProcessStartInfo {
      FileName = tool, Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {tool}");
    var o = p.StandardOutput.ReadToEnd();
    var e = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { /* best effort */ } }
    return new ToolResult(o, e, p.ExitCode);
  }

  // Runs a shell script under `sudo -S`, feeding the sudo password on stdin so the
  // privileged loop mount works in this environment without passwordless sudo.
  private static ToolResult RunSudoScript(string script) {
    var psi = new ProcessStartInfo {
      FileName = "sudo", ArgumentList = { "-S", "-p", "", "/bin/sh", "-c", script },
      RedirectStandardInput = true, RedirectStandardOutput = true,
      RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start sudo");
    p.StandardInput.WriteLine(SudoPassword);
    p.StandardInput.Flush();
    var o = p.StandardOutput.ReadToEnd();
    var e = p.StandardError.ReadToEnd();
    if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { /* best effort */ } }
    return new ToolResult(o, e, p.ExitCode);
  }
}

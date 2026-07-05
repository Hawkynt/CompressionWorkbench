#pragma warning disable CA1416 // mkntfs/ntfs-3g/mount are Linux-only and guarded at runtime.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Registry;
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// Oracle proof of NTFS symbolic-link support. An NTFS image is built by
/// <c>mkntfs</c>, mounted through <c>ntfs-3g</c> (loopback, via sudo), populated
/// with a real <c>target.txt</c> and a relative symlink, then unmounted.
/// <see cref="NtfsReader"/> / <see cref="NtfsFormatDescriptor"/> must detect the
/// link and expose its target. ntfs-3g stores POSIX symlinks in the Interix
/// "IntxLNK" <c>$DATA</c> format (not a reparse point); the reader handles both
/// that form and native Windows reparse-point symlinks/junctions. Skips cleanly
/// where the tools or privileged mount are unavailable.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class NtfsSymlinkExternalTests {

  private const string SudoPassword = "1234";
  private const int TargetBytes = 1234;

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ntfs_symlink_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void Ntfs3gSymlink_DetectedWithTarget() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("NTFS symlink oracle runs on Linux only.");
    foreach (var tool in new[] { "mkntfs", "ntfs-3g", "mount", "umount", "ln", "sudo" })
      if (!HasCommand(tool))
        Assert.Ignore($"'{tool}' not installed; cannot run the NTFS symlink oracle.");

    var imgPath = Path.Combine(_tmpDir, "ntfs_symlink.img");
    var mnt = Path.Combine(_tmpDir, "mnt");
    Directory.CreateDirectory(mnt);

    RunTool("truncate", $"-s 32M \"{imgPath}\"");
    var mk = RunTool("mkntfs", $"-F -Q -L cwbntfs \"{imgPath}\"");
    if (mk.ExitCode != 0)
      Assert.Ignore($"mkntfs failed to build the oracle image:\n{mk.StdErr}");

    var script =
      $"set -e; " +
      $"mount -t ntfs-3g -o loop '{imgPath}' '{mnt}'; " +
      $"head -c {TargetBytes} /dev/zero > '{mnt}/target.txt'; " +
      $"ln -s target.txt '{mnt}/link'; " +
      $"sync; " +
      $"umount '{mnt}'";
    var run = RunSudoScript(script);
    if (run.ExitCode != 0)
      Assert.Ignore($"privileged ntfs-3g mount/populate failed; skipping.\n" +
                    $"stdout:\n{run.StdOut}\nstderr:\n{run.StdErr}");

    using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read);
    var listing = new NtfsFormatDescriptor().List(fs, password: null);

    var link = listing.SingleOrDefault(e => e.Name.EndsWith("link", StringComparison.Ordinal)
                                            && e.IsSymlink);
    Assert.That(link, Is.Not.Null, "NtfsReader failed to detect the ntfs-3g symlink");
    Assert.That(link!.LinkTarget, Is.EqualTo("target.txt"), "symlink target");
    Assert.That(link.OriginalSize, Is.EqualTo("target.txt".Length),
      "the link's own size is the target-path byte length");
    Assert.That(link.TargetSize, Is.EqualTo(TargetBytes),
      "resolved target size must be the pointed-to file's size");
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

#pragma warning disable CA1416 // mksquashfs is Linux-only and guarded at runtime.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Registry;
using FileSystem.SquashFs;

namespace Compression.Tests.SquashFs;

/// <summary>
/// Oracle proof of SquashFS symlink support using <c>mksquashfs</c>. A source tree
/// with a real <c>target.txt</c> (2048 bytes) and a relative symlink pointing at it
/// is packed, then read back by <see cref="SquashFsReader"/> /
/// <see cref="SquashFsFormatDescriptor"/>: the link must decode with its target, its
/// own size must be the target-path length (no longer 0), and its resolved
/// <see cref="ArchiveEntryInfo.TargetSize"/> must be the pointed-to file's size.
/// Skips cleanly when <c>mksquashfs</c> is unavailable.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
public class SquashFsSymlinkExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_sqfs_symlink_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void Symlink_DecodesWithTargetOwnSizeAndResolvedSize() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("mksquashfs oracle runs on Linux only.");
    if (!HasCommand("mksquashfs"))
      Assert.Ignore("mksquashfs (squashfs-tools) not installed.");

    var srcDir = Path.Combine(_tmpDir, "src");
    Directory.CreateDirectory(srcDir);
    File.WriteAllBytes(Path.Combine(srcDir, "target.txt"), new byte[2048]);

    // Create the relative symlink through the OS so mksquashfs stores a genuine one.
    var ln = RunTool("ln", $"-s target.txt \"{Path.Combine(srcDir, "link")}\"");
    if (ln.ExitCode != 0) Assert.Ignore($"could not create symlink: {ln.StdErr}");

    var imgPath = Path.Combine(_tmpDir, "sq.img");
    var mk = RunTool("mksquashfs", $"\"{srcDir}\" \"{imgPath}\" -noappend -no-progress");
    if (mk.ExitCode != 0) Assert.Ignore($"mksquashfs failed:\n{mk.StdErr}");

    using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read);
    var listing = new SquashFsFormatDescriptor().List(fs, password: null);

    var target = listing.Single(e => e.Name.EndsWith("target.txt", StringComparison.Ordinal));
    Assert.That(target.OriginalSize, Is.EqualTo(2048));

    var link = listing.Single(e => e.Name.EndsWith("link", StringComparison.Ordinal));
    Assert.That(link.IsSymlink, Is.True, "link must be recognised as a symlink");
    Assert.That(link.LinkTarget, Is.EqualTo("target.txt"), "symlink target");
    Assert.That(link.OriginalSize, Is.EqualTo("target.txt".Length),
      "the SquashFS symlink's own size must be its target-path length, not 0");
    Assert.That(link.TargetSize, Is.EqualTo(2048),
      "HEADLINE: resolved target size must be the pointed-to file's size");
  }

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
}

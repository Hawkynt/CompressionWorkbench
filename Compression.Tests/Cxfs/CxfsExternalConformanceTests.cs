#pragma warning disable CS1591
using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Registry;
using FileSystem.Cxfs;

namespace Compression.Tests.Cxfs;

[TestFixture]
[Category("ExternalFsInterop")]
public class CxfsExternalConformanceTests {
  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    _tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_cxfs_repair_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_tmpDir, true); } catch { /* best effort */ }
  }

  [Test, Category("Conformance")]
  public void ConservativeV4Image_PassesXfsRepairNoModifyCheck() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("xfs_repair is exercised on Linux only.");
    if (!HasCommand("xfs_repair"))
      Assert.Ignore("xfs_repair (xfsprogs) is not installed.");

    var descriptor = new CxfsFormatDescriptor();
    var path = Path.Combine(_tmpDir, "cxfs-v4.img");
    using (var image = File.Create(path)) {
      var options = new FormatCreateOptions();
      options.FormatSpecific["VolumeLabel"] = "CXFSV4";
      descriptor.Create(image, [
        ArchiveInputInfo.InMemory("alpha.txt", "alpha"u8.ToArray()),
        ArchiveInputInfo.InMemory("payload.bin", Enumerable.Range(0, 128 * 1024).Select(i => (byte)(i * 13)).ToArray()),
      ], options);
    }

    var result = Run("xfs_repair", $"-n \"{path}\"", 60_000);
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"xfs_repair -n rejected the CXFS-compatible XFS-v4 image.\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
  }

  private static bool HasCommand(string name) {
    try {
      return Run("/bin/sh", $"-c \"command -v {name}\"", 10_000).ExitCode == 0;
    } catch {
      return false;
    }
  }

  private readonly record struct ToolResult(string StdOut, string StdErr, int ExitCode);

  private static ToolResult Run(string tool, string args, int timeoutMs) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var process = Process.Start(psi)
      ?? throw new InvalidOperationException($"Failed to start {tool}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(timeoutMs)) {
      try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
      throw new TimeoutException($"{tool} did not exit within {timeoutMs} ms.");
    }
    return new ToolResult(stdout, stderr, process.ExitCode);
  }
}

#pragma warning disable CA1416

using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Lib;
using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
[Category("OsIntegration")]
[Category("Conformance")]
public sealed class Ext2MountedFilesystemExternalConformanceTests {
  private string _temporaryDirectory = null!;

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [SetUp]
  public void Setup() {
    _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cwb_ext2_mount_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_temporaryDirectory);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_temporaryDirectory, recursive: true); } catch { }
  }

  [Test]
  public void MountedNamespaceAndDataMutationsRemainE2fsckClean() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("e2fsck interoperability runs on Linux only.");
    if (!HasCommand("e2fsck"))
      Assert.Ignore("e2fsck (e2fsprogs) is not installed.");

    var writer = new ExtWriter();
    writer.AddFile("seed.txt", "seed"u8.ToArray());
    using var image = new MemoryStream(
      writer.Build(
        blockSize: 1024,
        totalBlocks: 8000,
        ExtWriter.ExtVersion.Ext2,
        journal: false,
        volumeLabel: "cwbmount",
        inodeSize: 256),
      writable: true);

    image.Position = 0;
    var profile = FormatRegistry.ProbeFilesystem("Ext", image);
    Assert.That(profile.CanMountWritable, Is.True, string.Join("; ", profile.Limitations));

    image.Position = 0;
    using (var session = FormatRegistry.OpenFilesystem(
      "Ext", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true))) {
      var a = session.CreateDirectory(session.RootNodeId, "a");
      var b = session.CreateDirectory(a, "b");
      var file = session.CreateFile(b, "payload.bin");
      using (var handle = session.OpenFile(file, FileAccess.ReadWrite)) {
        var data = Enumerable.Range(0, 600_000).Select(i => (byte)(i * 29 + 11)).ToArray();
        handle.Write(0, data);
        handle.SetLength(610_123);
        handle.Write(610_000, "tail"u8);
        handle.SetLength(603_777);
        handle.Flush();
      }

      session.CreateHardLink(file, a, "payload.alias");
      var link = session.CreateSymbolicLink(a, "payload.link", "b/payload.bin");
      Assert.That(session.ReadSymbolicLink(link), Is.EqualTo("b/payload.bin"));

      session.Rename(b, "payload.bin", session.RootNodeId, "payload.bin", replace: false);
      session.DeleteFile(a, "payload.alias");
      session.DeleteFile(a, "payload.link");
      session.RemoveDirectory(a, "b");
      session.RemoveDirectory(session.RootNodeId, "a");
      session.DeleteFile(session.RootNodeId, "seed.txt");
      session.Flush();
    }

    var path = Path.Combine(_temporaryDirectory, "mounted-ext2.img");
    File.WriteAllBytes(path, image.ToArray());
    var result = Run("e2fsck", $"-fn \"{path}\"");
    Assert.That(result.ExitCode, Is.EqualTo(0),
      $"e2fsck rejected the mounted ext2 mutation result.\n{result.StandardOutput}\n{result.StandardError}");

    var combined = result.StandardOutput + "\n" + result.StandardError;
    foreach (var marker in new[] { "FIXED", "Repair", "WARNING", "inconsistent", "corrupt", "ungültig", "defekt" })
      Assert.That(combined, Does.Not.Contain(marker).IgnoreCase,
        $"e2fsck reported '{marker}' after mounted ext2 mutation.\n{combined}");
  }

  private static bool HasCommand(string command) {
    try {
      var result = Run("/bin/sh", $"-c \"command -v {command} >/dev/null 2>&1\"");
      return result.ExitCode == 0;
    } catch {
      return false;
    }
  }

  private static ToolResult Run(string fileName, string arguments) {
    var startInfo = new ProcessStartInfo {
      FileName = fileName,
      Arguments = arguments,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var process = Process.Start(startInfo)
      ?? throw new InvalidOperationException($"Failed to start {fileName}.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    if (!process.WaitForExit(60_000)) {
      try { process.Kill(entireProcessTree: true); } catch { }
      throw new TimeoutException($"{fileName} did not exit within 60 seconds.");
    }
    return new ToolResult(process.ExitCode, stdout, stderr);
  }

  private readonly record struct ToolResult(int ExitCode, string StandardOutput, string StandardError);
}
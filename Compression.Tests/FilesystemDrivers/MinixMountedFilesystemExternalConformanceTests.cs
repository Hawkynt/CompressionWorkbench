#pragma warning disable CA1416

using System.Diagnostics;
using System.Runtime.InteropServices;
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.FilesystemDrivers;

[TestFixture]
[Category("ExternalFsInterop")]
public sealed class MinixMountedFilesystemExternalConformanceTests {
  private string _temporaryDirectory = null!;

  [OneTimeSetUp]
  public void Init() => FormatRegistration.EnsureInitialized();

  [SetUp]
  public void Setup() {
    _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"cwb_minix_mount_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_temporaryDirectory);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(_temporaryDirectory, recursive: true); } catch { }
  }

  [TestCase(1)]
  [TestCase(2)]
  [TestCase(3)]
  public void MkfsImage_MountedMutation_RemainsFsckClean(int version) {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
      Assert.Ignore("mkfs.minix/fsck.minix interoperability runs on Linux only.");
    if (!HasCommand("mkfs.minix") || !HasCommand("fsck.minix"))
      Assert.Ignore("mkfs.minix and fsck.minix (util-linux) are required.");

    var path = Path.Combine(_temporaryDirectory, $"minix-v{version}.img");
    using (var file = File.Create(path)) file.SetLength(16L * 1024 * 1024);

    var format = Run("mkfs.minix", $"-{version}", path);
    Assert.That(format.ExitCode, Is.Zero,
      $"mkfs.minix -{version} failed.\n{format.StandardOutput}\n{format.StandardError}");

    var payload = Enumerable.Range(0, 600_000).Select(i => (byte)(i * 37 + version)).ToArray();
    using (var image = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
      var profile = FormatRegistry.ProbeFilesystem("MinixFs", image);
      Assert.That(profile.CanMountWritable, Is.True,
        $"Our native driver rejected mkfs.minix v{version}: {string.Join("; ", profile.Limitations)}");

      image.Position = 0;
      using var session = FormatRegistry.OpenFilesystem(
        "MinixFs", image, new FilesystemOpenOptions(ReadOnly: false, LeaveOpen: true));
      var dir = session.CreateDirectory(session.RootNodeId, "interop");
      var file = session.CreateFile(dir, "payload.bin");
      using (var handle = session.OpenFile(file, FileAccess.ReadWrite)) {
        handle.Write(0, payload);
        handle.SetLength(700_000);
        handle.Write(699_500, "tail"u8);
        handle.SetLength(610_123);
        handle.Flush();
      }

      session.CreateHardLink(file, dir, "payload.alias");
      var link = session.CreateSymbolicLink(dir, "payload.link", "payload.bin");
      Assert.That(session.ReadSymbolicLink(link), Is.EqualTo("payload.bin"));
      session.Rename(dir, "payload.bin", session.RootNodeId, "moved.bin", replace: false);
      session.DeleteFile(dir, "payload.alias");
      session.DeleteFile(dir, "payload.link");
      session.RemoveDirectory(session.RootNodeId, "interop");
      session.Flush();
    }

    var check = Run("fsck.minix", "-f", path);
    Assert.That(check.ExitCode, Is.Zero,
      $"fsck.minix rejected the mounted v{version} mutation result.\n{check.StandardOutput}\n{check.StandardError}");

    using var readImage = File.OpenRead(path);
    using var remounted = FormatRegistry.OpenFilesystem(
      "MinixFs", readImage, new FilesystemOpenOptions(ReadOnly: true, LeaveOpen: true));
    var moved = remounted.Lookup(remounted.RootNodeId, "moved.bin");
    Assert.That(moved, Is.Not.Null);
    using var handleRead = remounted.OpenFile(moved!.Value, FileAccess.Read);
    var bytes = new byte[checked((int)handleRead.Length)];
    Assert.That(handleRead.Read(0, bytes), Is.EqualTo(bytes.Length));
    Assert.That(bytes.AsSpan(0, payload.Length).ToArray(), Is.EqualTo(payload).AsCollection);
    Assert.That(bytes.AsSpan(payload.Length).ContainsAnyExcept((byte)0), Is.False);
  }

  private static bool HasCommand(string command) {
    try { return Run("/bin/sh", "-c", $"command -v {command} >/dev/null 2>&1").ExitCode == 0; }
    catch { return false; }
  }

  private static ToolResult Run(string fileName, params string[] arguments) {
    var startInfo = new ProcessStartInfo {
      FileName = fileName,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
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
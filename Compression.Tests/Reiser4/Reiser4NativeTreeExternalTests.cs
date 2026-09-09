using System.ComponentModel;
using System.Diagnostics;
using FileSystem.Reiser4;

namespace Compression.Tests.Reiser4;

[TestFixture]
[NUnit.Framework.Category("ExternalFsInterop")]
public sealed class Reiser4NativeTreeExternalTests {

  [Test, NUnit.Framework.Category("HappyPath")]
  public void DebugfsReiser4_SeesFileWrittenIntoNativeTree() {
    if (!OperatingSystem.IsLinux())
      Assert.Ignore("debugfs.reiser4 oracle is exercised on Linux runners.");

    const string fileName = "native-visible.bin";
    var payload = Enumerable.Range(0, 7_000).Select(static i => (byte)(i * 29)).ToArray();
    var writer = new Reiser4Writer();
    writer.AddFile(fileName, payload);

    var path = Path.Combine(Path.GetTempPath(), "cwb_reiser4_native_" + Guid.NewGuid().ToString("N") + ".img");
    try {
      File.WriteAllBytes(path, writer.Build());
      Process? process;
      try {
        var start = new ProcessStartInfo("debugfs.reiser4") {
          RedirectStandardOutput = true,
          RedirectStandardError = true,
          UseShellExecute = false,
          CreateNoWindow = true,
        };
        start.ArgumentList.Add("-k");
        start.ArgumentList.Add("/");
        start.ArgumentList.Add(path);
        process = Process.Start(start);
      } catch (Win32Exception) {
        Assert.Ignore("debugfs.reiser4 is not installed on this runner.");
        return;
      }

      Assert.That(process, Is.Not.Null);
      using (var started = process!) {
        var stdout = started.StandardOutput.ReadToEnd();
        var stderr = started.StandardError.ReadToEnd();
        started.WaitForExit();
        Assert.Multiple(() => {
          Assert.That(started.ExitCode, Is.EqualTo(0),
            $"debugfs.reiser4 failed:\nstdout:\n{stdout}\nstderr:\n{stderr}");
          Assert.That(stdout + stderr, Does.Contain(fileName),
            $"debugfs.reiser4 opened the volume but did not expose '{fileName}'.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        });
      }
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }
}

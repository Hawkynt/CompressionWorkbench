using System.Diagnostics;
using FileFormat.Regf;

namespace Compression.Tests.StructuredPseudoArchives;

[TestFixture]
[Category("ExternalInterop")]
public sealed class RegistryHiveExternalInteropTests {
  [Test]
  public void Regf_ParsesHiveSavedByWindowsRegExe() {
    if (!OperatingSystem.IsWindows())
      Assert.Ignore("Windows reg.exe is required for the REGF oracle.");

    var suffix = Guid.NewGuid().ToString("N");
    var key = $@"HKCU\Software\CompressionWorkbench\RegistryHiveOracle_{suffix}";
    var hivePath = Path.Combine(Path.GetTempPath(), $"cwb-regf-{suffix}.hiv");

    try {
      RunReg("add", key, "/v", "Greeting", "/t", "REG_SZ", "/d", "hello", "/f");
      RunReg("add", key + @"\Child", "/v", "Number", "/t", "REG_DWORD", "/d", "42", "/f");
      RunReg("save", key, hivePath, "/y");

      var descriptor = new RegfFormatDescriptor();
      using var stream = File.OpenRead(hivePath);
      var entries = descriptor.List(stream, null);

      Assert.Multiple(() => {
        Assert.That(entries.Any(e => e.Name == "Greeting" && e.Kind == "REG_SZ"), Is.True);
        Assert.That(entries.Any(e => e.IsDirectory && e.Name == "Child"), Is.True);
        Assert.That(entries.Any(e => e.Name == "Child/Number" && e.Kind == "REG_DWORD"), Is.True);
      });
    } finally {
      TryRunReg("delete", key, "/f");
      try { File.Delete(hivePath); } catch { }
    }
  }

  private static void RunReg(params string[] arguments) {
    var result = StartReg(arguments);
    Assert.That(result.ExitCode, Is.Zero, $"reg.exe {string.Join(' ', arguments)} failed:{Environment.NewLine}{result.Output}");
  }

  private static void TryRunReg(params string[] arguments) {
    try { _ = StartReg(arguments); } catch { }
  }

  private static (int ExitCode, string Output) StartReg(IEnumerable<string> arguments) {
    using var process = new Process {
      StartInfo = new ProcessStartInfo {
        FileName = "reg.exe",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
      },
    };
    foreach (var argument in arguments)
      process.StartInfo.ArgumentList.Add(argument);

    process.Start();
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout + stderr);
  }
}

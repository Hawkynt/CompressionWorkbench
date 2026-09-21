using System.Diagnostics;
using FileFormat.Regf;

namespace Compression.Tests.StructuredPseudoArchives;

/// <summary>
/// Reads a hive Windows produced a moment ago, rather than one recorded years ago.
///
/// The gating proof that both hive readers work on real input lives in
/// <c>StructuredPseudoArchiveReferenceVectorTests</c>, against checked-in hives. This adds the
/// current Windows build's own output on a host that will hand it over, which the frozen samples
/// cannot cover. It is advisory: <c>reg.exe save</c> needs <c>SeBackupPrivilege</c>, so on a host
/// without it the honest report is "not validated", not a red.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class RegistryHiveExternalInteropTests {
  [Test]
  public void Regf_ParsesHiveSavedByWindowsRegExe() {
    if (!OperatingSystem.IsWindows()) {
      Assert.Ignore("Windows reg.exe is required for the REGF oracle.");
      return;
    }

    var suffix = Guid.NewGuid().ToString("N");
    var key = $@"HKCU\Software\CompressionWorkbench\RegistryHiveOracle_{suffix}";
    var hivePath = Path.Combine(Path.GetTempPath(), $"cwb-regf-{suffix}.hiv");

    try {
      RunReg("add", key, "/v", "Greeting", "/t", "REG_SZ", "/d", "hello", "/f");
      RunReg("add", key + @"\Child", "/v", "Number", "/t", "REG_DWORD", "/d", "42", "/f");

      var save = StartReg(["save", key, hivePath, "/y"]);
      if (save.ExitCode != 0) {
        // "Dem Client fehlt ein erforderliches Recht" / "A required privilege is not held by the
        // client": the account cannot enable SeBackupPrivilege. That is a statement about the host,
        // not about the reader, and asserting on it reported a red where it meant "not validated".
        Assert.Ignore($"reg.exe save could not write a hive on this host: {save.Output.Trim()}");
        return;
      }

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
      try { File.Delete(hivePath); } catch { /* best effort */ }
    }
  }

  private static void RunReg(params string[] arguments) {
    var result = StartReg(arguments);
    Assert.That(result.ExitCode, Is.Zero, $"reg.exe {string.Join(' ', arguments)} failed:{Environment.NewLine}{result.Output}");
  }

  private static void TryRunReg(params string[] arguments) {
    try { _ = StartReg(arguments); } catch { /* best effort */ }
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

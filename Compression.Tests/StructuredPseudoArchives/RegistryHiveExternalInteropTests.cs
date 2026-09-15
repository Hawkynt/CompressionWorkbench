using System.Diagnostics;
using FileFormat.Creg;
using FileFormat.Regf;

namespace Compression.Tests.StructuredPseudoArchives;

[TestFixture]
[Category("ExternalInterop")]
public sealed class RegistryHiveExternalInteropTests {
  private const string DfWinRegRevision = "92ac103fe5072e83272463f61c9a1e65d4820997";
  private const string Windows9xUserDatUrl =
    $"https://raw.githubusercontent.com/log2timeline/dfwinreg/{DfWinRegRevision}/test_data/USER.DAT";

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

  [Test]
  public async Task Creg_ParsesApacheLicensedWindows9xUserDatFixture() {
    // log2timeline/dfwinreg is Apache-2.0 and publishes this real Windows 9x USER.DAT
    // as test data. Pin the repository revision so this behavioral oracle is immutable.
    byte[] bytes;
    try {
      using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
      bytes = await client.GetByteArrayAsync(Windows9xUserDatUrl);
    } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) {
      Assert.Ignore($"Windows 9x CREG fixture could not be downloaded: {ex.Message}");
      return;
    }

    Assert.Multiple(() => {
      Assert.That(bytes.Length, Is.EqualTo(159_776));
      Assert.That(bytes.AsSpan(0, 4).SequenceEqual("CREG"u8), Is.True);
    });

    var descriptor = new CregFormatDescriptor();
    using var stream = new MemoryStream(bytes, writable: false);
    var entries = descriptor.List(stream, null);

    const string autorun = ".DEFAULT/Software/Microsoft/Windows/CurrentVersion/Explorer/MountPoints/A/_Autorun";
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == ".DEFAULT"), Is.True);
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == "Software"), Is.True);
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == autorun), Is.True);
      Assert.That(entries.Any(e => e.Name == autorun + "/LastUpdate"), Is.True);
    });
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

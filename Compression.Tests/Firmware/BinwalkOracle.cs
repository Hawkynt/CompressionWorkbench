#pragma warning disable CS1591
using System.Diagnostics;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Firmware;

/// <summary>
/// binwalk v3 (<c>https://github.com/ReFirmLabs/binwalk</c>, MIT) as a black-box oracle: its
/// signature scan only reports most firmware headers after their own validation (a TRX only when
/// its CRC verifies, for one), so a description in its output is evidence the image is well formed.
/// Found as <c>binwalk</c> on the PATH or through <c>CWB_BINWALK</c>; v2 (the Python one) is not
/// accepted, because it does not validate.
/// </summary>
internal static class BinwalkOracle {

  private static readonly Lazy<string?> Tool = new(() => {
    foreach (var candidate in new[] { Environment.GetEnvironmentVariable("CWB_BINWALK"), "binwalk" }) {
      if (string.IsNullOrWhiteSpace(candidate)) continue;
      try {
        var (code, stdout, _) = Run(candidate, ["--version"]);
        if (code == 0 && stdout.Contains("binwalk 3", StringComparison.OrdinalIgnoreCase)) return candidate;
      } catch (Win32Exception) {
        // not there
      }
    }
    return null;
  });

  public static string Require() {
    if (Tool.Value is null) Assert.Ignore("binwalk v3 not found (cargo install binwalk, or set CWB_BINWALK).");
    return Tool.Value!;
  }

  /// <summary>The signature scan of <paramref name="data"/>, as binwalk prints it.</summary>
  public static string Scan(byte[] data) {
    var tool = Require();
    var path = Path.Combine(Path.GetTempPath(), "cwb_bw_" + Guid.NewGuid().ToString("N")[..10] + ".bin");
    File.WriteAllBytes(path, data);
    try {
      var (code, stdout, stderr) = Run(tool, [path]);
      Assert.That(code, Is.Zero, $"binwalk failed:\n{stderr}");
      return stdout;
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string tool, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in arguments) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }
}

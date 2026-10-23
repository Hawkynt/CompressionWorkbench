#pragma warning disable CS1591
using System.Diagnostics;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Firmware;

/// <summary>
/// OpenWrt firmware-utils (<c>https://github.com/openwrt/firmware-utils</c>, GPL-2.0) as black-box
/// oracles: <c>otrx check</c>, <c>oseama info</c> and friends judge images we write. The tools are
/// found in <c>CWB_FIRMWARE_UTILS</c> (a directory of built binaries) or on the PATH; tests ignore
/// themselves without them.
/// </summary>
internal static class FirmwareUtilsOracle {

  public static string Require(string tool) {
    var dir = Environment.GetEnvironmentVariable("CWB_FIRMWARE_UTILS");
    foreach (var candidate in new[] {
               dir is null ? null : Path.Combine(dir, tool + (OperatingSystem.IsWindows() ? ".exe" : "")),
               tool,
             }) {
      if (candidate is null) continue;
      if (Path.IsPathRooted(candidate) && !File.Exists(candidate)) continue;
      try {
        _ = Run(candidate, []);
        return candidate;
      } catch (Win32Exception) {
        // not there
      }
    }
    Assert.Ignore($"OpenWrt firmware-utils '{tool}' not found (build https://github.com/openwrt/firmware-utils and set CWB_FIRMWARE_UTILS).");
    return null!;
  }

  public static (int ExitCode, string Output) Run(string tool, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in arguments) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result + stderr);
  }

  /// <summary>Runs <paramref name="tool"/> with <paramref name="data"/> saved to a temporary file whose path replaces <c>{0}</c> in the arguments.</summary>
  public static (int ExitCode, string Output) RunOn(string tool, byte[] data, params string[] arguments) {
    var path = Path.Combine(Path.GetTempPath(), "cwb_fwu_" + Guid.NewGuid().ToString("N")[..10] + ".bin");
    File.WriteAllBytes(path, data);
    try {
      return Run(tool, arguments.Select(a => a.Replace("{0}", path)));
    } finally {
      try { File.Delete(path); } catch { /* best effort */ }
    }
  }
}

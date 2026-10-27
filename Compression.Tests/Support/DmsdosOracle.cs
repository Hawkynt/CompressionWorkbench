namespace Compression.Tests.Support;

/// <summary>
/// The GPL <c>dmsdos</c> tools as a black-box oracle on any host: natively on Linux (built by
/// <see cref="DmsdosCache"/>), through WSL on Windows (cloned and built once under
/// <c>~/.cache/cwb-dmsdos</c> inside the distro). Only the tools' behaviour is used — volumes go in,
/// detection text and file contents come out.
/// </summary>
internal static class DmsdosOracle {
  private static readonly Lazy<string?> Build = new(Locate);

  /// <summary>Skips the calling test when the tools cannot be had on this machine.</summary>
  public static void Require() {
    if (Build.Value is null)
      Assert.Ignore("dmsdos tools unavailable (need Linux or WSL with git + cmake + a C compiler, or CWB_DMSDOS_BUILD).");
  }

  /// <summary>What <c>cvftest -v</c> makes of the volume.</summary>
  public static string Detect(string cvfPath)
    => FsInteropToolbox.RunWsl($"'{Build.Value}/cvftest' {FsInteropToolbox.WinToWsl(cvfPath)} -v 2>&1").StdOut;

  /// <summary>
  /// Reads <paramref name="name"/> from the root of the volume with <c>dcread … raw</c>; the bytes are
  /// the whole clusters the driver decoded, so callers compare the first file-size bytes.
  /// </summary>
  public static (int Exit, byte[] Data) ReadFile(string cvfPath, string name) {
    var outFile = $"{cvfPath}.{name}.out";
    try {
      var r = FsInteropToolbox.RunWsl(
        $"'{Build.Value}/dcread' {FsInteropToolbox.WinToWsl(cvfPath)} /{name} raw > {FsInteropToolbox.WinToWsl(outFile)} 2>/dev/null");
      var raw = File.Exists(outFile) ? File.ReadAllBytes(outFile) : [];
      return (r.ExitCode, DmsdosCache.PayloadAfterDiagnostics(raw));
    } finally {
      try { File.Delete(outFile); } catch (IOException) { /* best effort */ }
    }
  }

  private static string? Locate() {
    if (!OperatingSystem.IsWindows()) return DmsdosCache.EnsureTools();
    if (!FsInteropToolbox.WslAvailable) return null;

    const string script =
      "set -e; d=$HOME/.cache/cwb-dmsdos; "
      + "if [ ! -x $d/build/dcread ] || [ ! -x $d/build/cvftest ]; then "
      + "command -v git >/dev/null && command -v cmake >/dev/null && command -v cc >/dev/null; "
      + "mkdir -p $d; [ -d $d/src/.git ] || git clone -q --depth 1 " + DmsdosCache.RepoUrl + " $d/src; "
      + "cmake -S $d/src -B $d/build -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 >/dev/null 2>&1; "
      + "cmake --build $d/build -j 2 >/dev/null 2>&1; fi; echo $d/build";
    var result = FsInteropToolbox.RunWsl(script);
    var lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return result.ExitCode == 0 && lines.Length > 0 ? lines[^1] : null;
  }
}

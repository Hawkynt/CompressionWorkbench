#pragma warning disable CS1591
using System.Diagnostics;

namespace Compression.Tests.Support;

/// <summary>
/// Clones and builds the GPL <c>dmsdos</c> driver/tools (the independent Linux
/// DoubleSpace/DriveSpace/Stacker implementation) in userspace library mode and
/// caches the build under <c>%TEMP%/cwb-dmsdos-cache</c>. Used by the genuine-CVF
/// driver-proof gates that ask a real, third-party driver to mount and read back
/// images our writers produce.
/// <para>
/// dmsdos is GPL; we don't bundle it. First run clones from GitHub and builds
/// with CMake (needs <c>git</c> + <c>cmake</c> + a C compiler); later runs reuse
/// the cached <c>build/</c>. <see cref="EnsureTools"/> returns the build
/// directory (containing <c>cvftest</c> and <c>dcread</c>) on success, or
/// <c>null</c> on any failure (no toolchain, offline, build error) — callers are
/// expected to <c>Assert.Ignore</c>, never fail loudly.
/// </para>
/// <para>
/// Set <c>CWB_DMSDOS_BUILD=&lt;dir&gt;</c> to point at a pre-built tree and skip
/// the clone/build entirely (air-gapped CI).
/// </para>
/// </summary>
internal static class DmsdosCache {
  public const string RepoUrl = "https://github.com/sandsmark/dmsdos.git";

  private static string CacheDir {
    get {
      var dir = Path.Combine(Path.GetTempPath(), "cwb-dmsdos-cache");
      Directory.CreateDirectory(dir);
      return dir;
    }
  }

  /// <summary>The two tools the driver-proof gates use.</summary>
  public static string CvfTest(string buildDir) => Path.Combine(buildDir, "cvftest");
  public static string DcRead(string buildDir) => Path.Combine(buildDir, "dcread");

  /// <summary>
  /// Returns the path to a built dmsdos <c>build/</c> directory, or <c>null</c>
  /// if the tools cannot be produced on this machine.
  /// </summary>
  public static string? EnsureTools() {
    var explicitDir = Environment.GetEnvironmentVariable("CWB_DMSDOS_BUILD");
    if (!string.IsNullOrEmpty(explicitDir) && File.Exists(Path.Combine(explicitDir, "cvftest")))
      return explicitDir;

    if (!OperatingSystem.IsLinux()) return null; // userspace build is Linux/GCC only

    var repo = Path.Combine(CacheDir, "dmsdos");
    var build = Path.Combine(repo, "build");
    if (File.Exists(Path.Combine(build, "cvftest")) && File.Exists(Path.Combine(build, "dcread")))
      return build; // cache hit

    try {
      if (!Directory.Exists(Path.Combine(repo, ".git"))) {
        if (Run("git", $"clone --depth 1 {RepoUrl} \"{repo}\"", CacheDir) != 0)
          return null;
      }

      // Old CMakeLists needs the policy-compat shim on modern CMake.
      if (Run("cmake", $"-S \"{repo}\" -B \"{build}\" -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5", repo) != 0)
        return null;
      if (Run("cmake", $"--build \"{build}\" -j", repo) != 0)
        return null;

      return File.Exists(Path.Combine(build, "cvftest")) && File.Exists(Path.Combine(build, "dcread"))
        ? build : null;
    } catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) {
      return null; // git/cmake missing or disk error
    }
  }

  private static int Run(string exe, string args, string cwd) {
    using var p = new Process {
      StartInfo = new ProcessStartInfo(exe, args) {
        WorkingDirectory = cwd,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
      },
    };
    p.Start();
    p.StandardOutput.ReadToEnd();
    p.StandardError.ReadToEnd();
    if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { /* ignore */ } return -1; }
    return p.ExitCode;
  }

  /// <summary>
  /// <c>dcread … raw</c> prints diagnostic text lines (mount info and a final
  /// <c>scan_dir: searching for NAME in N</c> line) to stdout before the raw
  /// cluster bytes, and emits the whole untruncated cluster. The real payload
  /// begins right after the last <c>scan_dir:</c> line; callers compare the
  /// first &lt;filesize&gt; bytes of the returned span.
  /// </summary>
  public static byte[] PayloadAfterDiagnostics(byte[] output) {
    var marker = "scan_dir:"u8;
    var idx = -1;
    for (var i = output.Length - marker.Length; i >= 0; i--) {
      if (output.AsSpan(i, marker.Length).SequenceEqual(marker)) { idx = i; break; }
    }
    if (idx < 0) return output;
    var nl = Array.IndexOf(output, (byte)'\n', idx);
    return nl >= 0 ? output[(nl + 1)..] : output;
  }

  /// <summary>
  /// Runs a built dmsdos tool, capturing raw stdout bytes (tool output is
  /// binary for <c>dcread … raw</c>). Returns (exitCode, stdoutBytes).
  /// </summary>
  public static (int Exit, byte[] StdOut) RunTool(string exePath, string args) {
    using var p = new Process {
      StartInfo = new ProcessStartInfo(exePath, args) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
      },
    };
    p.Start();
    using var ms = new MemoryStream();
    p.StandardOutput.BaseStream.CopyTo(ms);
    p.StandardError.ReadToEnd();
    p.WaitForExit(60_000);
    return (p.ExitCode, ms.ToArray());
  }
}

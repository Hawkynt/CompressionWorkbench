#pragma warning disable CS1591
using System.Diagnostics;

namespace Compression.Tests.Support;

/// <summary>
/// Drives a headless DOSBox-X session for filesystem-validation purposes.
/// Used by <c>ExternalFsInteropTests.DoubleSpace_OurImage_DosboxXChkdsk</c>
/// to run Microsoft's actual <c>DBLSPACE.EXE /CHKDSK</c> against our CVF
/// output inside an emulated MS-DOS 6.22 session — that is the only
/// real-tool validation path for DBLSPACE that we can legitimately wire (no
/// apt-installable Linux validator exists).
/// <para>
/// All flags passed to DOSBox-X come from the documented command-line
/// reference (run <c>dosbox-x --help</c> to see them): <c>-conf</c>,
/// <c>-fastlaunch</c>, <c>-silent</c>, <c>-nogui</c>, <c>-nomenu</c>,
/// <c>-exit</c>, <c>-time-limit</c>. We do NOT use undocumented flags.
/// </para>
/// <para>
/// Discovery is best-effort: <see cref="DosboxXAvailable"/> is true when
/// <c>dosbox-x</c> resolves on either the Windows host PATH or inside the
/// default WSL distro. Callers should <c>Assert.Ignore</c> when neither path
/// resolves rather than failing loudly — the user explicitly forbade hard
/// failures on missing dependencies.
/// </para>
/// </summary>
internal static class DosboxRunner {
  /// <summary>Sentinel returned when DOSBox-X is missing entirely.</summary>
  public const int ExitCode_DosboxMissing = -1;
  /// <summary>Sentinel returned when DOSBox-X timed out and was killed.</summary>
  public const int ExitCode_TimedOut = -2;

  /// <summary>Path to <c>dosbox-x[.exe]</c> on the Windows PATH, or null.</summary>
  public static string? HostPath { get; } = TryFromPath("dosbox-x") ?? TryFromPath("dosbox");

  /// <summary>True when DOSBox-X is reachable from this process (host or WSL).</summary>
  public static bool DosboxXAvailable => HostPath is not null || WslHasDosboxX();

  private static bool? _wslHasDosboxX;
  private static bool WslHasDosboxX() {
    if (_wslHasDosboxX is { } cached) return cached;
    bool found;
    if (!OperatingSystem.IsWindows()) {
      found = false;
    } else {
      var wsl = TryFromPath("wsl");
      if (wsl is null) {
        found = false;
      } else {
        var r = RunExact(wsl, "-e bash -c \"command -v dosbox-x\"");
        found = r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut);
      }
    }
    _wslHasDosboxX = found;
    return found;
  }

  /// <summary>
  /// Result of a DOSBox-X run. <see cref="GuestOutput"/> is the contents of
  /// the file the autoexec script redirected its output to (we cannot easily
  /// scrape the emulated VGA console; instead the convention is that scripts
  /// redirect to <c>C:\OUTPUT.TXT</c> which we extract from the boot image
  /// after the session — but for many host setups that requires an extra
  /// FAT-reader step, so we also return the raw DOSBox-X stdout/stderr).
  /// </summary>
  public sealed record RunResult(int ExitCode, string StdOut, string StdErr, string GuestOutput);

  /// <summary>
  /// Writes <paramref name="confContents"/> to a temp file and runs
  /// <c>dosbox-x -conf &lt;tmp&gt; -fastlaunch -silent -nogui -nomenu -exit
  /// -time-limit &lt;timeout&gt;</c>. Captures stdout/stderr; if the autoexec
  /// script redirected to a host-readable file via DOSBox-X's <c>config -wcd</c>
  /// or by mounting the host temp dir as a guest drive, the caller can read
  /// that file separately via <see cref="RunResult.GuestOutput"/>.
  /// </summary>
  /// <param name="confContents">Body of <c>dosbox.conf</c> — typically built via <see cref="DosboxConfBuilder"/>.</param>
  /// <param name="guestOutputPath">Optional host path the autoexec wrote to via a mounted drive; read post-run.</param>
  /// <param name="timeout">Hard kill timeout. Also passed to DOSBox-X via <c>-time-limit</c>.</param>
  public static RunResult RunWithConf(string confContents, string? guestOutputPath = null, TimeSpan? timeout = null) {
    if (!DosboxXAvailable)
      return new RunResult(ExitCode_DosboxMissing, string.Empty, "dosbox-x not found on PATH or in WSL.", string.Empty);

    var t = timeout ?? TimeSpan.FromSeconds(60);
    var confPath = Path.Combine(Path.GetTempPath(), $"cwb_dosbox_{Guid.NewGuid():N}.conf");
    File.WriteAllText(confPath, confContents);

    try {
      string fileName;
      string args;
      // Prefer Windows-native dosbox-x when present so paths in the conf can
      // remain in their native form. Fall back to WSL when only the WSL build
      // is installed — but in that case the conf file will need /mnt/c/...
      // paths, which is the caller's responsibility.
      var timeLimit = ((int)t.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
      var commonArgs = $"-conf \"{confPath}\" -fastlaunch -silent -nogui -nomenu -exit -time-limit {timeLimit}";
      if (HostPath is not null) {
        fileName = HostPath;
        args = commonArgs;
      } else {
        fileName = TryFromPath("wsl") ?? "wsl";
        // Translate the conf path to the WSL form so dosbox-x in WSL can read it.
        var wslConf = WinPathToWsl(confPath);
        args = $"-e bash -c \"dosbox-x -conf {wslConf} -fastlaunch -silent -nogui -nomenu -exit -time-limit {timeLimit}\"";
      }

      var psi = new ProcessStartInfo {
        FileName = fileName,
        Arguments = args,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var proc = Process.Start(psi)!;
      var stdoutTask = proc.StandardOutput.ReadToEndAsync();
      var stderrTask = proc.StandardError.ReadToEndAsync();
      // Add 5 s grace beyond DOSBox-X's own time-limit so a killed-but-not-
      // exited process is reaped by us, not by the OS.
      var killAfter = (int)t.Add(TimeSpan.FromSeconds(5)).TotalMilliseconds;
      var exited = proc.WaitForExit(killAfter);
      if (!exited) {
        try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        return new RunResult(
          ExitCode_TimedOut,
          stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty,
          $"DOSBox-X timed out after {t.TotalSeconds}s and was killed.",
          ReadGuestOutputSafe(guestOutputPath));
      }
      // Drain the std streams now that the process has actually exited.
      var stdout = stdoutTask.GetAwaiter().GetResult();
      var stderr = stderrTask.GetAwaiter().GetResult();
      return new RunResult(proc.ExitCode, stdout, stderr, ReadGuestOutputSafe(guestOutputPath));
    } catch (Exception ex) {
      return new RunResult(-99, string.Empty, ex.Message, string.Empty);
    } finally {
      try { File.Delete(confPath); } catch { /* best effort */ }
    }
  }

  private static string ReadGuestOutputSafe(string? path) {
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return string.Empty;
    try {
      using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete);
      using var sr = new StreamReader(fs);
      return sr.ReadToEnd();
    } catch {
      return "(guest output unavailable)";
    }
  }

  private static string WinPathToWsl(string winPath) {
    var full = Path.GetFullPath(winPath);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    var tail = full[2..].Replace('\\', '/');
    return $"'/mnt/{drive}{tail}'";
  }

  private static (string StdOut, string StdErr, int ExitCode) RunExact(string tool, string args) {
    var psi = new ProcessStartInfo {
      FileName = tool,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    try {
      using var proc = Process.Start(psi)!;
      var stdout = proc.StandardOutput.ReadToEnd();
      var stderr = proc.StandardError.ReadToEnd();
      proc.WaitForExit(15000);
      return (stdout, stderr, proc.ExitCode);
    } catch (Exception ex) {
      return (string.Empty, ex.Message, -1);
    }
  }

  private static string? TryFromPath(string tool) {
    var pathEnv = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(pathEnv)) return null;
    var exeName = OperatingSystem.IsWindows() && !tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
      ? tool + ".exe"
      : tool;
    foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
      if (string.IsNullOrWhiteSpace(dir)) continue;
      string candidate;
      try {
        candidate = Path.Combine(dir.Trim(), exeName);
      } catch {
        continue;
      }
      if (File.Exists(candidate)) return candidate;
    }
    return null;
  }
}

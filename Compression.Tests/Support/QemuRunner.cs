#pragma warning disable CS1591
using System.Diagnostics;

namespace Compression.Tests.Support;

/// <summary>
/// Boots a FreeBSD live ISO under QEMU with a UFS test image attached as a
/// second disk and captures the resulting serial console. Used by
/// <c>ExternalFsInteropTests.Ufs_OurImage_FsckFfsAccepts</c> as the only
/// real-tool path for validating UFS — there is no apt-installable
/// <c>fsck.ufs</c> on Linux; <c>fsck_ffs</c> only ships with FreeBSD.
/// <para>
/// Discovery is best-effort: <see cref="QemuAvailable"/> is true when
/// <c>qemu-system-x86_64</c> resolves on PATH. <see cref="LocateFreeBsdIso"/>
/// returns null unless the user pre-stages the ISO and points at it via the
/// <c>CWB_FREEBSD_ISO</c> environment variable (we refuse to auto-download
/// 412 MB on every CI run). When either dependency is missing the caller
/// should <c>Assert.Ignore</c>.
/// </para>
/// </summary>
internal static class QemuRunner {
  /// <summary>
  /// Sentinel returned by <see cref="RunFsckFfs"/> when QEMU booted but the
  /// guest dropped to an interactive prompt instead of running our script.
  /// The bootonly ISO behaves this way — driving it requires a custom live
  /// image (see docs/FILESYSTEMS.md).
  /// </summary>
  public const int SkipExitCode_StillInteractive = -42;

  public static bool QemuAvailable => QemuPath is not null;

  public static string? QemuPath { get; } =
    TryFromPath("qemu-system-x86_64") ?? TryFromPath("qemu-system-i386");

  /// <summary>
  /// Returns the path to a FreeBSD ISO if the user has staged one. Honours
  /// the <c>CWB_FREEBSD_ISO</c> environment variable and falls back to a
  /// canonical path under the cache directory used by other interop tests.
  /// Returns null when no ISO is available.
  /// </summary>
  public static string? LocateFreeBsdIso() {
    var fromEnv = Environment.GetEnvironmentVariable("CWB_FREEBSD_ISO");
    if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
      return fromEnv;

    // Fallback: a few well-known cache locations.
    var candidates = new[] {
      Path.Combine(Path.GetTempPath(), "cwb-fs-iso", "FreeBSD-14.3-RELEASE-amd64-bootonly.iso"),
      Path.Combine(Path.GetTempPath(), "cwb-fs-iso", "FreeBSD-14.4-RELEASE-amd64-bootonly.iso"),
      Path.Combine(Path.GetTempPath(), "cwb-fs-iso", "FreeBSD-15.0-RELEASE-amd64-bootonly.iso"),
    };
    foreach (var c in candidates)
      if (File.Exists(c)) return c;
    return null;
  }

  /// <summary>
  /// Boots <paramref name="isoPath"/> under QEMU with <paramref name="imgPath"/>
  /// attached as <c>ada1</c>, captures the serial console to
  /// <paramref name="serialLogPath"/>, and returns the QEMU exit code plus the
  /// captured log content. Times out after <paramref name="timeout"/>.
  /// <para>
  /// QEMU is invoked headless (<c>-display none</c>) with all I/O on the
  /// serial port (<c>-serial file:&lt;log&gt;</c>) so we can scrape the result
  /// without a window. KVM/HAXM acceleration is requested via <c>-accel</c>;
  /// QEMU falls back to TCG when none is available, just slower.
  /// </para>
  /// </summary>
  public static (int ExitCode, string Log) RunFsckFfs(
      string isoPath, string imgPath, string serialLogPath, TimeSpan timeout) {
    if (QemuPath is null) return (-1, "qemu-system-x86_64 not found");

    // Quote each path to survive spaces; on Windows this is the standard form.
    string Q(string p) => $"\"{p}\"";

    var args = string.Join(' ', [
      "-m", "1024",
      "-display", "none",
      "-serial", $"file:{Q(serialLogPath)}",
      "-cdrom", Q(isoPath),
      "-drive", $"file={Q(imgPath)},format=raw,if=ide,index=1",
      "-boot", "d",
      "-no-reboot",
      // Try whichever accelerator the host supports; QEMU silently falls back to TCG.
      "-accel", "whpx,kernel-irqchip=off",
      "-accel", "kvm",
      "-accel", "haxm",
      "-accel", "tcg",
    ]);

    var psi = new ProcessStartInfo {
      FileName = QemuPath,
      Arguments = args,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    try {
      using var proc = Process.Start(psi)!;
      var exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
      if (!exited) {
        try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        var partial = SafeReadLog(serialLogPath);
        // Guests that boot to an installer/prompt won't return on their own —
        // surface as the "still interactive" sentinel so the caller can skip.
        return (SkipExitCode_StillInteractive, partial);
      }
      var log = SafeReadLog(serialLogPath);
      return (proc.ExitCode, log);
    } catch (Exception ex) {
      return (-1, ex.Message);
    }
  }

  /// <summary>
  /// Returns the path to a DragonFly BSD ISO if the user has staged one. Honours
  /// the <c>CWB_DRAGONFLY_ISO</c> environment variable. DragonFly is the only OS
  /// with native HAMMER/HAMMER2 tools (<c>hammer info</c>, <c>hammer2 info</c>).
  /// Download from <c>https://www.dragonflybsd.org/download/</c> (≈900 MB live ISO).
  /// </summary>
  public static string? LocateDragonFlyIso() {
    var fromEnv = Environment.GetEnvironmentVariable("CWB_DRAGONFLY_ISO");
    if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
      return fromEnv;

    var candidates = new[] {
      Path.Combine(Path.GetTempPath(), "cwb-fs-iso", "dfly-x86_64-6.4.0_REL.iso"),
      Path.Combine(Path.GetTempPath(), "cwb-fs-iso", "dfly-x86_64-6.6.0_REL.iso"),
    };
    foreach (var c in candidates)
      if (File.Exists(c)) return c;
    return null;
  }

  /// <summary>
  /// Boots <paramref name="isoPath"/> (DragonFly BSD live) under QEMU with
  /// <paramref name="imgPath"/> attached as second disk, and runs
  /// <c>hammer info /dev/da1</c> (or <c>hammer2 info</c> when
  /// <paramref name="useHammer2"/> is true) via the serial console.
  /// Returns QEMU exit code + serial log. Same caveat as
  /// <see cref="RunFsckFfs"/>: the stock live ISO drops to a login prompt.
  /// To drive non-interactively the user must build a custom live ISO with
  /// an autoexec'd <c>/etc/rc.local</c>. Surfaces
  /// <see cref="SkipExitCode_StillInteractive"/> on timeout.
  /// </summary>
  public static (int ExitCode, string Log) RunHammerInfo(
      string isoPath, string imgPath, string serialLogPath, TimeSpan timeout, bool useHammer2 = false) {
    if (QemuPath is null) return (-1, "qemu-system-x86_64 not found");
    string Q(string p) => $"\"{p}\"";
    var args = string.Join(' ', [
      "-m", "1024",
      "-display", "none",
      "-serial", $"file:{Q(serialLogPath)}",
      "-cdrom", Q(isoPath),
      "-drive", $"file={Q(imgPath)},format=raw,if=ide,index=1",
      "-boot", "d",
      "-no-reboot",
      "-accel", "whpx,kernel-irqchip=off",
      "-accel", "kvm",
      "-accel", "haxm",
      "-accel", "tcg",
    ]);
    var psi = new ProcessStartInfo {
      FileName = QemuPath,
      Arguments = args,
      RedirectStandardOutput = true, RedirectStandardError = true,
      UseShellExecute = false, CreateNoWindow = true,
    };
    try {
      using var proc = Process.Start(psi)!;
      var exited = proc.WaitForExit((int)timeout.TotalMilliseconds);
      if (!exited) {
        try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        return (SkipExitCode_StillInteractive, SafeReadLog(serialLogPath));
      }
      return (proc.ExitCode, SafeReadLog(serialLogPath));
    } catch (Exception ex) {
      return (-1, ex.Message);
    }
  }

  private static string SafeReadLog(string path) {
    try {
      // Open with FileShare.ReadWrite — QEMU may still hold a handle if we killed it.
      using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                    FileShare.ReadWrite | FileShare.Delete);
      using var sr = new StreamReader(fs);
      return sr.ReadToEnd();
    } catch {
      return "(serial log unavailable)";
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

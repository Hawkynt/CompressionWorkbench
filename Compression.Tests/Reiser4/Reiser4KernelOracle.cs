namespace Compression.Tests.Reiser4;

/// <summary>
/// A Reiser4 kernel under QEMU as the oracle: it mounts an image, resolves every path and reads
/// every file whole, and reports what it found as a manifest (see <c>KernelOracle/</c>). Nothing a
/// test does with it is taken on our reader's word.
/// </summary>
/// <remarks>
/// The kernel package is fetched once by <c>kernel-run.sh --prepare</c>; tests do that only when
/// <c>CWB_REISER4_KERNEL_DOWNLOAD=1</c>, and otherwise skip unless it is already in place.
/// </remarks>
internal static class Reiser4KernelOracle {

  private static readonly string ScriptDirectory = Path.Combine(AppContext.BaseDirectory, "Reiser4", "KernelOracle");
  // Lazy: fixtures run in parallel and Prepare() may download the kernel; it must run once.
  private static readonly Lazy<bool> _available = new(Prepare);

  internal static void Require() {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Assert.Ignore("WSL is not available on this machine.");
    if (!_available.Value)
      Assert.Ignore("The Reiser4 kernel oracle is not prepared (set CWB_REISER4_KERNEL_DOWNLOAD=1 to fetch it; needs qemu-system-x86_64).");
  }

  private static bool Prepare() {
    var script = FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "kernel-run.sh"));
    var mode = Environment.GetEnvironmentVariable("CWB_REISER4_KERNEL_DOWNLOAD") == "1" ? "--prepare" : "--check";
    return FsInteropToolbox.RunWsl($"bash {script} {mode}").ExitCode == 0;
  }

  /// <summary>What the kernel made of an image: its manifest and any complaints it logged.</summary>
  internal sealed record Mount(IReadOnlyDictionary<string, Reiser4KernelVolumeTests.ManifestEntry> Manifest, string[] Complaints, string Log);

  /// <summary>
  /// How long qemu may run the guest (<c>CWB_REISER4_KERNEL_TIMEOUT</c>, seconds; 1200 by default,
  /// generous for a software-emulated guest on a loaded host). The script enforces it inside the
  /// Linux environment; the process wrapper here waits that long plus three minutes, so the
  /// script's own report always arrives first.
  /// </summary>
  private static int GuestTimeoutSeconds
    => int.TryParse(Environment.GetEnvironmentVariable("CWB_REISER4_KERNEL_TIMEOUT"), out var seconds) && seconds > 0 ? seconds : 1200;

  /// <summary>Mounts a copy of <paramref name="imagePath" /> and reads everything in it.</summary>
  /// <remarks>
  /// How a run ends decides what the test says (see <c>kernel-run.sh</c>): a guest that never
  /// booted, or a wrapper that had to kill a run qemu should have ended itself, is the host's
  /// failure and skips with the reason; a missing harness file is the repository's and fails; a
  /// guest that booted and mounted the image but did not finish — hung, crashed, or out of time —
  /// fails with its console, since that can be the image's doing.
  /// </remarks>
  internal static Mount Read(string imagePath) {
    var copy = imagePath + ".kernel";
    File.Copy(imagePath, copy, overwrite: true);
    try {
      var script = FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "kernel-run.sh"));
      var guest = FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "manifest.sh"));
      var timeout = GuestTimeoutSeconds;
      var run = FsInteropToolbox.RunWsl($"CWB_REISER4_KERNEL_TIMEOUT={timeout} bash {script} {FsInteropToolbox.WinToWsl(copy)} {guest}",
        TimeSpan.FromSeconds(timeout + 180));
      var log = run.StdOut + "\n" + run.StdErr;
      if (run.TimedOut)
        Assert.Ignore($"infrastructure: the Linux environment did not return within {timeout + 180} s although qemu is limited to {timeout} s.");
      switch (run.ExitCode) {
        case 0:
          break;
        case 2:
          Assert.Ignore($"infrastructure: the Reiser4 guest kernel did not boot (no ===GUEST-BEGIN===):\n{Tail(log)}");
          break;
        case 3:
          Assert.Fail($"the kernel oracle harness is incomplete:\n{log}");
          break;
        case 4:
          Assert.Fail((log.Contains("===QEMU rc=124", StringComparison.Ordinal)
              ? $"the guest mounted the image but did not finish within {timeout} s (raise CWB_REISER4_KERNEL_TIMEOUT if the host is overloaded):\n"
              : "the guest mounted the image but did not finish (it hung or crashed):\n") + log);
          break;
        default:
          Assert.Fail($"the kernel run failed (exit {run.ExitCode}):\n{log}");
          break;
      }
      Assert.That(log, Does.Not.Contain("MOUNTFAIL"), $"the kernel refused to mount the volume:\n{log}");
      var complaints = log.Split('\n').Where(static l => l.StartsWith("dmesg:", StringComparison.Ordinal))
        .Where(static l => l.Contains("WARNING", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
          l.Contains("bad", StringComparison.OrdinalIgnoreCase) || l.Contains("fsck", StringComparison.OrdinalIgnoreCase) ||
          l.Contains("BUG", StringComparison.Ordinal) || l.Contains("Oops", StringComparison.Ordinal))
        .ToArray();
      var start = log.IndexOf("===MANIFEST===", StringComparison.Ordinal);
      var end = log.IndexOf("===END-MANIFEST===", StringComparison.Ordinal);
      Assert.That(start >= 0 && end > start, Is.True, $"the kernel printed no manifest:\n{log}");
      return new Mount(Reiser4KernelVolumeTests.ParseManifest(log[start..end]), complaints, log);
    } finally {
      try { File.Delete(copy); } catch { /* best effort */ }
    }
  }

  private static string Tail(string log) => string.Join('\n', log.Split('\n').TakeLast(60));
}

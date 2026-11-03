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
  private static bool? _available;

  internal static void Require() {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Assert.Ignore("WSL is not available on this machine.");
    _available ??= Prepare();
    if (_available != true)
      Assert.Ignore("The Reiser4 kernel oracle is not prepared (set CWB_REISER4_KERNEL_DOWNLOAD=1 to fetch it; needs qemu-system-x86_64).");
  }

  private static bool Prepare() {
    var script = FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "kernel-run.sh"));
    var mode = Environment.GetEnvironmentVariable("CWB_REISER4_KERNEL_DOWNLOAD") == "1" ? "--prepare" : "--check";
    return FsInteropToolbox.RunWsl($"bash {script} {mode}").ExitCode == 0;
  }

  /// <summary>What the kernel made of an image: its manifest and any complaints it logged.</summary>
  internal sealed record Mount(IReadOnlyDictionary<string, Reiser4KernelVolumeTests.ManifestEntry> Manifest, string[] Complaints, string Log);

  /// <summary>Mounts a copy of <paramref name="imagePath" /> and reads everything in it.</summary>
  internal static Mount Read(string imagePath) {
    var copy = imagePath + ".kernel";
    File.Copy(imagePath, copy, overwrite: true);
    try {
      var script = FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "kernel-run.sh"));
      var guest = FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "manifest.sh"));
      var run = FsInteropToolbox.RunWsl($"bash {script} {FsInteropToolbox.WinToWsl(copy)} {guest}");
      var log = run.StdOut + "\n" + run.StdErr;
      Assert.That(run.ExitCode, Is.EqualTo(0), $"the kernel run did not finish:\n{log}");
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
}

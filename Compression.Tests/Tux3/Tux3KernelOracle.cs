using Compression.Tests.Reiser4;

namespace Compression.Tests.Tux3;

/// <summary>
/// The linux-tux3 kernel under QEMU as the oracle (<c>KernelOracle/kernel-run.sh</c>): it mounts an
/// image, resolves every path, reads every file whole and prints a manifest. The kernel is built
/// from source as <c>docs/TUX3-ON-DISK.md</c> ("Kernel oracle") records; tests skip without it.
/// </summary>
internal static class Tux3KernelOracle {

  private static readonly string ScriptDirectory = Path.Combine(AppContext.BaseDirectory, "Tux3", "KernelOracle");

  private static readonly Lazy<bool> _available = new(static () =>
    FsInteropToolbox.RunWsl($"bash {FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "kernel-run.sh"))} --check").ExitCode == 0);

  internal static void Require() {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Assert.Ignore("WSL is not available on this machine.");
    if (!_available.Value)
      Assert.Ignore("The linux-tux3 kernel oracle is not built (CWB_TUX3_BZIMAGE; see docs/TUX3-ON-DISK.md, \"Kernel oracle\").");
  }

  /// <summary>What the kernel made of an image: its manifest, its complaints and the whole log.</summary>
  internal sealed record Mount(IReadOnlyDictionary<string, Reiser4KernelVolumeTests.ManifestEntry> Manifest, string[] Complaints, string Log);

  /// <summary>Mounts <paramref name="imagePath" /> and reads everything in it.</summary>
  /// <param name="prelude">Guest commands run first, with the volume unmounted on entry and exit;
  /// when given, the kernel writes to <paramref name="imagePath" /> itself instead of a copy.</param>
  internal static Mount Read(string imagePath, string? prelude = null) {
    var image = prelude is null ? imagePath + ".kernel" : imagePath;
    if (prelude is null) File.Copy(imagePath, image, overwrite: true);
    var guestPath = imagePath + ".guest.sh";
    try {
      var manifest = Path.Combine(ScriptDirectory, "manifest.sh");
      File.WriteAllText(guestPath, (prelude ?? "").ReplaceLineEndings("\n") + "\n" + File.ReadAllText(manifest).ReplaceLineEndings("\n"));
      var script = FsInteropToolbox.WinToWsl(Path.Combine(ScriptDirectory, "kernel-run.sh"));
      var run = FsInteropToolbox.RunWsl($"CWB_TUX3_KERNEL_TIMEOUT=300 bash {script} {FsInteropToolbox.WinToWsl(image)} {FsInteropToolbox.WinToWsl(guestPath)}",
        TimeSpan.FromSeconds(480));
      var log = run.StdOut + "\n" + run.StdErr;
      if (run.TimedOut) Assert.Ignore("infrastructure: the Linux environment did not return in time.");
      if (run.ExitCode == 2) Assert.Ignore("infrastructure: the tux3 guest kernel did not boot:\n" + log);
      Assert.That(run.ExitCode, Is.EqualTo(0), "the kernel run failed:\n" + log);
      Assert.That(log, Does.Not.Contain("MOUNTFAIL"), "the kernel refused to mount the volume:\n" + log);
      // The kernel warns on every sync without waiting ("sync_fs wait==0 is unsupported"); that says
      // nothing about the volume.
      var complaints = log.Split('\n').Where(static l => l.StartsWith("dmesg:", StringComparison.Ordinal))
        .Where(static l => l.Contains("tux3", StringComparison.OrdinalIgnoreCase) || l.Contains("BUG", StringComparison.Ordinal) || l.Contains("Oops", StringComparison.Ordinal))
        .Where(static l => !l.Contains("s_blocksize", StringComparison.Ordinal) && !l.Contains("sync_fs wait==0", StringComparison.Ordinal))
        .ToArray();
      var start = log.IndexOf("===MANIFEST===", StringComparison.Ordinal);
      var end = log.IndexOf("===END-MANIFEST===", StringComparison.Ordinal);
      Assert.That(start >= 0 && end > start, Is.True, "the kernel printed no manifest:\n" + log);
      return new Mount(Reiser4KernelVolumeTests.ParseManifest(log[start..end]), complaints, log);
    } finally {
      try {
        if (prelude is null) File.Delete(image);
        File.Delete(guestPath);
      } catch { /* best effort */ }
    }
  }
}

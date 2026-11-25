namespace Compression.Tests.Reiser4;

/// <summary>
/// The oracle for short-key (three-word key) volumes: a Linux kernel built with
/// <c>REISER4_LARGE_KEY=0</c>, booted by <c>KernelOracle/kernel-run.sh</c>, and a reiser4progs
/// whose <c>mkfs.reiser4 -o key=key_short</c> and <c>fsck.reiser4</c> work on such volumes.
/// No distribution ships either: both are built from source with local oracle-only fixes, as
/// <c>docs/REISER4-NATIVE-PROFILE.md</c> ("Short keys") records. Tests skip unless both are in place.
/// </summary>
/// <remarks>
/// Linux paths: <c>CWB_REISER4_SHORT_BZIMAGE</c> names the kernel image (default
/// <c>~/.cache/cwb/reiser4-short/bzImage</c>) and <c>CWB_REISER4_SHORT_PROGS</c> the directory of
/// the reiser4progs programs (default <c>~/.cache/cwb/reiser4-short/sbin</c>).
/// </remarks>
internal static class Reiser4ShortKeyOracle {

  internal static string KernelImage => Environment.GetEnvironmentVariable("CWB_REISER4_SHORT_BZIMAGE") is { Length: > 0 } path
    ? path : "$HOME/.cache/cwb/reiser4-short/bzImage";

  internal static string Programs => Environment.GetEnvironmentVariable("CWB_REISER4_SHORT_PROGS") is { Length: > 0 } path
    ? path : "$HOME/.cache/cwb/reiser4-short/sbin";

  private static readonly Lazy<bool> _available = new(static () => {
    var script = FsInteropToolbox.WinToWsl(Path.Combine(AppContext.BaseDirectory, "Reiser4", "KernelOracle", "kernel-run.sh"));
    return FsInteropToolbox.RunWsl($"CWB_REISER4_BZIMAGE={KernelImage} bash {script} --check && test -x {Programs}/fsck.reiser4").ExitCode == 0;
  });

  internal static void Require() {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Assert.Ignore("WSL is not available on this machine.");
    if (!_available.Value)
      Assert.Ignore("The short-key Reiser4 oracle is not built (a REISER4_LARGE_KEY=0 kernel at CWB_REISER4_SHORT_BZIMAGE and a fixed " +
        "reiser4progs at CWB_REISER4_SHORT_PROGS; see docs/REISER4-NATIVE-PROFILE.md, \"Short keys\").");
  }

  /// <summary>The short-key fsck.reiser4 --check on a copy of <paramref name="path" />.</summary>
  internal static void AssertFsckConsistent(string path) {
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"cp {wsl} {wsl}.fsck && {Programs}/fsck.reiser4 --check -y {wsl}.fsck; rc=$?; rm -f {wsl}.fsck; exit $rc");
    var text = run.StdOut + "\n" + run.StdErr;
    Assert.That(run.ExitCode, Is.EqualTo(0), $"fsck.reiser4 (short keys) rejected {Path.GetFileName(path)}:\n{text}");
    Assert.That(text, Does.Contain("FS is consistent"), $"fsck.reiser4 (short keys) did not call {Path.GetFileName(path)} consistent:\n{text}");
    Assert.That(text, Does.Contain("SHORT"), $"fsck.reiser4 did not take {Path.GetFileName(path)} for a short-key volume:\n{text}");
  }

  /// <summary>A fresh short-key volume as the fixed <c>mkfs.reiser4 -o key=key_short</c> formats it.</summary>
  internal static byte[] Mkfs(string path, string options = "") {
    var wsl = FsInteropToolbox.WinToWsl(path);
    var run = FsInteropToolbox.RunWsl($"rm -f {wsl} && truncate -s 16M {wsl} && {Programs}/mkfs.reiser4 -fffy -o key=key_short{options} {wsl}");
    Assert.That(run.ExitCode, Is.EqualTo(0), $"mkfs.reiser4 -o key=key_short failed:\n{run.StdOut}\n{run.StdErr}");
    return File.ReadAllBytes(path);
  }
}

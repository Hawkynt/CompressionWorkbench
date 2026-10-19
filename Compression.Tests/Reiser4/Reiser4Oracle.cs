namespace Compression.Tests.Reiser4;

/// <summary>
/// Where reiser4progs is looked for, and what its absence means. On a developer machine a
/// missing tool skips the oracle tests; the CI gate sets <c>CWB_REQUIRE_REISER4PROGS=1</c>, which
/// turns the same absence into a failure so the gate can never pass by skipping.
/// </summary>
internal static class Reiser4Oracle {

  internal static bool Required => Environment.GetEnvironmentVariable("CWB_REQUIRE_REISER4PROGS") == "1";

  /// <summary>Skips, or fails under the gate, unless every named tool is reachable.</summary>
  internal static void Require(params string[] tools) {
    if (!FsInteropToolbox.WslAvailable && OperatingSystem.IsWindows())
      Missing("WSL is not available on this machine.");
    foreach (var tool in tools.Length == 0 ? ["mkfs.reiser4", "fsck.reiser4", "debugfs.reiser4"] : tools)
      if (!FsInteropToolbox.WslHasTool(tool))
        Missing($"reiser4progs is not installed ('{tool}' missing; `sudo apt install -y reiser4progs`).");
  }

  /// <summary>Reports a missing oracle: a skip normally, a failure under the gate.</summary>
  internal static void Missing(string reason) {
    if (Required) Assert.Fail(reason + " The Reiser4 oracle gate requires it.");
    Assert.Ignore(reason);
  }
}

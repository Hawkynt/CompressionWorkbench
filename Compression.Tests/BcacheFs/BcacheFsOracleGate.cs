namespace Compression.Tests.BcacheFs;

/// <summary>
/// What a bcachefs oracle test does when its oracle is missing.
/// </summary>
/// <remarks>
/// Everywhere else a missing tool is a skip with an install hint. On the CI job
/// whose runner is pinned so the tools are there, <c>CWB_REQUIRE_BCACHEFS=1</c>
/// (bcachefs-tools) and <c>CWB_REQUIRE_BCACHEFS_KERNEL=1</c> (a kernel with the
/// driver) make it a failure instead: a gate that silently skips when the runner
/// image changes under it is no gate.
/// </remarks>
internal static class BcacheFsOracleGate {

  internal const string RequireTools = "CWB_REQUIRE_BCACHEFS";
  internal const string RequireKernel = "CWB_REQUIRE_BCACHEFS_KERNEL";

  /// <summary>Skips, or fails where <paramref name="variable" /> says the oracle is expected.</summary>
  internal static void Missing(string variable, string reason) {
    if (Environment.GetEnvironmentVariable(variable) == "1")
      Assert.Fail($"{reason} ({variable}=1 says it is expected here.)");
    Assert.Ignore(reason);
  }

  /// <summary>bcachefs-tools in the Linux environment, or a skip (a failure where it is required).</summary>
  internal static void RequireBcachefsTools() {
    if (!FsInteropToolbox.WslAvailable)
      Missing(RequireTools, "No Linux environment (WSL) to run bcachefs-tools in.");
    if (!FsInteropToolbox.WslHasTool("bcachefs"))
      Missing(RequireTools, "bcachefs-tools is not installed: `sudo apt install -y bcachefs-tools`.");
  }
}

#pragma warning disable CS1591

namespace Compression.Analysis.ExternalTools;

/// <summary>
/// Result of probing unknown data with an external tool.
/// </summary>
/// <param name="ToolName">Name of the external tool that was used.</param>
/// <param name="DetectedType">The type/format detected by the tool, or null if unrecognized.</param>
/// <param name="Confidence">Confidence level (0.0-1.0) based on tool output quality.</param>
/// <param name="RawOutput">Raw stdout from the tool invocation.</param>
/// <param name="Succeeded">Whether the tool executed successfully and recognized the data.</param>
public sealed record BlackBoxProbeResult(
  string ToolName,
  string? DetectedType,
  double Confidence,
  string RawOutput,
  bool Succeeded
);

/// <summary>
/// Aggregate result of probing unknown data with all available external tools.
/// </summary>
public sealed class ProbeReport {
  /// <summary>Individual results from each tool that was tried.</summary>
  public required List<BlackBoxProbeResult> Results { get; init; }

  /// <summary>The best guess across all tools (highest confidence successful result), or null.</summary>
  public BlackBoxProbeResult? BestGuess { get; init; }
}

/// <summary>
/// Feeds unknown binary data to available external tools and observes whether they
/// recognize it, providing a black-box approach to format identification.
/// </summary>
public static class BlackBoxProber {

  /// <summary>Default timeout in milliseconds for each tool probe (10 seconds).</summary>
  public const int DefaultProbeTimeoutMs = 10_000;

  /// <summary>
  /// Probes unknown data by writing it to a temp file and feeding it to available external tools.
  /// Tries: <c>file --mime-type</c>, <c>7z l</c>, and <c>binwalk</c>.
  /// </summary>
  /// <param name="data">The binary data to identify.</param>
  /// <param name="timeoutMs">Timeout per tool invocation.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>A <see cref="ProbeReport"/> summarizing all tool results.</returns>
  public static async Task<ProbeReport> ProbeFormatAsync(
    byte[] data,
    int timeoutMs = DefaultProbeTimeoutMs,
    CancellationToken cancellationToken = default
  ) {
    var results = new List<BlackBoxProbeResult>();
    var tempPath = Path.GetTempFileName();

    try {
      await File.WriteAllBytesAsync(tempPath, data, cancellationToken).ConfigureAwait(false);

      // Probe with each available tool concurrently
      var tasks = new List<Task<BlackBoxProbeResult?>>();

      tasks.Add(TryFileCommand(tempPath, timeoutMs, cancellationToken));
      tasks.Add(Try7zList(tempPath, timeoutMs, cancellationToken));
      tasks.Add(TryBinwalk(tempPath, timeoutMs, cancellationToken));

      var probeResults = await Task.WhenAll(tasks).ConfigureAwait(false);

      foreach (var r in probeResults) {
        if (r != null)
          results.Add(r);
      }
    } finally {
      try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
    }

    // Sort by confidence descending
    results.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

    var bestGuess = results.Find(r => r.Succeeded);

    return new ProbeReport {
      Results = results,
      BestGuess = bestGuess
    };
  }

  /// <summary>
  /// Synchronous convenience overload for <see cref="ProbeFormatAsync"/>.
  /// </summary>
  public static ProbeReport ProbeFormat(byte[] data, int timeoutMs = DefaultProbeTimeoutMs) =>
    ProbeFormatAsync(data, timeoutMs).GetAwaiter().GetResult();

  // ── Tool-specific probes ────────────────────────────────────────────

  private static async Task<BlackBoxProbeResult?> TryFileCommand(
    string tempPath, int timeoutMs, CancellationToken ct
  ) {
    var fileTool = ToolDiscovery.GetToolPath("file");
    if (fileTool == null)
      return null;

    try {
      ct.ThrowIfCancellationRequested();

      // Run "file --mime-type -b <path>"
      var run = await ExternalToolRunner.RunAsync(
        fileTool,
        $"--mime-type -b \"{tempPath}\"",
        timeoutMs: timeoutMs
      ).ConfigureAwait(false);

      if (run.TimedOut)
        return new BlackBoxProbeResult("file", null, 0, "(timed out)", false);

      var mime = ToolOutputParser.ParseFileMimeType(run.Stdout);
      if (mime == null)
        return new BlackBoxProbeResult("file", null, 0, run.Stdout.Trim(), false);

      // "application/octet-stream" means "I don't know" -- low confidence
      var isGeneric = mime is "application/octet-stream" or "text/plain";
      var confidence = isGeneric ? 0.1 : 0.8;

      return new BlackBoxProbeResult("file", mime, confidence, run.Stdout.Trim(), !isGeneric);
    } catch (OperationCanceledException) {
      return null;
    }
  }

  private static async Task<BlackBoxProbeResult?> Try7zList(
    string tempPath, int timeoutMs, CancellationToken ct
  ) {
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip == null)
      return null;

    try {
      ct.ThrowIfCancellationRequested();

      // Run "7z l <path>" to see if it recognizes and can list contents
      var run = await ExternalToolRunner.RunAsync(
        sevenZip,
        $"l \"{tempPath}\"",
        timeoutMs: timeoutMs
      ).ConfigureAwait(false);

      if (run.TimedOut)
        return new BlackBoxProbeResult("7z", null, 0, "(timed out)", false);

      if (!run.Success)
        return new BlackBoxProbeResult("7z", null, 0, run.Stderr.Trim(), false);

      // Extract the archive type from output
      var archiveType = ExtractArchiveType(run.Stdout);
      if (archiveType == null)
        return new BlackBoxProbeResult("7z", null, 0.1, run.Stdout.Trim(), false);

      // Parse entry count for confidence adjustment
      var entries = ToolOutputParser.Parse7zSimpleList(run.Stdout);
      var confidence = entries.Count > 0 ? 0.85 : 0.6;

      var detectedType = $"archive/{archiveType.ToLowerInvariant()}";
      return new BlackBoxProbeResult("7z", detectedType, confidence, run.Stdout.Trim(), true);
    } catch (OperationCanceledException) {
      return null;
    }
  }

  private static async Task<BlackBoxProbeResult?> TryBinwalk(
    string tempPath, int timeoutMs, CancellationToken ct
  ) {
    var binwalk = ToolDiscovery.GetToolPath("binwalk");
    if (binwalk == null)
      return null;

    try {
      ct.ThrowIfCancellationRequested();

      // Run "binwalk <path>" for embedded signature scanning
      var run = await ExternalToolRunner.RunAsync(
        binwalk,
        $"\"{tempPath}\"",
        timeoutMs: timeoutMs
      ).ConfigureAwait(false);

      if (run.TimedOut)
        return new BlackBoxProbeResult("binwalk", null, 0, "(timed out)", false);

      var entries = ToolOutputParser.ParseBinwalkOutput(run.Stdout);
      if (entries.Count == 0)
        return new BlackBoxProbeResult("binwalk", null, 0, run.Stdout.Trim(), false);

      // Use the first entry (at lowest offset) as the primary detection
      var primary = entries[0];
      var detectedType = primary.Description;
      var confidence = entries.Count > 1 ? 0.7 : 0.5;

      // Higher confidence if found at offset 0
      if (primary.Offset == 0)
        confidence += 0.1;

      return new BlackBoxProbeResult("binwalk", detectedType, Math.Min(1.0, confidence), run.Stdout.Trim(), true);
    } catch (OperationCanceledException) {
      return null;
    }
  }

  private static string? ExtractArchiveType(string output) {
    foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
      var trimmed = line.Trim();
      if (trimmed.StartsWith("Type = ", StringComparison.OrdinalIgnoreCase))
        return trimmed["Type = ".Length..].Trim();
    }
    return null;
  }
}

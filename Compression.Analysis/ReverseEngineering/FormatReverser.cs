#pragma warning disable CS1591

using Compression.Analysis.ExternalTools;

namespace Compression.Analysis.ReverseEngineering;

/// <summary>
/// Orchestrates black-box format reverse engineering: runs an unknown tool with controlled
/// probe inputs, collects outputs, and analyzes them to discover the output format structure
/// (headers, size fields, compression algorithm, filename storage, etc.).
/// </summary>
public sealed class FormatReverser {

  /// <summary>
  /// Full report of what was discovered about an unknown tool's output format.
  /// </summary>
  public sealed class ReverseEngineeringReport {
    public required string ToolExecutable { get; init; }
    public required string ArgumentTemplate { get; init; }
    public required int ProbesRun { get; init; }
    public required int ProbesSucceeded { get; init; }
    public required OutputCorrelator.FixedRegion? Header { get; init; }
    public required OutputCorrelator.FixedRegion? Footer { get; init; }
    public required List<OutputCorrelator.SizeField> SizeFields { get; init; }
    public required OutputCorrelator.PayloadInfo Payload { get; init; }
    public required CompressionIdentifier.IdentificationResult CompressionAnalysis { get; init; }
    public required List<ProbeRunResult> AllRuns { get; init; }
    public required string Summary { get; init; }
  }

  /// <summary>Result of a single probe run.</summary>
  public sealed class ProbeRunResult {
    public required string ProbeName { get; init; }
    public required int InputSize { get; init; }
    public required int OutputSize { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
  }

  private readonly string _executable;
  private readonly string _argumentTemplate;
  private readonly int _timeoutMs;

  /// <summary>
  /// Creates a format reverser for the given tool.
  /// </summary>
  /// <param name="executable">Path or name of the tool executable.</param>
  /// <param name="argumentTemplate">
  /// Argument template with placeholders:
  /// <c>{input}</c> = input file path, <c>{output}</c> = output file path.
  /// Example: <c>"{input}" "{output}"</c> or <c>--compress "{input}" -o "{output}"</c>
  /// </param>
  /// <param name="timeoutMs">Timeout per tool invocation in milliseconds.</param>
  public FormatReverser(string executable, string argumentTemplate, int timeoutMs = 30000) {
    _executable = executable;
    _argumentTemplate = argumentTemplate;
    _timeoutMs = timeoutMs;
  }

  /// <summary>
  /// Runs the full reverse engineering pipeline: probe → collect → correlate → identify.
  /// </summary>
  /// <param name="progress">Optional callback for progress reporting (probeName, current, total).</param>
  /// <param name="ct">Cancellation token.</param>
  public async Task<ReverseEngineeringReport> AnalyzeAsync(
    Action<string, int, int>? progress = null,
    CancellationToken ct = default
  ) {
    // Generate probes.
    var probes = ProbeGenerator.GenerateStandardProbes();
    var sizeProbes = ProbeGenerator.GenerateSizeProbes();
    var allProbes = new List<ProbeGenerator.Probe>();
    allProbes.AddRange(probes);
    allProbes.AddRange(sizeProbes);

    // Run each probe through the tool.
    var runs = new List<OutputCorrelator.ProbeRun>();
    var allResults = new List<ProbeRunResult>();
    var succeeded = 0;

    for (var i = 0; i < allProbes.Count; i++) {
      ct.ThrowIfCancellationRequested();
      var probe = allProbes[i];
      progress?.Invoke(probe.Name, i + 1, allProbes.Count);

      var (output, error) = await RunToolAsync(probe, ct).ConfigureAwait(false);

      if (output != null) {
        runs.Add(new() { Input = probe, Output = output });
        allResults.Add(new() { ProbeName = probe.Name, InputSize = probe.Data.Length, OutputSize = output.Length, Success = true });
        succeeded++;
      } else {
        allResults.Add(new() { ProbeName = probe.Name, InputSize = probe.Data.Length, OutputSize = 0, Success = false, Error = error });
      }
    }

    if (runs.Count < 3)
      return BuildReport(runs, allResults, succeeded, null, null, [], DefaultPayload(), DefaultCompression(), "Too few successful probes to analyze.");

    // Correlate outputs.
    var header = OutputCorrelator.FindCommonHeader(runs);
    var footer = OutputCorrelator.FindCommonFooter(runs);
    var headerSize = header?.Length ?? 0;
    var footerSize = footer?.Length ?? 0;

    var sizeFields = OutputCorrelator.FindSizeFields(runs);
    var payload = OutputCorrelator.AnalyzePayload(runs, headerSize, footerSize);

    // Extract a representative payload for compression identification.
    var repRun = runs.FirstOrDefault(r => r.Input.Name == "4k-text")
                 ?? runs.FirstOrDefault(r => r.Output.Length > headerSize + footerSize + 16);

    CompressionIdentifier.IdentificationResult compression;
    if (repRun != null && repRun.Output.Length > headerSize + footerSize) {
      var payloadEnd = repRun.Output.Length - footerSize;
      var payloadData = repRun.Output.AsSpan(headerSize, payloadEnd - headerSize);

      // Use detected input_size field as expected decompressed size.
      var expectedSize = sizeFields.FirstOrDefault(f => f.Meaning == "input_size") != null
        ? repRun.Input.Data.Length
        : -1;

      compression = CompressionIdentifier.Identify(payloadData, expectedSize);
    } else {
      compression = DefaultCompression();
    }

    // Build summary.
    var summary = BuildSummary(header, footer, sizeFields, payload, compression);

    return BuildReport(runs, allResults, succeeded, header, footer, sizeFields, payload, compression, summary);
  }

  /// <summary>Synchronous convenience overload.</summary>
  public ReverseEngineeringReport Analyze(Action<string, int, int>? progress = null) =>
    AnalyzeAsync(progress).GetAwaiter().GetResult();

  private async Task<(byte[]? Output, string? Error)> RunToolAsync(ProbeGenerator.Probe probe, CancellationToken ct) {
    var tempDir = Path.Combine(Path.GetTempPath(), "cwb-reverse-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tempDir);

    try {
      // Write probe input to temp file.
      var inputPath = Path.Combine(tempDir, probe.FileName);
      await File.WriteAllBytesAsync(inputPath, probe.Data, ct).ConfigureAwait(false);

      var outputPath = Path.Combine(tempDir, "output.bin");

      // Expand template.
      var args = _argumentTemplate
        .Replace("{input}", inputPath, StringComparison.OrdinalIgnoreCase)
        .Replace("{output}", outputPath, StringComparison.OrdinalIgnoreCase);

      // Resolve executable.
      var exe = _executable;
      if (!Path.IsPathRooted(exe)) {
        var resolved = ToolDiscovery.GetToolPath(exe);
        if (resolved != null) exe = resolved;
      }

      var result = await ExternalToolRunner.RunAsync(exe, args, timeoutMs: _timeoutMs).ConfigureAwait(false);

      if (result.TimedOut)
        return (null, "Timed out");

      // Check if output file was created.
      if (File.Exists(outputPath))
        return (await File.ReadAllBytesAsync(outputPath, ct).ConfigureAwait(false), null);

      // Maybe the tool wrote to stdout?
      if (result.StdoutBytes is { Length: > 0 })
        return (result.StdoutBytes, null);

      // Maybe the tool modified the input file in-place or created a file with a different name?
      var createdFiles = Directory.GetFiles(tempDir)
        .Where(f => f != inputPath)
        .OrderByDescending(f => new FileInfo(f).Length)
        .ToList();

      if (createdFiles.Count > 0)
        return (await File.ReadAllBytesAsync(createdFiles[0], ct).ConfigureAwait(false), null);

      return (null, $"Exit code {result.ExitCode}, no output file found. stderr: {result.Stderr}");
    } catch (Exception ex) {
      return (null, ex.Message);
    } finally {
      try { Directory.Delete(tempDir, true); } catch { /* cleanup best-effort */ }
    }
  }

  private ReverseEngineeringReport BuildReport(
    List<OutputCorrelator.ProbeRun> runs,
    List<ProbeRunResult> allResults,
    int succeeded,
    OutputCorrelator.FixedRegion? header,
    OutputCorrelator.FixedRegion? footer,
    List<OutputCorrelator.SizeField> sizeFields,
    OutputCorrelator.PayloadInfo payload,
    CompressionIdentifier.IdentificationResult compression,
    string summary
  ) => new() {
    ToolExecutable = _executable,
    ArgumentTemplate = _argumentTemplate,
    ProbesRun = allResults.Count,
    ProbesSucceeded = succeeded,
    Header = header,
    Footer = footer,
    SizeFields = sizeFields,
    Payload = payload,
    CompressionAnalysis = compression,
    AllRuns = allResults,
    Summary = summary
  };

  private static string BuildSummary(
    OutputCorrelator.FixedRegion? header,
    OutputCorrelator.FixedRegion? footer,
    List<OutputCorrelator.SizeField> sizeFields,
    OutputCorrelator.PayloadInfo payload,
    CompressionIdentifier.IdentificationResult compression
  ) {
    var lines = new List<string> { "=== Format Reverse Engineering Report ===" };

    if (header != null)
      lines.Add($"Header: {header.Length} bytes — magic: [{string.Join(" ", header.Bytes.Select(b => $"0x{b:X2}"))}]");
    else
      lines.Add("Header: none detected (no common prefix)");

    if (footer != null)
      lines.Add($"Footer: {footer.Length} bytes — [{string.Join(" ", footer.Bytes.Select(b => $"0x{b:X2}"))}]");

    foreach (var sf in sizeFields)
      lines.Add($"Size field at offset {sf.Offset}: {sf.Width}-byte {sf.Endianness}, meaning: {sf.Meaning}");

    lines.Add($"Stores filename: {(payload.StoresFilename ? "YES" : "no")}");
    lines.Add($"Deterministic: {(payload.IsDeterministic ? "YES" : "no")}");
    lines.Add($"Payload entropy: {payload.PayloadEntropy:F2} — {(payload.IsCompressed ? "COMPRESSED" : "uncompressed/encoded")}");
    lines.Add($"Average output/input ratio: {payload.CompressionRatio:F2}x");

    if (compression.Signatures.Count > 0) {
      lines.Add("Known signatures in payload:");
      foreach (var sig in compression.Signatures)
        lines.Add($"  offset {sig.Offset}: {sig.FormatName}");
    }

    if (compression.Matches.Count > 0) {
      lines.Add("Building blocks that successfully decompressed the payload:");
      foreach (var m in compression.Matches.Take(5))
        lines.Add($"  {m.DisplayName} ({m.Family}) — decompressed to {m.DecompressedSize} bytes, confidence: {m.Confidence:P0}");
    }

    lines.Add($"Entropy classification: {compression.EntropyClass}");
    if (compression.BestGuess != null)
      lines.Add($"Best guess: {compression.BestGuess}");

    return string.Join(Environment.NewLine, lines);
  }

  private static OutputCorrelator.PayloadInfo DefaultPayload() => new() {
    HeaderSize = 0, FooterSize = 0, PayloadEntropy = 0,
    IsCompressed = false, StoresFilename = false, IsDeterministic = true, CompressionRatio = 1.0
  };

  private static CompressionIdentifier.IdentificationResult DefaultCompression() => new() {
    Matches = [], Signatures = [], EntropyClass = "Unknown"
  };
}

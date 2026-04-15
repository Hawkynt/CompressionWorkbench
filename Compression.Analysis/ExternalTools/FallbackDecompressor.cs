#pragma warning disable CS1591

namespace Compression.Analysis.ExternalTools;

/// <summary>
/// Result of a fallback decompression attempt.
/// </summary>
public sealed class FallbackResult {
  /// <summary>Whether decompression succeeded.</summary>
  public required bool Success { get; init; }

  /// <summary>Which external tool succeeded (null if none did).</summary>
  public string? ToolUsed { get; init; }

  /// <summary>Error message if all tools failed.</summary>
  public string? Error { get; init; }

  /// <summary>Paths to extracted files (for archives) or the single output file (for streams).</summary>
  public List<string> ExtractedFiles { get; init; } = [];
}

/// <summary>
/// Attempts decompression/extraction using external tools when our built-in decompressor fails.
/// Tries tools in order of capability: 7z (broadest format support), then format-specific tools.
/// </summary>
public static class FallbackDecompressor {

  /// <summary>
  /// Tries to decompress/extract the given file using external tools.
  /// </summary>
  /// <param name="filePath">Path to the compressed file.</param>
  /// <param name="outputDirectory">Directory to extract contents into.</param>
  /// <param name="timeoutMs">Timeout per tool invocation.</param>
  /// <returns>Result indicating success and which tool was used.</returns>
  public static async Task<FallbackResult> TryDecompressAsync(
    string filePath,
    string outputDirectory,
    int timeoutMs = ExternalToolRunner.DefaultTimeoutMs
  ) {
    Directory.CreateDirectory(outputDirectory);

    // Try 7z first — it supports the broadest range of formats.
    var result = await TryWith7zAsync(filePath, outputDirectory, timeoutMs).ConfigureAwait(false);
    if (result.Success)
      return result;

    // Try format-specific tools based on file extension.
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    result = ext switch {
      ".gz" => await TryWithToolAsync("gzip", $"-d -k -c \"{filePath}\"", filePath, outputDirectory, timeoutMs, useStdoutCapture: true).ConfigureAwait(false),
      ".bz2" => await TryWithToolAsync("bzip2", $"-d -k -c \"{filePath}\"", filePath, outputDirectory, timeoutMs, useStdoutCapture: true).ConfigureAwait(false),
      ".xz" => await TryWithToolAsync("xz", $"-d -k -c \"{filePath}\"", filePath, outputDirectory, timeoutMs, useStdoutCapture: true).ConfigureAwait(false),
      ".zst" => await TryWithToolAsync("zstd", $"-d -c \"{filePath}\"", filePath, outputDirectory, timeoutMs, useStdoutCapture: true).ConfigureAwait(false),
      ".lz4" => await TryWithToolAsync("lz4", $"-d -c \"{filePath}\"", filePath, outputDirectory, timeoutMs, useStdoutCapture: true).ConfigureAwait(false),
      ".tar" => await TryWithTarAsync(filePath, outputDirectory, timeoutMs).ConfigureAwait(false),
      ".rar" => await TryWithUnrarAsync(filePath, outputDirectory, timeoutMs).ConfigureAwait(false),
      _ => new FallbackResult { Success = false, Error = $"No fallback tool for extension '{ext}'" }
    };

    if (result.Success)
      return result;

    // For compound extensions like .tar.gz, .tar.bz2, etc.
    var lowerPath = filePath.ToLowerInvariant();
    if (lowerPath.EndsWith(".tar.gz", StringComparison.Ordinal) || lowerPath.EndsWith(".tgz", StringComparison.Ordinal)) {
      result = await TryWithTarAsync(filePath, outputDirectory, timeoutMs, "z").ConfigureAwait(false);
      if (result.Success)
        return result;
    } else if (lowerPath.EndsWith(".tar.bz2", StringComparison.Ordinal) || lowerPath.EndsWith(".tbz2", StringComparison.Ordinal)) {
      result = await TryWithTarAsync(filePath, outputDirectory, timeoutMs, "j").ConfigureAwait(false);
      if (result.Success)
        return result;
    } else if (lowerPath.EndsWith(".tar.xz", StringComparison.Ordinal) || lowerPath.EndsWith(".txz", StringComparison.Ordinal)) {
      result = await TryWithTarAsync(filePath, outputDirectory, timeoutMs, "J").ConfigureAwait(false);
      if (result.Success)
        return result;
    }

    return new FallbackResult {
      Success = false,
      Error = "All fallback tools failed or were unavailable"
    };
  }

  /// <summary>
  /// Synchronous convenience overload.
  /// </summary>
  public static FallbackResult TryDecompress(string filePath, string outputDirectory, int timeoutMs = ExternalToolRunner.DefaultTimeoutMs) =>
    TryDecompressAsync(filePath, outputDirectory, timeoutMs).GetAwaiter().GetResult();

  /// <summary>
  /// Tries to decompress a stream format file, writing decompressed bytes to the output stream.
  /// </summary>
  /// <param name="filePath">Path to the compressed file.</param>
  /// <param name="output">Stream to write decompressed data to.</param>
  /// <param name="timeoutMs">Timeout per tool invocation.</param>
  /// <returns>True if decompression succeeded.</returns>
  public static async Task<bool> TryDecompressToStreamAsync(
    string filePath,
    Stream output,
    int timeoutMs = ExternalToolRunner.DefaultTimeoutMs
  ) {
    var ext = Path.GetExtension(filePath).ToLowerInvariant();

    // Map extensions to tool + args that write to stdout.
    var (toolName, args) = ext switch {
      ".gz" => ("gzip", $"-d -c \"{filePath}\""),
      ".bz2" => ("bzip2", $"-d -c \"{filePath}\""),
      ".xz" => ("xz", $"-d -c \"{filePath}\""),
      ".zst" => ("zstd", $"-d -c \"{filePath}\""),
      ".lz4" => ("lz4", $"-d -c \"{filePath}\""),
      ".lz" => ("lzip", $"-d -c \"{filePath}\""),
      _ => (string.Empty, string.Empty)
    };

    if (string.IsNullOrEmpty(toolName))
      return false;

    var toolPath = ToolDiscovery.GetToolPath(toolName);
    if (toolPath == null)
      return false;

    var run = await ExternalToolRunner.RunAsync(toolPath, args, timeoutMs: timeoutMs, captureStdoutBytes: true).ConfigureAwait(false);
    if (!run.Success || run.StdoutBytes == null || run.StdoutBytes.Length == 0)
      return false;

    await output.WriteAsync(run.StdoutBytes).ConfigureAwait(false);
    return true;
  }

  private static async Task<FallbackResult> TryWith7zAsync(string filePath, string outputDir, int timeoutMs) {
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip == null)
      return new FallbackResult { Success = false, Error = "7z not found" };

    var run = await ExternalToolRunner.RunAsync(sevenZip, $"x \"{filePath}\" -o\"{outputDir}\" -y", timeoutMs: timeoutMs).ConfigureAwait(false);
    if (!run.Success)
      return new FallbackResult { Success = false, Error = $"7z failed (exit {run.ExitCode}): {run.Stderr}", ToolUsed = "7z" };

    var extracted = CollectExtractedFiles(outputDir);
    return new FallbackResult { Success = true, ToolUsed = "7z", ExtractedFiles = extracted };
  }

  private static async Task<FallbackResult> TryWithToolAsync(
    string toolName,
    string args,
    string filePath,
    string outputDir,
    int timeoutMs,
    bool useStdoutCapture
  ) {
    var toolPath = ToolDiscovery.GetToolPath(toolName);
    if (toolPath == null)
      return new FallbackResult { Success = false, Error = $"{toolName} not found" };

    if (useStdoutCapture) {
      // Tool writes decompressed data to stdout — capture and write to file.
      var run = await ExternalToolRunner.RunAsync(toolPath, args, timeoutMs: timeoutMs, captureStdoutBytes: true).ConfigureAwait(false);
      if (!run.Success || run.StdoutBytes == null || run.StdoutBytes.Length == 0)
        return new FallbackResult { Success = false, Error = $"{toolName} failed: {run.Stderr}", ToolUsed = toolName };

      var outName = Path.GetFileNameWithoutExtension(filePath);
      var outPath = Path.Combine(outputDir, outName);
      await File.WriteAllBytesAsync(outPath, run.StdoutBytes).ConfigureAwait(false);
      return new FallbackResult { Success = true, ToolUsed = toolName, ExtractedFiles = [outPath] };
    }

    // Tool extracts in-place.
    var directRun = await ExternalToolRunner.RunAsync(toolPath, args, timeoutMs: timeoutMs).ConfigureAwait(false);
    if (!directRun.Success)
      return new FallbackResult { Success = false, Error = $"{toolName} failed: {directRun.Stderr}", ToolUsed = toolName };

    return new FallbackResult { Success = true, ToolUsed = toolName, ExtractedFiles = CollectExtractedFiles(outputDir) };
  }

  private static async Task<FallbackResult> TryWithTarAsync(string filePath, string outputDir, int timeoutMs, string compressionFlag = "") {
    var tarPath = ToolDiscovery.GetToolPath("tar");
    if (tarPath == null)
      return new FallbackResult { Success = false, Error = "tar not found" };

    var flags = $"x{compressionFlag}f";
    var run = await ExternalToolRunner.RunAsync(tarPath, $"{flags} \"{filePath}\" -C \"{outputDir}\"", timeoutMs: timeoutMs).ConfigureAwait(false);
    if (!run.Success)
      return new FallbackResult { Success = false, Error = $"tar failed: {run.Stderr}", ToolUsed = "tar" };

    return new FallbackResult { Success = true, ToolUsed = "tar", ExtractedFiles = CollectExtractedFiles(outputDir) };
  }

  private static async Task<FallbackResult> TryWithUnrarAsync(string filePath, string outputDir, int timeoutMs) {
    // Try unrar first, then 7z as fallback for RAR.
    var unrar = ToolDiscovery.GetToolPath("unrar");
    if (unrar != null) {
      var run = await ExternalToolRunner.RunAsync(unrar, $"x -y \"{filePath}\" \"{outputDir}/\"", timeoutMs: timeoutMs).ConfigureAwait(false);
      if (run.Success)
        return new FallbackResult { Success = true, ToolUsed = "unrar", ExtractedFiles = CollectExtractedFiles(outputDir) };
    }

    // Fall back to 7z for RAR.
    return await TryWith7zAsync(filePath, outputDir, timeoutMs).ConfigureAwait(false);
  }

  private static List<string> CollectExtractedFiles(string dir) {
    try {
      return [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories)];
    } catch {
      return [];
    }
  }
}

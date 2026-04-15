#pragma warning disable CS1591

using Compression.Registry;

namespace Compression.Analysis.ExternalTools;

/// <summary>
/// Result of cross-validating our format detection against an external tool.
/// </summary>
public sealed class ValidationResult {
  /// <summary>Whether the external tool agrees with our detection.</summary>
  public required bool Matches { get; init; }

  /// <summary>Our detected format Id.</summary>
  public required string OurFormatId { get; init; }

  /// <summary>External tool's identification result (description or mime type).</summary>
  public string? ExternalResult { get; init; }

  /// <summary>Which external tool was used for validation.</summary>
  public string? ToolUsed { get; init; }

  /// <summary>True if no external tool was available for validation.</summary>
  public bool NoToolAvailable { get; init; }
}

/// <summary>
/// Compares our format detection results with external tools (7z, file command)
/// to flag disagreements and improve confidence.
/// </summary>
public static class CrossValidator {

  // Maps MIME types to our format IDs for comparison.
  private static readonly Dictionary<string, string[]> _mimeToFormatIds = new(StringComparer.OrdinalIgnoreCase) {
    ["application/gzip"] = ["Gzip"],
    ["application/x-gzip"] = ["Gzip"],
    ["application/x-bzip2"] = ["Bzip2"],
    ["application/x-xz"] = ["Xz"],
    ["application/x-lzma"] = ["Lzma"],
    ["application/zstd"] = ["Zstd"],
    ["application/x-zstd"] = ["Zstd"],
    ["application/x-lz4"] = ["Lz4"],
    ["application/zip"] = ["Zip", "Jar", "War", "Ear", "Apk", "Ipa", "Xpi", "Crx", "Epub", "Odt", "Ods", "Odp", "Docx", "Xlsx", "Pptx", "Cbz", "Maff", "Kmz", "Appx", "NuPkg"],
    ["application/x-7z-compressed"] = ["SevenZip"],
    ["application/x-rar-compressed"] = ["Rar"],
    ["application/x-tar"] = ["Tar"],
    ["application/x-cpio"] = ["Cpio"],
    ["application/x-lzh-compressed"] = ["Lzh"],
    ["application/x-arj"] = ["Arj"],
    ["application/vnd.ms-cab-compressed"] = ["Cab"],
    ["application/x-archive"] = ["Ar"],
    ["application/x-iso9660-image"] = ["Iso9660"],
    ["application/x-compress"] = ["Compress"],
    ["application/x-rpm"] = ["Rpm"],
    ["application/x-deb"] = ["Deb"],
    ["application/x-ace-compressed"] = ["Ace"],
    ["application/java-archive"] = ["Jar"],
    ["application/epub+zip"] = ["Epub"],
    ["application/pdf"] = ["Pdf"],
    ["application/x-stuffit"] = ["StuffIt"],
    ["application/x-lzip"] = ["Lzip"],
  };

  // Maps 7z archive type strings to our format IDs.
  private static readonly Dictionary<string, string[]> _7zTypeToFormatIds = new(StringComparer.OrdinalIgnoreCase) {
    ["7z"] = ["SevenZip"],
    ["zip"] = ["Zip", "Jar", "War", "Ear", "Apk", "Ipa", "Xpi", "Crx", "Epub", "Odt", "Ods", "Odp", "Docx", "Xlsx", "Pptx", "Cbz", "Maff", "Kmz", "Appx", "NuPkg"],
    ["gzip"] = ["Gzip"],
    ["bzip2"] = ["Bzip2"],
    ["xz"] = ["Xz"],
    ["tar"] = ["Tar"],
    ["rar"] = ["Rar"],
    ["rar5"] = ["Rar"],
    ["cab"] = ["Cab"],
    ["arj"] = ["Arj"],
    ["cpio"] = ["Cpio"],
    ["lzh"] = ["Lzh"],
    ["iso"] = ["Iso9660"],
    ["wim"] = ["Wim"],
    ["rpm"] = ["Rpm"],
    ["deb"] = ["Deb"],
    ["z"] = ["Compress"],
    ["lzma"] = ["Lzma"],
    ["pe"] = ["Sfx"],
    ["msi"] = ["Msi"],
    ["chm"] = ["Chm"],
    ["dmg"] = ["Dmg"],
    ["swf"] = ["Swf"],
    ["nsis"] = ["Sfx"],
    ["fat"] = ["Fat"],
    ["ntfs"] = ["Ntfs"],
    ["hfs"] = ["Hfs"],
    ["ext"] = ["Ext"],
    ["squashfs"] = ["SquashFs"],
    ["cramfs"] = ["CramFs"],
  };

  /// <summary>
  /// Validates our format detection against external tools.
  /// Tries <c>file --mime</c> first, then <c>7z l</c> as fallback.
  /// </summary>
  /// <param name="filePath">Path to the file to validate.</param>
  /// <param name="ourFormatId">Our detected format Id.</param>
  /// <param name="timeoutMs">Timeout per external tool invocation.</param>
  /// <returns>Validation result describing agreement or disagreement.</returns>
  public static async Task<ValidationResult> ValidateDetectionAsync(
    string filePath,
    string ourFormatId,
    int timeoutMs = ExternalToolRunner.DefaultTimeoutMs
  ) {
    // Try 'file' command first (available on Linux/macOS, sometimes on Windows via Git).
    var fileTool = ToolDiscovery.GetToolPath("file");
    if (fileTool != null) {
      var result = await TryValidateWithFileCommand(fileTool, filePath, ourFormatId, timeoutMs).ConfigureAwait(false);
      if (result != null)
        return result;
    }

    // Try 7z as fallback.
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip != null) {
      var result = await TryValidateWith7z(sevenZip, filePath, ourFormatId, timeoutMs).ConfigureAwait(false);
      if (result != null)
        return result;
    }

    return new ValidationResult {
      Matches = false,
      OurFormatId = ourFormatId,
      NoToolAvailable = true
    };
  }

  /// <summary>
  /// Synchronous convenience overload.
  /// </summary>
  public static ValidationResult ValidateDetection(string filePath, string ourFormatId, int timeoutMs = ExternalToolRunner.DefaultTimeoutMs) =>
    ValidateDetectionAsync(filePath, ourFormatId, timeoutMs).GetAwaiter().GetResult();

  /// <summary>
  /// Validates a batch of files and returns disagreements.
  /// </summary>
  public static async Task<List<ValidationResult>> ValidateBatchAsync(
    IEnumerable<(string FilePath, string FormatId)> files,
    int timeoutMs = ExternalToolRunner.DefaultTimeoutMs
  ) {
    var results = new List<ValidationResult>();

    foreach (var (filePath, formatId) in files) {
      var result = await ValidateDetectionAsync(filePath, formatId, timeoutMs).ConfigureAwait(false);
      results.Add(result);
    }

    return results;
  }

  private static async Task<ValidationResult?> TryValidateWithFileCommand(
    string fileTool,
    string filePath,
    string ourFormatId,
    int timeoutMs
  ) {
    var run = await ExternalToolRunner.RunAsync(fileTool, $"--mime-type -b \"{filePath}\"", timeoutMs: timeoutMs).ConfigureAwait(false);
    if (!run.Success)
      return null;

    var mimeType = ToolOutputParser.ParseFileMimeType(run.Stdout);
    if (mimeType == null)
      return null;

    var matches = false;
    if (_mimeToFormatIds.TryGetValue(mimeType, out var expectedIds))
      matches = expectedIds.Any(id => string.Equals(id, ourFormatId, StringComparison.OrdinalIgnoreCase));
    else if (mimeType is "application/octet-stream" or "text/plain")
      matches = true; // Generic type — can't confirm or deny.

    return new ValidationResult {
      Matches = matches,
      OurFormatId = ourFormatId,
      ExternalResult = mimeType,
      ToolUsed = "file"
    };
  }

  private static async Task<ValidationResult?> TryValidateWith7z(
    string sevenZip,
    string filePath,
    string ourFormatId,
    int timeoutMs
  ) {
    var run = await ExternalToolRunner.RunAsync(sevenZip, $"l -slt \"{filePath}\"", timeoutMs: timeoutMs).ConfigureAwait(false);
    // 7z exits with non-zero for unsupported formats.
    var stdout = run.Stdout;
    if (string.IsNullOrWhiteSpace(stdout))
      return null;

    // Extract the "Type = ..." line from 7z output.
    var archiveType = ExtractSevenZipType(stdout);
    if (archiveType == null)
      return null;

    var matches = false;
    if (_7zTypeToFormatIds.TryGetValue(archiveType, out var expectedIds))
      matches = expectedIds.Any(id => string.Equals(id, ourFormatId, StringComparison.OrdinalIgnoreCase));

    return new ValidationResult {
      Matches = matches,
      OurFormatId = ourFormatId,
      ExternalResult = $"7z type: {archiveType}",
      ToolUsed = "7z"
    };
  }

  private static string? ExtractSevenZipType(string output) {
    foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
      var trimmed = line.Trim();
      if (trimmed.StartsWith("Type = ", StringComparison.OrdinalIgnoreCase))
        return trimmed["Type = ".Length..].Trim();
    }
    return null;
  }
}

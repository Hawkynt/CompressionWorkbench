namespace Compression.Analysis;

/// <summary>
/// Suggests the best archive format for a given set of files based on file types,
/// total size, and target platform.
/// </summary>
public sealed class FormatSuggester {

  /// <summary>Target platform hint.</summary>
  public enum Platform {
    /// <summary>No specific platform.</summary>
    Any,
    /// <summary>Windows target.</summary>
    Windows,
    /// <summary>Linux/Unix target.</summary>
    Linux,
    /// <summary>macOS target.</summary>
    MacOS,
    /// <summary>Cross-platform target.</summary>
    CrossPlatform
  }

  /// <summary>
  /// A ranked format suggestion with rationale.
  /// </summary>
  public sealed class FormatSuggestion {
    /// <summary>Format Id (e.g., "Zip", "SevenZip", "TarGz").</summary>
    public required string FormatId { get; init; }

    /// <summary>Display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Suggested extension.</summary>
    public required string Extension { get; init; }

    /// <summary>Score (0-100).</summary>
    public required int Score { get; init; }

    /// <summary>Human-readable rationale for this suggestion.</summary>
    public required string Rationale { get; init; }
  }

  /// <summary>
  /// Suggests archive formats for the given files and platform.
  /// </summary>
  public List<FormatSuggestion> Suggest(IReadOnlyList<string> filePaths, Platform platform = Platform.Any) {
    var totalSize = 0L;
    var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var hasDirectories = false;

    foreach (var path in filePaths) {
      if (Directory.Exists(path)) {
        hasDirectories = true;
        foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories)) {
          totalSize += new FileInfo(f).Length;
          extensions.Add(Path.GetExtension(f).ToLowerInvariant());
        }
      } else if (File.Exists(path)) {
        totalSize += new FileInfo(path).Length;
        extensions.Add(Path.GetExtension(path).ToLowerInvariant());
      }
    }

    var suggestions = new List<FormatSuggestion>();

    // ZIP: universal, cross-platform, good for mixed content.
    var zipScore = 70;
    var zipRationale = "Universal format, supported everywhere";
    if (platform is Platform.CrossPlatform or Platform.Any) { zipScore += 10; zipRationale += "; best for cross-platform sharing"; }
    if (totalSize < 100 * 1024 * 1024) zipScore += 5; // good for small-medium files
    suggestions.Add(new FormatSuggestion { FormatId = "Zip", DisplayName = "ZIP", Extension = ".zip", Score = zipScore, Rationale = zipRationale });

    // 7z: best compression, good for large files.
    var szScore = 65;
    var szRationale = "Best compression ratio with LZMA2";
    if (totalSize > 10 * 1024 * 1024) { szScore += 15; szRationale += "; excellent for large files"; }
    if (platform == Platform.Windows) { szScore += 5; szRationale += "; native Windows support via 7-Zip"; }
    suggestions.Add(new FormatSuggestion { FormatId = "SevenZip", DisplayName = "7z", Extension = ".7z", Score = szScore, Rationale = szRationale });

    // tar.gz: standard on Linux/Unix.
    var tgzScore = 55;
    var tgzRationale = "Standard Unix archive format";
    if (platform is Platform.Linux) { tgzScore += 25; tgzRationale += "; native Linux format"; }
    if (hasDirectories) { tgzScore += 5; tgzRationale += "; preserves Unix permissions"; }
    suggestions.Add(new FormatSuggestion { FormatId = "TarGz", DisplayName = "tar.gz", Extension = ".tar.gz", Score = tgzScore, Rationale = tgzRationale });

    // tar.xz: best compression for tar archives.
    var txzScore = 50;
    var txzRationale = "Better compression than gzip";
    if (platform is Platform.Linux) { txzScore += 20; txzRationale += "; common on Linux"; }
    if (totalSize > 50 * 1024 * 1024) { txzScore += 10; txzRationale += "; efficient for large datasets"; }
    suggestions.Add(new FormatSuggestion { FormatId = "TarXz", DisplayName = "tar.xz", Extension = ".tar.xz", Score = txzScore, Rationale = txzRationale });

    // tar.zst: fast compression/decompression.
    var tzstScore = 50;
    var tzstRationale = "Very fast with good compression";
    if (totalSize > 100 * 1024 * 1024) { tzstScore += 10; tzstRationale += "; ideal for large files where speed matters"; }
    suggestions.Add(new FormatSuggestion { FormatId = "TarZstd", DisplayName = "tar.zst", Extension = ".tar.zst", Score = tzstScore, Rationale = tzstRationale });

    // Single file, stream compression.
    if (filePaths.Count == 1 && !hasDirectories) {
      suggestions.Add(new FormatSuggestion { FormatId = "Gzip", DisplayName = "gzip", Extension = ".gz", Score = 60, Rationale = "Simple single-file compression; universal support" });
      suggestions.Add(new FormatSuggestion { FormatId = "Zstd", DisplayName = "Zstandard", Extension = ".zst", Score = 55, Rationale = "Fast single-file compression with excellent ratio" });
    }

    // Sort by score descending.
    suggestions.Sort((a, b) => b.Score.CompareTo(a.Score));
    return suggestions;
  }
}

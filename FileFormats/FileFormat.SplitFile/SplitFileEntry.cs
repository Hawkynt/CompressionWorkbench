#pragma warning disable CS1591
namespace FileFormat.SplitFile;

/// <summary>
/// Represents the single logical file assembled from split parts.
/// </summary>
public sealed class SplitFileEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets or sets the part count.
  /// </summary>
public int PartCount { get; init; }
}

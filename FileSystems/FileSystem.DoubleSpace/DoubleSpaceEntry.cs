#pragma warning disable CS1591
namespace FileSystem.DoubleSpace;

/// <summary>
/// Represents a double space entry.
/// </summary>
public sealed class DoubleSpaceEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
  internal int StartCluster { get; init; }
  internal int SectorCount { get; init; }
}

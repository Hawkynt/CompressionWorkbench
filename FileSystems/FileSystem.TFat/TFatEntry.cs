#pragma warning disable CS1591
namespace FileSystem.TFat;

/// <summary>
/// Represents a t fat entry.
/// </summary>
public sealed class TFatEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public required string Name { get; init; }
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
  /// <summary>
  /// Gets or sets the start cluster.
  /// </summary>
public int StartCluster { get; init; }
  /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
public DateTime? LastModified { get; init; }
}

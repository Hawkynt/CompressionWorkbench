#pragma warning disable CS1591
namespace FileSystem.Fat;

/// <summary>
/// Represents a fat entry.
/// </summary>
public sealed class FatEntry {
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
  /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
  public DateTime? LastModified { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.BeeGfs;

/// <summary>
/// Represents a bee gfs entry.
/// </summary>
public sealed class BeeGfsEntry {
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
    /// <summary>
  /// Gets or sets the offset.
  /// </summary>
public long Offset { get; init; }
    /// <summary>
  /// Gets or sets the data.
  /// </summary>
public byte[] Data { get; init; } = [];
}

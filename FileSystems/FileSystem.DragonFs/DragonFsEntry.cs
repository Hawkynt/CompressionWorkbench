#pragma warning disable CS1591
namespace FileSystem.DragonFs;

/// <summary>
/// Represents a dragon fs entry.
/// </summary>
public sealed class DragonFsEntry {
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
  /// Gets or sets the data offset.
  /// </summary>
public int DataOffset { get; init; }
}

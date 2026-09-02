#pragma warning disable CS1591
namespace FileSystem.MinixV1;

/// <summary>
/// Represents a minix v 1 entry.
/// </summary>
public sealed class MinixV1Entry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public int Size { get; init; }
    /// <summary>
  /// Gets or sets the inode number.
  /// </summary>
public int InodeNumber { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
}

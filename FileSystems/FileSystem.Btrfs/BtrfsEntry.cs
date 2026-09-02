#pragma warning disable CS1591
namespace FileSystem.Btrfs;

/// <summary>
/// Represents a btrfs entry.
/// </summary>
public sealed class BtrfsEntry {
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
  /// Gets or sets the last modified.
  /// </summary>
  public DateTime? LastModified { get; init; }
  internal long Inode { get; init; }
}

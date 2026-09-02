#pragma warning disable CS1591
namespace FileSystem.Xfs;

/// <summary>
/// Represents a xfs entry.
/// </summary>
public sealed class XfsEntry {
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
  internal long InodeNumber { get; init; }
}

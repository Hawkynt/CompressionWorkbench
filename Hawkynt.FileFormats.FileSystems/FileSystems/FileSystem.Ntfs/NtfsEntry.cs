#pragma warning disable CS1591
namespace FileSystem.Ntfs;

/// <summary>
/// Represents a ntfs entry.
/// </summary>
public sealed class NtfsEntry {
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
  /// Gets a value indicating whether is symlink.
  /// </summary>
  public bool IsSymlink { get; init; }
  /// <summary>
  /// Gets or sets the link target.
  /// </summary>
  public string? LinkTarget { get; init; }
  /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
  public DateTime? LastModified { get; init; }
  internal uint MftRecord { get; init; }
}

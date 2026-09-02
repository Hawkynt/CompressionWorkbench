#pragma warning disable CS1591
namespace FileSystem.Apfs;

/// <summary>
/// Represents an apfs entry.
/// </summary>
public sealed class ApfsEntry {
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
  internal ulong ObjectId { get; init; }
  /// <summary>First physical block of the file's data extent (0 = no extent).</summary>
  /// <summary>First block of the file's single extent, or 0 when it has none.</summary>
  public ulong FirstBlock { get; init; }
  /// <summary>Length in bytes of the file extent (0 = empty/none).</summary>
  internal long ExtentLength { get; init; }
}

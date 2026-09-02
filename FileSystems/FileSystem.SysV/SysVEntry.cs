#pragma warning disable CS1591
namespace FileSystem.SysV;

/// <summary>Directory entry from a SysV FS image.</summary>
public sealed class SysVEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  /// <summary>
  /// Gets or sets the inode number.
  /// </summary>
public int InodeNumber { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.Qnx6;

/// <summary>Directory entry from a QNX6 filesystem.</summary>
public sealed class Qnx6Entry {
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
public uint InodeNumber { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.Mfs;

/// <summary>
/// Represents a mfs entry.
/// </summary>
public sealed class MfsEntry {
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
public bool IsDirectory => false;
  internal ushort FirstBlock { get; init; }
}

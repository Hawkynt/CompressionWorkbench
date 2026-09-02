#pragma warning disable CS1591
namespace FileFormat.Msi;

/// <summary>
/// Represents an entry (stream or storage) in an MSI/OLE Compound File.
/// </summary>
public sealed class MsiEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the full path.
  /// </summary>
public string FullPath { get; init; } = "";
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  internal int DirectoryIndex { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.Vdfs;

/// <summary>
/// Represents a vdfs entry.
/// </summary>
public sealed class VdfsEntry {
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
  internal long DataOffset { get; init; }
}

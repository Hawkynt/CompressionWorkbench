#pragma warning disable CS1591
namespace FileFormat.Vmdk;

/// <summary>
/// Represents a vmdk entry.
/// </summary>
public sealed class VmdkEntry {
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
}

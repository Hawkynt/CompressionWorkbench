#pragma warning disable CS1591
namespace FileFormat.Vhd;

/// <summary>
/// Represents a vhd entry.
/// </summary>
public sealed class VhdEntry {
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

#pragma warning disable CS1591
namespace FileFormat.Rgss;

/// <summary>
/// Represents a rgss entry.
/// </summary>
public sealed class RgssEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the offset.
  /// </summary>
  public long Offset { get; init; }
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets or sets the file key.
  /// </summary>
  public uint FileKey { get; init; } // v3 only; otherwise 0
}

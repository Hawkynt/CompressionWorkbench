#pragma warning disable CS1591
namespace FileFormat.Ampk;

/// <summary>
/// Represents an ampk entry.
/// </summary>
public sealed class AmpkEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets or sets the compressed size.
  /// </summary>
  public long CompressedSize { get; init; }
  internal int Offset { get; init; }
}

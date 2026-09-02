#pragma warning disable CS1591
namespace FileFormat.Wrapster;

/// <summary>
/// Represents a wrapster entry.
/// </summary>
public sealed class WrapsterEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  internal int Offset { get; init; }
  internal int DataLength { get; init; }
}

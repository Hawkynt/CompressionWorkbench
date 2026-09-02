#pragma warning disable CS1591
namespace FileFormat.Umx;

/// <summary>
/// Represents an umx entry.
/// </summary>
public sealed class UmxEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  internal int Offset { get; init; }
}

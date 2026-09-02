#pragma warning disable CS1591
namespace FileFormat.IffCdaf;

/// <summary>
/// Represents an iff cdaf entry.
/// </summary>
public sealed class IffCdafEntry {
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

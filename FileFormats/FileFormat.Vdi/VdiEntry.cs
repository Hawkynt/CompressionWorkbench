#pragma warning disable CS1591
namespace FileFormat.Vdi;

/// <summary>
/// Represents a vdi entry.
/// </summary>
public sealed class VdiEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
}

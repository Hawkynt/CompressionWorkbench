#pragma warning disable CS1591
namespace FileFormat.Qcow2;

/// <summary>
/// Represents a qcow 2 entry.
/// </summary>
public sealed class Qcow2Entry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets or sets the offset.
  /// </summary>
public long Offset { get; init; }
}

#pragma warning disable CS1591
namespace FileFormat.Dmg;

/// <summary>
/// Represents a dmg entry.
/// </summary>
public sealed class DmgEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
}

#pragma warning disable CS1591
namespace FileFormat.Tnef;

/// <summary>
/// Represents a tnef entry.
/// </summary>
public sealed class TnefEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
  internal byte[]? Data { get; init; }
}

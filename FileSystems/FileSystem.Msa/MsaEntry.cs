#pragma warning disable CS1591
namespace FileSystem.Msa;

/// <summary>
/// Represents a msa entry.
/// </summary>
public sealed class MsaEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
}

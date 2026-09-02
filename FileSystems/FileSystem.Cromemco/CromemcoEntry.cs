#pragma warning disable CS1591
namespace FileSystem.Cromemco;

/// <summary>
/// Represents a cromemco entry.
/// </summary>
public sealed class CromemcoEntry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
    /// <summary>
  /// Gets or sets the start block.
  /// </summary>
public int StartBlock { get; init; }
    /// <summary>
  /// Gets or sets the block count.
  /// </summary>
public int BlockCount { get; init; }
}

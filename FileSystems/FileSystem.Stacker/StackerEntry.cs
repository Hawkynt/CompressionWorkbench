#pragma warning disable CS1591
namespace FileSystem.Stacker;

/// <summary>
/// Represents a stacker entry.
/// </summary>
public sealed class StackerEntry {
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

  /// <summary>Byte offset into the host file for opaque/fallback extraction.</summary>
  public int DataOffset { get; init; }

  /// <summary>First inner-FAT cluster of the file (0 when not FAT-resolved).</summary>
  public int FirstCluster { get; init; }
}

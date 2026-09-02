#pragma warning disable CS1591
namespace FileSystem.Bbc;

/// <summary>
/// Entry in a BBC Micro Acorn DFS catalog.
/// </summary>
public sealed class BbcEntry {
  /// <summary>"$.FILENAME" or "X.FILENAME" form where X is the DFS directory prefix.</summary>
  public string FullName { get; init; } = "";
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the directory.
  /// </summary>
public char Directory { get; init; } = '$';
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory => false;
    /// <summary>
  /// Gets a value indicating whether is locked.
  /// </summary>
public bool IsLocked { get; init; }
    /// <summary>
  /// Gets or sets the load address.
  /// </summary>
public uint LoadAddress { get; init; }
    /// <summary>
  /// Gets or sets the exec address.
  /// </summary>
public uint ExecAddress { get; init; }
  internal int StartSector { get; init; }
  /// <summary>Side of the disk (0 or 1). Relevant for .dsd double-sided images.</summary>
  internal int Side { get; init; }
}

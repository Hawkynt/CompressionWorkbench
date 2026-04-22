#pragma warning disable CS1591
namespace FileSystem.Bbc;

/// <summary>
/// Entry in a BBC Micro Acorn DFS catalog.
/// </summary>
public sealed class BbcEntry {
  /// <summary>"$.FILENAME" or "X.FILENAME" form where X is the DFS directory prefix.</summary>
  public string FullName { get; init; } = "";
  public string Name { get; init; } = "";
  public char Directory { get; init; } = '$';
  public long Size { get; init; }
  public bool IsDirectory => false;
  public bool IsLocked { get; init; }
  public uint LoadAddress { get; init; }
  public uint ExecAddress { get; init; }
  internal int StartSector { get; init; }
  /// <summary>Side of the disk (0 or 1). Relevant for .dsd double-sided images.</summary>
  internal int Side { get; init; }
}

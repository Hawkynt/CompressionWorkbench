#pragma warning disable CS1591
namespace FileSystem.SysV;

/// <summary>Directory entry from a SysV FS image.</summary>
public sealed class SysVEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public int InodeNumber { get; init; }
  public bool IsDirectory { get; init; }
}

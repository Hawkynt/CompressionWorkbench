#pragma warning disable CS1591
namespace FileSystem.Coherent;

/// <summary>Directory entry from a Coherent FS image.</summary>
public sealed class CoherentEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public int InodeNumber { get; init; }
  public bool IsDirectory { get; init; }
}

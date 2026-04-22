#pragma warning disable CS1591
namespace FileSystem.DoubleSpace;

public sealed class DoubleSpaceEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  internal int StartCluster { get; init; }
  internal int SectorCount { get; init; }
}

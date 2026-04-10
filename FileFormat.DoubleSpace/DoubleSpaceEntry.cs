#pragma warning disable CS1591
namespace FileFormat.DoubleSpace;

public sealed class DoubleSpaceEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  internal int StartSector { get; init; }
  internal int SectorCount { get; init; }
}

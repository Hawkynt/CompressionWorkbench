#pragma warning disable CS1591
namespace FileSystem.Trsdos;

public sealed class TrsdosEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int FirstSector { get; init; }
  public int SectorCount { get; init; }
  public byte Attributes { get; init; }
}

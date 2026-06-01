#pragma warning disable CS1591
namespace FileSystem.Ti99;

public sealed class Ti99Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int FirstSector { get; init; }
  public int SectorCount { get; init; }
  public byte FileFlags { get; init; }
  public byte RecordsPerSector { get; init; }
}

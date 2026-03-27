#pragma warning disable CS1591
namespace FileFormat.TrDos;

public sealed class TrDosEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public int DataSize { get; init; }
  public int StartSector { get; init; }
  public int StartTrack { get; init; }
  public int LengthSectors { get; init; }
  public char FileType { get; init; }
}

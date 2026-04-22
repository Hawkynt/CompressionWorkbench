#pragma warning disable CS1591
namespace FileSystem.D71;

public sealed class D71Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory => false;
  public byte FileType { get; init; } // 0x82=PRG, 0x81=SEQ, 0x83=USR, 0x84=REL
  internal int StartTrack { get; init; }
  internal int StartSector { get; init; }
}

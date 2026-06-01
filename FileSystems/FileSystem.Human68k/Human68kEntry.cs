#pragma warning disable CS1591
namespace FileSystem.Human68k;

public sealed class Human68kEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int FirstCluster { get; init; }
  public byte Attributes { get; init; }
}

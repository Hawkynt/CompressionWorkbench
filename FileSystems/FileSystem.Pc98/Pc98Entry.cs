#pragma warning disable CS1591
namespace FileSystem.Pc98;

public sealed class Pc98Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int FirstCluster { get; init; }
  public byte Attributes { get; init; }
}

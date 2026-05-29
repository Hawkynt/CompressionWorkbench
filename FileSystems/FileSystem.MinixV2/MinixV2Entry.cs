#pragma warning disable CS1591
namespace FileSystem.MinixV2;

public sealed class MinixV2Entry {
  public string Name { get; init; } = "";
  public int Size { get; init; }
  public int InodeNumber { get; init; }
  public bool IsDirectory { get; init; }
}

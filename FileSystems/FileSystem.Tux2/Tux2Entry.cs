#pragma warning disable CS1591
namespace FileSystem.Tux2;

public sealed class Tux2Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public byte[] Data { get; init; } = [];
}

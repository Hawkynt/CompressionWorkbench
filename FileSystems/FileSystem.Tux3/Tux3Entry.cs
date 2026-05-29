#pragma warning disable CS1591
namespace FileSystem.Tux3;

public sealed class Tux3Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public byte[] Data { get; init; } = [];
}

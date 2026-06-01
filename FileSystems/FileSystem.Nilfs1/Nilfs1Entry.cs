#pragma warning disable CS1591
namespace FileSystem.Nilfs1;

public sealed class Nilfs1Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public byte[] Data { get; init; } = [];
}

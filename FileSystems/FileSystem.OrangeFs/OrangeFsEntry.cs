#pragma warning disable CS1591
namespace FileSystem.OrangeFs;

public sealed class OrangeFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public byte[] Data { get; init; } = [];
}

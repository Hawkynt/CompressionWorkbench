#pragma warning disable CS1591
namespace FileSystem.DragonFs;

public sealed class DragonFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int DataOffset { get; init; }
}

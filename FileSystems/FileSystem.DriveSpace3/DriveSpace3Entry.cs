#pragma warning disable CS1591
namespace FileSystem.DriveSpace3;

public sealed class DriveSpace3Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int DataOffset { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.Stacker;

public sealed class StackerEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int DataOffset { get; init; }
}

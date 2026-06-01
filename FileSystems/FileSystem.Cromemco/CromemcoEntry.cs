#pragma warning disable CS1591
namespace FileSystem.Cromemco;

public sealed class CromemcoEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int StartBlock { get; init; }
  public int BlockCount { get; init; }
}

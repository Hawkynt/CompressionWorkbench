#pragma warning disable CS1591
namespace FileFormat.MinixFs;

public sealed class MinixFsEntry {
  public string Name { get; init; } = "";
  public int Size { get; init; }
  public int InodeNumber { get; init; }
  public bool IsDirectory { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.ApplePascal;

public sealed class ApplePascalEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int StartBlock { get; init; }
  public int EndBlock { get; init; }
  public int FileKind { get; init; }
  public int BytesInLastBlock { get; init; }
}

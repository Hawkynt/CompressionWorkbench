#pragma warning disable CS1591
namespace FileSystem.F2fs;

public sealed class F2fsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal uint NodeId { get; init; }
}

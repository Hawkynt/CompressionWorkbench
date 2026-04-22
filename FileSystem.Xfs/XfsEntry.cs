#pragma warning disable CS1591
namespace FileSystem.Xfs;

public sealed class XfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal long InodeNumber { get; init; }
}

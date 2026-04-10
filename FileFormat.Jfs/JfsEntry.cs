#pragma warning disable CS1591
namespace FileFormat.Jfs;

public sealed class JfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal int InodeNumber { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.Ufs;

public sealed class UfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal int Inode { get; init; }
}

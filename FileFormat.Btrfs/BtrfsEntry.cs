#pragma warning disable CS1591
namespace FileFormat.Btrfs;

public sealed class BtrfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal long Inode { get; init; }
}

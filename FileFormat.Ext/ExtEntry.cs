#pragma warning disable CS1591
namespace FileFormat.Ext;

public sealed class ExtEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal uint Inode { get; init; }
}

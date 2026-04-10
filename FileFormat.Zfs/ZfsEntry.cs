#pragma warning disable CS1591
namespace FileFormat.Zfs;

public sealed class ZfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal ulong ObjectId { get; init; }
}

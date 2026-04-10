#pragma warning disable CS1591
namespace FileFormat.Apfs;

public sealed class ApfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal ulong ObjectId { get; init; }
}

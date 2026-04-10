#pragma warning disable CS1591
namespace FileFormat.ReiserFs;

public sealed class ReiserFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal uint DirId { get; init; }
  internal uint ObjectId { get; init; }
}

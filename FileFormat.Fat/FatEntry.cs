#pragma warning disable CS1591
namespace FileFormat.Fat;

public sealed class FatEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  internal int StartCluster { get; init; }
  public DateTime? LastModified { get; init; }
}

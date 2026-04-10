#pragma warning disable CS1591
namespace FileFormat.ExFat;

public sealed class ExFatEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal uint FirstCluster { get; init; }
}

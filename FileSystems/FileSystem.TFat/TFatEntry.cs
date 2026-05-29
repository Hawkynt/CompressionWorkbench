#pragma warning disable CS1591
namespace FileSystem.TFat;

public sealed class TFatEntry {
  public required string Name { get; init; }
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public int StartCluster { get; init; }
  public DateTime? LastModified { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.Ntfs;

public sealed class NtfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal uint MftRecord { get; init; }
}

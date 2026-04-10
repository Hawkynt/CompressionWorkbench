#pragma warning disable CS1591
namespace FileFormat.Vmdk;

public sealed class VmdkEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory => false;
}

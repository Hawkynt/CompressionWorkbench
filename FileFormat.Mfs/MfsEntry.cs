#pragma warning disable CS1591
namespace FileFormat.Mfs;

public sealed class MfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory => false;
  internal ushort FirstBlock { get; init; }
}

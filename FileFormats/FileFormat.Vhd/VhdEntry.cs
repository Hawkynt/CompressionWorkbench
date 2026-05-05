#pragma warning disable CS1591
namespace FileFormat.Vhd;

public sealed class VhdEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory => false;
}

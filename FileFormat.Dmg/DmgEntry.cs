#pragma warning disable CS1591
namespace FileFormat.Dmg;

public sealed class DmgEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
}

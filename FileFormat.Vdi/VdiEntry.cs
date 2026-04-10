#pragma warning disable CS1591
namespace FileFormat.Vdi;

public sealed class VdiEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
}

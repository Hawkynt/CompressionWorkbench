#pragma warning disable CS1591
namespace FileFormat.Umx;

public sealed class UmxEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  internal int Offset { get; init; }
}

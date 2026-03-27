#pragma warning disable CS1591
namespace FileFormat.IffCdaf;

public sealed class IffCdafEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  internal int Offset { get; init; }
}

#pragma warning disable CS1591
namespace FileFormat.Wrapster;

public sealed class WrapsterEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  internal int Offset { get; init; }
  internal int DataLength { get; init; }
}

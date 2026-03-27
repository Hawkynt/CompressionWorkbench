#pragma warning disable CS1591
namespace FileFormat.LhF;

public sealed class LhFEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public long CompressedSize { get; init; }
  internal int TrackNumber { get; init; }
  internal int Offset { get; init; }
}

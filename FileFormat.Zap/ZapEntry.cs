#pragma warning disable CS1591
namespace FileFormat.Zap;

public sealed class ZapEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public long CompressedSize { get; init; }
  internal int TrackNumber { get; init; }
  internal int Offset { get; init; }
  internal bool IsCompressed { get; init; }
}

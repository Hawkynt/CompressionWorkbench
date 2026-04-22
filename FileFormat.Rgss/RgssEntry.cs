#pragma warning disable CS1591
namespace FileFormat.Rgss;

public sealed class RgssEntry {
  public string Name { get; init; } = "";
  public long Offset { get; init; }
  public long Size { get; init; }
  public uint FileKey { get; init; } // v3 only; otherwise 0
}

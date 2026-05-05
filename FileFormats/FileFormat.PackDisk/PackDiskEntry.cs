#pragma warning disable CS1591
namespace FileFormat.PackDisk;

public sealed class PackDiskEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public long CompressedSize { get; init; }
  internal int Offset { get; init; }
}

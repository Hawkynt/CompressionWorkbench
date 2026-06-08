#pragma warning disable CS1591
namespace FileFormat.Aomei;

public sealed class AomeiEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];
}

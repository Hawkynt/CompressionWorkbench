#pragma warning disable CS1591
namespace FileFormat.Qcow2;

public sealed class Qcow2Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public long Offset { get; init; }
}

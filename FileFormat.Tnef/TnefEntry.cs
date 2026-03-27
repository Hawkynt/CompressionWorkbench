#pragma warning disable CS1591
namespace FileFormat.Tnef;

public sealed class TnefEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  internal byte[]? Data { get; init; }
}

#pragma warning disable CS1591
namespace FileFormat.Msa;

public sealed class MsaEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
}

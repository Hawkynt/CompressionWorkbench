#pragma warning disable CS1591
namespace FileFormat.Vdfs;

public sealed class VdfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  internal long DataOffset { get; init; }
}

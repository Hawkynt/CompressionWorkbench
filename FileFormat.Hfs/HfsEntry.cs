#pragma warning disable CS1591
namespace FileFormat.Hfs;

public sealed class HfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public DateTime? LastModified { get; init; }
  internal ushort StartBlock { get; init; }
  internal ushort BlockCount { get; init; }
}

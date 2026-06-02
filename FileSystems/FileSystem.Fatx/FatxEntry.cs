#pragma warning disable CS1591
namespace FileSystem.Fatx;

/// <summary>Single directory record from an Xbox FATX volume.</summary>
public sealed class FatxEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public uint FirstCluster { get; init; }
  public bool IsDirectory { get; init; }
}

#pragma warning disable CS1591
namespace FileSystem.Ods1;

/// <summary>Directory entry from an ODS-1 (Files-11 Level 1) volume.</summary>
public sealed class Ods1Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public uint StartLbn { get; init; }
  public uint BlockCount { get; init; }
  public bool IsDirectory { get; init; }
}

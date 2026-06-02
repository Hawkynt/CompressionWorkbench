#pragma warning disable CS1591
namespace FileSystem.AmigaPfs;

/// <summary>Directory entry from an AmigaPFS volume.</summary>
public sealed class AmigaPfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public uint AnodeNumber { get; init; }
  public bool IsDirectory { get; init; }
}

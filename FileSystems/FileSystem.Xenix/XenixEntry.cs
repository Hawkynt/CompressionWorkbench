#pragma warning disable CS1591
namespace FileSystem.Xenix;

/// <summary>Directory entry from a Microsoft/SCO Xenix filesystem.</summary>
public sealed class XenixEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public int InodeNumber { get; init; }
  public bool IsDirectory { get; init; }
}

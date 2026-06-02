#pragma warning disable CS1591
namespace FileSystem.Qnx6;

/// <summary>Directory entry from a QNX6 filesystem.</summary>
public sealed class Qnx6Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public uint InodeNumber { get; init; }
  public bool IsDirectory { get; init; }
}

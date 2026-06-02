#pragma warning disable CS1591
namespace FileSystem.Qnx4;

/// <summary>Directory entry from a QNX4 filesystem.</summary>
public sealed class Qnx4Entry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public uint FirstExtentBlock { get; init; }
  public uint ExtentBlockCount { get; init; }
  public bool IsDirectory { get; init; }
}

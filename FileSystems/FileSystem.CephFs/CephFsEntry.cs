#pragma warning disable CS1591
namespace FileSystem.CephFs;

public sealed class CephFsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public long Offset { get; init; }
  public byte[] Data { get; init; } = [];
}

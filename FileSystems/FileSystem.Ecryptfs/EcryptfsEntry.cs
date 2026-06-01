#pragma warning disable CS1591
namespace FileSystem.Ecryptfs;

public sealed class EcryptfsEntry {
  public string Name { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  public byte[] Data { get; init; } = [];
}

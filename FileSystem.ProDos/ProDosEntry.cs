#pragma warning disable CS1591
namespace FileSystem.ProDos;

/// <summary>
/// Directory entry in a ProDOS volume. Full path includes parent subdirectory names
/// separated by '/'.
/// </summary>
public sealed class ProDosEntry {
  public string Name { get; init; } = "";
  public string FullPath { get; init; } = "";
  public long Size { get; init; }
  public bool IsDirectory { get; init; }
  /// <summary>ProDOS storage type nibble: 1=seedling, 2=sapling, 3=tree, 0xD=subdir.</summary>
  public byte StorageType { get; init; }
  public byte FileType { get; init; }
  internal int KeyPointer { get; init; }
  internal int BlocksUsed { get; init; }
}

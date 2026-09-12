#pragma warning disable CS1591
namespace FileSystem.ProDos;

/// <summary>
/// Directory entry in a ProDOS volume. Full path includes parent subdirectory names
/// separated by '/'.
/// </summary>
public sealed class ProDosEntry {
  /// <summary>
  /// Gets or sets the name.
  /// </summary>
  public string Name { get; init; } = "";
  /// <summary>
  /// Gets or sets the full path.
  /// </summary>
  public string FullPath { get; init; } = "";
  /// <summary>
  /// Gets or sets the size.
  /// </summary>
  public long Size { get; init; }
  /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
  public bool IsDirectory { get; init; }
  /// <summary>ProDOS storage type nibble: 1=seedling, 2=sapling, 3=tree, 0xD=subdir.</summary>
  public byte StorageType { get; init; }
  /// <summary>
  /// Gets or sets the file type.
  /// </summary>
  public byte FileType { get; init; }
  internal int KeyPointer { get; init; }
  internal int BlocksUsed { get; init; }
}

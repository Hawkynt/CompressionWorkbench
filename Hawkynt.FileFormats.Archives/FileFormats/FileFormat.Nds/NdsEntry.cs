namespace FileFormat.Nds;

/// <summary>
/// Represents a single file or directory entry within a Nintendo DS ROM NitroFS file system.
/// </summary>
public sealed class NdsEntry {
  /// <summary>Gets the entry name (file or directory name without path).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the full path within the NitroFS (e.g., "data/sprites/enemy.bin").</summary>
  public string FullPath { get; init; } = "";

  /// <summary>Gets whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Gets the file ID within the FAT (only meaningful for file entries).</summary>
  public int FileId { get; init; }

  /// <summary>Gets the absolute offset of the file data within the ROM image.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the size of the file data in bytes.</summary>
  public long Size { get; init; }
}

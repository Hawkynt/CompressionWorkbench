#pragma warning disable CS1591
namespace FileSystem.RomFs;

/// <summary>Represents a single entry (file or directory) in a ROMFS image.</summary>
public sealed class RomFsEntry {
  /// <summary>Full path within the filesystem (e.g. "etc/config.txt").</summary>
  public string Name { get; init; } = "";
  /// <summary>Size of the file data in bytes (0 for directories).</summary>
  public int Size { get; init; }
  /// <summary>Absolute byte offset of this entry's data in the image stream.</summary>
  public long DataOffset { get; init; }
  /// <summary>True when this entry represents a directory.</summary>
  public bool IsDirectory { get; init; }
}

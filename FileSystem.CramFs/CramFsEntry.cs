namespace FileSystem.CramFs;

/// <summary>
/// Represents a single inode entry discovered while walking a CramFS image.
/// </summary>
public sealed class CramFsEntry {
  /// <summary>The filename component (no path separator).</summary>
  public string Name { get; init; } = "";

  /// <summary>Absolute path within the filesystem, using '/' as separator.</summary>
  public string FullPath { get; init; } = "";

  /// <summary>Uncompressed file size in bytes (0 for directories).</summary>
  public int Size { get; init; }

  /// <summary>Unix mode bits including file type and permissions.</summary>
  public ushort Mode { get; init; }

  /// <summary>Owner UID (16-bit cramfs field).</summary>
  public ushort Uid { get; init; }

  /// <summary>Group GID (8-bit cramfs field).</summary>
  public byte Gid { get; init; }

  /// <summary>Byte offset inside the image where the block-pointer table starts (= inode.offset * 4).</summary>
  internal int DataOffset { get; init; }

  /// <summary>Returns true when this entry represents a directory.</summary>
  public bool IsDirectory => (this.Mode & CramFsConstants.S_IFMT) == CramFsConstants.S_IFDIR;

  /// <summary>Returns true when this entry represents a regular file.</summary>
  public bool IsRegularFile => (this.Mode & CramFsConstants.S_IFMT) == CramFsConstants.S_IFREG;

  /// <summary>Returns true when this entry represents a symbolic link.</summary>
  public bool IsSymlink => (this.Mode & CramFsConstants.S_IFMT) == CramFsConstants.S_IFLNK;
}

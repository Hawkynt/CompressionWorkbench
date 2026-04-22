namespace FileSystem.SquashFs;

/// <summary>
/// Represents a single entry (file, directory, or symlink) inside a SquashFS image.
/// </summary>
public sealed class SquashFsEntry {
  /// <summary>The base name of the entry (no path separators).</summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>The full path within the archive, using forward slashes.</summary>
  public string FullPath { get; init; } = string.Empty;

  /// <summary>Uncompressed size in bytes. 0 for directories.</summary>
  public long Size { get; init; }

  /// <summary>True if this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>True if this entry is a symbolic link.</summary>
  public bool IsSymlink { get; init; }

  /// <summary>The symlink target path, or null if not a symlink.</summary>
  public string? SymlinkTarget { get; init; }

  /// <summary>Last modification time.</summary>
  public DateTime ModifiedTime { get; init; }

  /// <summary>Unix permissions (low 12 bits of inode mode field).</summary>
  public int Permissions { get; init; }

  /// <summary>User ID.</summary>
  public uint Uid { get; init; }

  /// <summary>Group ID.</summary>
  public uint Gid { get; init; }

  // Internal fields used by the reader for extraction — not part of the public API.
  internal uint BlocksStart { get; init; }
  internal uint Fragment { get; init; }
  internal uint FragmentOffset { get; init; }
  internal uint FileSize { get; init; }
  internal uint[] BlockSizes { get; init; } = [];
}

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

  /// <summary>
  /// Where this file's data blocks actually sit, and how many bytes of them
  /// there are: the start the inode records, and the compressed sizes summed.
  /// Zero length when the file has no blocks of its own — an empty file, or one
  /// small enough to live entirely in a shared fragment.
  /// </summary>
  internal (long Offset, long Length) DataExtent {
    get {
      if (this.IsDirectory || this.BlockSizes.Length == 0) return (0, 0);
      var length = 0L;
      foreach (var size in this.BlockSizes)
        length += size & ~SquashFsConstants.BlockUncompressedFlag;
      return length > 0 ? (this.BlocksStart, length) : (0, 0);
    }
  }
}

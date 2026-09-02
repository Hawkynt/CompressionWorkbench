#pragma warning disable CS1591
namespace FileSystem.Gfs2;

/// <summary>
/// One entry in a GFS2 image. Read-only: we surface the superblock,
/// root/master directory inode pointers, and (optionally) any directory
/// entries we manage to walk from the root inode's leaf blocks.
/// </summary>
public sealed class Gfs2Entry {
    /// <summary>
  /// Gets or sets the name.
  /// </summary>
public string Name { get; init; } = "";
    /// <summary>
  /// Gets or sets the size.
  /// </summary>
public long Size { get; init; }
    /// <summary>
  /// Gets a value indicating whether is directory.
  /// </summary>
public bool IsDirectory { get; init; }
    /// <summary>
  /// Gets or sets the last modified.
  /// </summary>
public DateTime? LastModified { get; init; }

  /// <summary>Block address of the dinode (gfs2_dinode) that backs this entry.</summary>
  internal ulong InodeBlock { get; init; }

  /// <summary>Inode formal number (di_num.no_formal_ino).</summary>
  internal ulong FormalIno { get; init; }
}

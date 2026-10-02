#pragma warning disable CS1591
namespace FileSystem.HfsPlus;

/// <summary>
/// Represents a single file or directory entry found within an HFS+ volume image.
/// </summary>
public sealed class HfsPlusEntry {
  /// <summary>The filename.</summary>
  public string Name { get; init; } = "";

  /// <summary>The full slash-separated path from the volume root.</summary>
  public string FullPath { get; init; } = "";

  /// <summary>The logical file size in bytes.</summary>
  public long Size { get; init; }

  /// <summary>Whether this entry is a directory rather than a file.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>
  /// Whether this file is a dataless placeholder (decmpfs DATALESS_CMPFS_TYPE or
  /// DATALESS_PKG_CMPFS_TYPE): its content lives with a file provider, not on this volume, so
  /// it reports a size of 0 and extracts as an empty file.
  /// </summary>
  public bool IsDataless { get; init; }

  /// <summary>Whether this entry is a symbolic link (Finder type 'slnk').</summary>
  public bool IsSymlink { get; init; }

  /// <summary>The symlink target path (data-fork contents), or null when not a link.</summary>
  public string? LinkTarget { get; init; }

  /// <summary>The Catalog Node ID assigned to this entry.</summary>
  public uint Cnid { get; init; }

  /// <summary>The last modification timestamp (may be null if not available).</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>The first allocation block of the data fork.</summary>
  internal uint FirstBlock { get; init; }

  /// <summary>The number of allocation blocks in the data fork.</summary>
  internal uint BlockCount { get; init; }

  /// <summary>
  /// Every extent of the data fork in logical order: the eight the catalog record
  /// holds, then any the extents overflow file adds.
  /// </summary>
  internal IReadOnlyList<(uint StartBlock, uint BlockCount)> Extents { get; init; } = [];

  /// <summary>
  /// The "com.apple.decmpfs" attribute of a file stored with HFS+ transparent compression, or
  /// null. When set, <see cref="Size"/> is the length it decompresses to.
  /// </summary>
  internal byte[]? Decmpfs { get; init; }

  /// <summary>The resource fork's length, read only for a compressed file (whose chunks it may hold).</summary>
  internal long ResourceSize { get; init; }

  /// <summary>The resource fork's extents, read only for a compressed file.</summary>
  internal IReadOnlyList<(uint StartBlock, uint BlockCount)> ResourceExtents { get; init; } = [];
}

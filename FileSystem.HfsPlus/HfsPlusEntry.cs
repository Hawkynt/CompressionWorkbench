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

  /// <summary>The Catalog Node ID assigned to this entry.</summary>
  public uint Cnid { get; init; }

  /// <summary>The last modification timestamp (may be null if not available).</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>The first allocation block of the data fork.</summary>
  internal uint FirstBlock { get; init; }

  /// <summary>The number of allocation blocks in the data fork.</summary>
  internal uint BlockCount { get; init; }
}

namespace FileFormat.Adf;

/// <summary>
/// Represents a single file or directory entry found within an ADF disk image.
/// </summary>
public sealed class AdfEntry {
  /// <summary>The filename as stored in the AmigaDOS directory block.</summary>
  public string Name { get; init; } = "";

  /// <summary>The full slash-separated path from the disk root (e.g. "dir/subdir/file.txt").</summary>
  public string FullPath { get; init; } = "";

  /// <summary>Whether this entry is a directory rather than a file.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>The sector number of the file or directory header block on the disk.</summary>
  public int HeaderBlock { get; init; }

  /// <summary>The uncompressed file size in bytes (0 for directories).</summary>
  public int Size { get; init; }
}

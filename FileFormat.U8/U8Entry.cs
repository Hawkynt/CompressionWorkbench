namespace FileFormat.U8;

/// <summary>
/// Represents a single entry (file or directory) inside a Nintendo U8 archive.
/// </summary>
public sealed class U8Entry {

  /// <summary>Gets the full path of this entry, with <c>/</c> separators (no leading slash).</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of this entry's data inside the archive (files only).</summary>
  public long Offset { get; init; }

  /// <summary>Gets the size in bytes of this entry's data (files only; 0 for directories).</summary>
  public long Size { get; init; }

  /// <summary>Gets a value indicating whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }
}

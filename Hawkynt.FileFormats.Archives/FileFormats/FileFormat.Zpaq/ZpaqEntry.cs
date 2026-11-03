namespace FileFormat.Zpaq;

/// <summary>
/// A file or directory as recorded in a ZPAQ archive.
/// </summary>
public sealed class ZpaqEntry {
  /// <summary>Path as stored, with forward slashes; directories end with '/'.</summary>
  public string FileName { get; }

  /// <summary>Uncompressed size in bytes, or -1 when the archive does not record it (streaming format).</summary>
  public long Size { get; }

  /// <summary>
  /// Compressed size attributed to this entry: the share of the data blocks it references,
  /// proportional to its new (non-deduplicated) fragments. Zero for directories.
  /// </summary>
  public long CompressedSize { get; }

  /// <summary>Last-modified time (UTC) from the archive's decimal date, or <see langword="null"/>.</summary>
  public DateTime? LastModified { get; }

  /// <summary>Whether this entry is a directory.</summary>
  public bool IsDirectory { get; }

  /// <summary>1-based version (transaction) that last wrote this entry.</summary>
  public int Version { get; }

  /// <summary>
  /// Raw attribute bytes as stored (first byte 'w' = Windows attributes follow, 'u' = Unix mode
  /// follows), packed little-endian; 0 when none were stored.
  /// </summary>
  public long Attributes { get; }

  /// <summary>Fragment IDs that make up the file (journaling format).</summary>
  internal IReadOnlyList<uint> Fragments { get; }

  /// <summary>Streaming-format segments (block offset, segment index) that make up the file.</summary>
  internal IReadOnlyList<(long BlockOffset, int Segment)> Segments { get; }

  internal ZpaqEntry(string fileName, long size, long compressedSize, DateTime? lastModified, int version, long attributes,
      IReadOnlyList<uint> fragments, IReadOnlyList<(long, int)> segments) {
    this.FileName = fileName;
    this.Size = size;
    this.CompressedSize = compressedSize;
    this.LastModified = lastModified;
    this.IsDirectory = fileName.EndsWith('/');
    this.Version = version;
    this.Attributes = attributes;
    this.Fragments = fragments;
    this.Segments = segments;
  }
}

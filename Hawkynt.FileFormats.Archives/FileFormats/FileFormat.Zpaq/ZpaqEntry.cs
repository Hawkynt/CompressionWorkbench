namespace FileFormat.Zpaq;

/// <summary>
/// Represents a single file entry recorded in a ZPAQ journaling archive.
/// </summary>
public sealed class ZpaqEntry {
  /// <summary>
  /// Gets the filename as stored in the archive.
  /// Path separators are always forward slashes; directory entries end with '/'.
  /// </summary>
  public string FileName { get; }

  /// <summary>
  /// Gets the uncompressed size of the file in bytes as reported by the archive.
  /// This value is zero for directory entries and for files whose size could not
  /// be determined from the journal (e.g. when the header block is compressed).
  /// </summary>
  public long Size { get; }

  /// <summary>
  /// Gets the compressed size of the data block(s) that carry this file's data.
  /// This is the total byte count of the raw (still-compressed) data blocks
  /// associated with this entry.
  /// </summary>
  public long CompressedSize { get; }

  /// <summary>
  /// Gets the last-modified timestamp decoded from the journal header, or
  /// <see langword="null"/> when the timestamp could not be recovered.
  /// </summary>
  public DateTime? LastModified { get; }

  /// <summary>
  /// Gets a value indicating whether this entry represents a directory.
  /// </summary>
  public bool IsDirectory { get; }

  /// <summary>
  /// Gets the zero-based transaction index in which this entry was recorded.
  /// Each append to a ZPAQ archive constitutes a new transaction; the most
  /// recent transaction with a given filename defines the file's current state.
  /// </summary>
  public int Version { get; }

  internal ZpaqEntry(
      string fileName,
      long   size,
      long   compressedSize,
      DateTime? lastModified,
      bool   isDirectory,
      int    version) {
    FileName       = fileName;
    Size           = size;
    CompressedSize = compressedSize;
    LastModified   = lastModified;
    IsDirectory    = isDirectory;
    Version        = version;
  }
}

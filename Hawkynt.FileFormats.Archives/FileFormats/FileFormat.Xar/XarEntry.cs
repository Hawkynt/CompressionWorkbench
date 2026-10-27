namespace FileFormat.Xar;

/// <summary>
/// Represents a single file entry in a XAR archive.
/// </summary>
public sealed class XarEntry {
  /// <summary>File name (path within archive).</summary>
  public required string FileName { get; init; }

  /// <summary>Uncompressed size in bytes.</summary>
  public long OriginalSize { get; init; }

  /// <summary>Compressed size in bytes.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Compression method: "none", "zlib", "bzip2", "lzma", "xz", or the raw encoding style when unrecognised.</summary>
  public string Method { get; init; } = "none";

  /// <summary>Last modification date.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>Offset of data in the heap (relative to heap start).</summary>
  internal long HeapOffset { get; init; }

  /// <summary>Archived checksum (from TOC).</summary>
  internal string? ArchivedChecksum { get; init; }

  /// <summary>Extracted checksum (from TOC).</summary>
  internal string? ExtractedChecksum { get; init; }

  /// <summary>Hash named by the extracted checksum's <c>style</c> attribute (e.g. "sha1", "md5").</summary>
  internal string? ExtractedChecksumStyle { get; init; }
}

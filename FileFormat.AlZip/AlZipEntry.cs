namespace FileFormat.AlZip;

/// <summary>
/// Represents a single file entry in an ALZip (.alz) archive.
/// </summary>
public sealed class AlZipEntry {
  /// <summary>File name (path within archive).</summary>
  public required string FileName { get; init; }

  /// <summary>Uncompressed size in bytes.</summary>
  public long OriginalSize { get; init; }

  /// <summary>Compressed size in bytes.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }

  /// <summary>Compression method: Store(0), Bzip2(1), Deflate(2).</summary>
  public int Method { get; init; }

  /// <summary>CRC-32 checksum of uncompressed data.</summary>
  public uint Crc32 { get; init; }

  /// <summary>Last modification date.</summary>
  public DateTime? LastModified { get; init; }

  /// <summary>File attributes byte.</summary>
  public byte Attributes { get; init; }

  /// <summary>Offset of the compressed data in the stream.</summary>
  internal long DataOffset { get; init; }

  /// <summary>Human-readable method name.</summary>
  public string MethodName => Method switch {
    0 => "Store",
    1 => "Bzip2",
    2 => "Deflate",
    _ => $"Unknown({Method})",
  };
}

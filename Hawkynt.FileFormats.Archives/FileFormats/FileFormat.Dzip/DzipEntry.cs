namespace FileFormat.Dzip;

/// <summary>
/// Represents a single entry in a Bloodlines DZIP archive.
/// </summary>
public sealed class DzipEntry {
  /// <summary>Gets the entry path (forward-slash separated, e.g. "materials/test.vmt").</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute byte offset of the entry's data within the archive stream.</summary>
  public long Offset { get; init; }

  /// <summary>Gets the on-disk size of the entry data in bytes.</summary>
  public long CompressedSize { get; init; }

  /// <summary>Gets the uncompressed (original) size of the entry data in bytes.</summary>
  public long Size { get; init; }

  /// <summary>Gets the compression flag (0 = stored, non-zero = LZSS-compressed).</summary>
  public byte CompressionFlag { get; init; }
}

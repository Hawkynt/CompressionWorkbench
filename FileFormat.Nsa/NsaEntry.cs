namespace FileFormat.Nsa;

/// <summary>
/// Represents a single file entry in an NSA archive.
/// </summary>
public sealed class NsaEntry {
  /// <summary>Gets the filename stored in the archive (null-terminated Shift-JIS/ASCII).</summary>
  public string Name { get; init; } = string.Empty;

  /// <summary>Gets the compression type for this entry.</summary>
  public NsaCompressionType CompressionType { get; init; }

  /// <summary>Gets the absolute offset of the compressed data from the start of the archive file.</summary>
  public uint Offset { get; init; }

  /// <summary>Gets the compressed size of the data in bytes.</summary>
  public uint CompressedSize { get; init; }

  /// <summary>Gets the original (uncompressed) size of the data in bytes.</summary>
  public uint OriginalSize { get; init; }
}

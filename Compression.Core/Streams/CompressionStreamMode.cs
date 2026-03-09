namespace Compression.Core.Streams;

/// <summary>
/// Specifies whether a compression stream is compressing or decompressing.
/// </summary>
public enum CompressionStreamMode {
  /// <summary>
  /// The stream compresses data written to it.
  /// </summary>
  Compress,

  /// <summary>
  /// The stream decompresses data read from it.
  /// </summary>
  Decompress
}

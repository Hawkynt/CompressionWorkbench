namespace FileFormat.Nsis;

/// <summary>
/// Identifies the compression algorithm used in an NSIS installer.
/// </summary>
public enum NsisCompression {
  /// <summary>No compression — data is stored verbatim.</summary>
  None = 0,
  /// <summary>Zlib/Deflate compression (2-byte zlib header + raw Deflate stream).</summary>
  Zlib = 1,
  /// <summary>BZip2 block-sorted compression.</summary>
  BZip2 = 2,
  /// <summary>LZMA compression (5-byte properties header + raw LZMA stream).</summary>
  Lzma = 3,
}

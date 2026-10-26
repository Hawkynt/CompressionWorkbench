namespace FileFormat.Nsis;

/// <summary>
/// Identifies the compressor an NSIS installer was built with (<c>SetCompressor</c>).
/// </summary>
public enum NsisCompression {
  /// <summary>Nothing is compressed: the header and every block are stored.</summary>
  None = 0,
  /// <summary>"zlib": raw Deflate with no zlib wrapper (NSIS 2's inflate also omits stored-block NLEN).</summary>
  Zlib = 1,
  /// <summary>"bzip2": NSIS's header-less bzip2 dialect.</summary>
  BZip2 = 2,
  /// <summary>"lzma": a 5-byte properties header, then range-coded data ending in an end marker.</summary>
  Lzma = 3,
}

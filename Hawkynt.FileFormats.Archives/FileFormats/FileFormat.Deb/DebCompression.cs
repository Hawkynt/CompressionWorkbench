namespace FileFormat.Deb;

/// <summary>
/// Specifies the compression algorithm used for tar members inside a .deb package.
/// </summary>
public enum DebCompression {
  /// <summary>Gzip compression (.tar.gz). This is the traditional default.</summary>
  Gzip,

  /// <summary>XZ compression (.tar.xz).</summary>
  Xz,

  /// <summary>Zstandard compression (.tar.zst).</summary>
  Zstd,

  /// <summary>BZip2 compression (.tar.bz2).</summary>
  Bzip2,
}

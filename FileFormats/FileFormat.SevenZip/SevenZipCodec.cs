namespace FileFormat.SevenZip;

/// <summary>
/// Specifies the compression codec used when writing a 7z archive.
/// </summary>
public enum SevenZipCodec {
  /// <summary>LZMA2 compression (default).</summary>
  Lzma2 = 0,

  /// <summary>LZMA compression.</summary>
  Lzma = 1,

  /// <summary>Deflate compression.</summary>
  Deflate = 2,

  /// <summary>BZip2 compression.</summary>
  BZip2 = 3,

  /// <summary>PPMd compression (Model H, variant used by 7-Zip).</summary>
  PPMd = 4,

  /// <summary>Copy (store without compression).</summary>
  Copy = 5,
}

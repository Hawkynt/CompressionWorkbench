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

  /// <summary>Zstandard, as the 7-Zip-zstd coder 04 F7 11 01 writes it.</summary>
  Zstd = 6,

  /// <summary>Brotli, as the 7-Zip-zstd coder 04 F7 11 02 writes it (3 MiB chunks behind skippable-frame headers).</summary>
  Brotli = 7,

  /// <summary>LZ4, as the 7-Zip-zstd coder 04 F7 11 04 writes it (4 MiB chunks behind skippable-frame headers).</summary>
  Lz4 = 8,

  /// <summary>LZ5 1.5, as the 7-Zip-zstd coder 04 F7 11 05 writes it (4 MiB chunks behind skippable-frame headers).</summary>
  Lz5 = 9,

  /// <summary>Lizard, as the 7-Zip-zstd coder 04 F7 11 06 writes it (4 MiB chunks behind skippable-frame headers).</summary>
  Lizard = 10,
}

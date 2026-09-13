namespace FileFormat.Zip;

/// <summary>
/// ZIP compression methods.
/// </summary>
public enum ZipCompressionMethod : ushort {
  /// <summary>No compression (store).</summary>
  Store = 0,

  /// <summary>Shrink (LZW with partial clearing).</summary>
  Shrink = 1,

  /// <summary>Reduce with compression factor 1.</summary>
  Reduce1 = 2,

  /// <summary>Reduce with compression factor 2.</summary>
  Reduce2 = 3,

  /// <summary>Reduce with compression factor 3.</summary>
  Reduce3 = 4,

  /// <summary>Reduce with compression factor 4.</summary>
  Reduce4 = 5,

  /// <summary>Implode (LZ77 + Shannon-Fano trees).</summary>
  Implode = 6,

  /// <summary>Deflate compression.</summary>
  Deflate = 8,

  /// <summary>Deflate64 (Enhanced Deflate) compression.</summary>
  Deflate64 = 9,

  /// <summary>BZip2 compression.</summary>
  BZip2 = 12,

  /// <summary>LZMA compression.</summary>
  Lzma = 14,

  /// <summary>Zstandard compression.</summary>
  Zstd = 93,

  /// <summary>PPMd version I, Rev 1 compression.</summary>
  Ppmd = 98,

  /// <summary>WinZip AES encryption (actual method stored in extra field).</summary>
  WinZipAes = 99
}

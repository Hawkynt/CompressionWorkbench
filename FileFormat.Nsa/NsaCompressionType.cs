namespace FileFormat.Nsa;

/// <summary>
/// Compression type codes used in NSA archive entries.
/// </summary>
public enum NsaCompressionType : byte {
  /// <summary>No compression — data is stored as-is.</summary>
  None = 0,

  /// <summary>SPB — special image compression (not decompressable as generic binary).</summary>
  Spb = 1,

  /// <summary>LZSS — LZ77-based compression with 4 KB window and flag-byte framing.</summary>
  Lzss = 2,

  /// <summary>NBZ — bzip2 compressed data without the leading "BZ" magic header.</summary>
  Nbz = 3,
}

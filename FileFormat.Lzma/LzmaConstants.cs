namespace FileFormat.Lzma;

/// <summary>
/// Constants for the LZMA alone (.lzma) file format.
/// </summary>
public static class LzmaConstants {
  /// <summary>Total size of the LZMA alone header in bytes (1 properties + 4 dict size + 8 uncompressed size).</summary>
  public const int HeaderSize = 13;

  /// <summary>Default dictionary size: 8 MiB.</summary>
  public const int DefaultDictionarySize = 1 << 23;

  /// <summary>
  /// Sentinel value stored in the uncompressed-size field when the size is unknown.
  /// The decoder uses end-of-stream marker detection in this case.
  /// </summary>
  public const long UnknownSize = -1L;
}

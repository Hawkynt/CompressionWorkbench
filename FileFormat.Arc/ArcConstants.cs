namespace FileFormat.Arc;

/// <summary>
/// Constants for the ARC archive format.
/// </summary>
public static class ArcConstants {
  /// <summary>Magic byte that begins every ARC entry header.</summary>
  public const byte Magic = 0x1A;

  /// <summary>Method code that marks the end of the archive.</summary>
  public const byte MethodEndOfArchive = 0;

  /// <summary>Method 1: Stored (old format, no original-size field in header).</summary>
  public const byte MethodStoredOld = 1;

  /// <summary>Method 2: Stored (new format, original-size field present).</summary>
  public const byte MethodStored = 2;

  /// <summary>Method 3: Packed — ARC run-length encoding (RLE) using 0x90 as the repeat marker.</summary>
  public const byte MethodPacked = 3;

  /// <summary>Method 4: Squeezed — Huffman coding (not supported).</summary>
  public const byte MethodSqueezed = 4;

  /// <summary>Method 5: Crunched — LZW 9-12 bits (not supported).</summary>
  public const byte MethodCrunched5 = 5;

  /// <summary>Method 6: Crunched — LZW 9-12 bits, no RLE (not supported).</summary>
  public const byte MethodCrunched6 = 6;

  /// <summary>Method 7: Crunched — LZW 9-12 bits, new hash (not supported).</summary>
  public const byte MethodCrunched7 = 7;

  /// <summary>Method 8: Crunched — LZW 9-13 bits, dynamic reset.</summary>
  public const byte MethodCrunched8 = 8;

  /// <summary>Method 9: Squashed — LZW 9-13 bits, no RLE.</summary>
  public const byte MethodSquashed = 9;

  /// <summary>Length of the filename field in the header (13 bytes, null-terminated).</summary>
  public const int FileNameLength = 13;

  /// <summary>Size of the old-style entry header in bytes (method 1, no original-size field).</summary>
  public const int OldHeaderSize = 25;

  /// <summary>Size of the new-style entry header in bytes (methods 2+, includes original-size field).</summary>
  public const int NewHeaderSize = 29;

  /// <summary>The RLE repeat marker byte used by ARC's Packed (method 3) format.</summary>
  public const byte RleMarker = 0x90;

  /// <summary>Maximum bits for LZW codes in method 8/9.</summary>
  public const int LzwMaxBits = 13;

  /// <summary>Minimum bits for LZW codes (initial code width).</summary>
  public const int LzwMinBits = 9;
}

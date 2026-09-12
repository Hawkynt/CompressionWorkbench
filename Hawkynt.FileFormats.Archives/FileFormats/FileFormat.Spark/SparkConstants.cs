namespace FileFormat.Spark;

/// <summary>
/// Constants for the RISC OS Spark archive format (David Pilling, 1989).
/// Spark is essentially the ARC format with RISC OS extensions (load/exec addresses,
/// file attributes, and directory support).
/// </summary>
internal static class SparkConstants {
  /// <summary>Entry marker byte that begins every header (same as ARC).</summary>
  public const byte EntryMarker = 0x1A;

  // ---- Standard ARC methods (bits 0-6) ----

  /// <summary>Method 0x00: End of archive.</summary>
  public const byte MethodEndOfArchive = 0x00;

  /// <summary>Method 0x01: Stored (old format, no original-size field).</summary>
  public const byte MethodStoredOld = 0x01;

  /// <summary>Method 0x02: Stored.</summary>
  public const byte MethodStored = 0x02;

  /// <summary>Method 0x03: Packed (RLE using 0x90 escape).</summary>
  public const byte MethodPacked = 0x03;

  /// <summary>Method 0x04: Squeezed (static Huffman).</summary>
  public const byte MethodSqueezed = 0x04;

  /// <summary>Method 0x05: Crunched (LZW, old 9-12 bit with RLE pre-pass).</summary>
  public const byte MethodCrunchedOld = 0x05;

  /// <summary>Method 0x06: Crunched (LZW 9-12 bit, no clear code).</summary>
  public const byte MethodCrunched = 0x06;

  /// <summary>Method 0x07: Crunched + Packed (LZW + RLE).</summary>
  public const byte MethodCrunchedPacked = 0x07;

  /// <summary>Method 0x08: Crunched (LZW with variable codes, 9-13 bit, clear code).</summary>
  public const byte MethodCrunchedVariable = 0x08;

  /// <summary>Method 0x09: Squashed (LZW 9-13 bit, Unix compress style).</summary>
  public const byte MethodSquashed = 0x09;

  // ---- Spark-specific methods (bit 7 set = RISC OS extended) ----

  /// <summary>Method 0x80: End of directory marker.</summary>
  public const byte MethodEndOfDirectory = 0x80;

  /// <summary>Method 0x82: Stored directory entry (Spark-specific).</summary>
  public const byte MethodDirectory = 0x82;

  /// <summary>Method 0x83: Spark Packed (RLE with RISC OS extension).</summary>
  public const byte MethodSparkPacked = 0x83;

  /// <summary>Method 0x84: Spark Squeezed (Huffman with RISC OS extension).</summary>
  public const byte MethodSparkSqueezed = 0x84;

  /// <summary>Method 0x85: Spark Crunched old (LZW with RISC OS extension).</summary>
  public const byte MethodSparkCrunchedOld = 0x85;

  /// <summary>Method 0x86: Spark Crunched (LZW with RISC OS extension).</summary>
  public const byte MethodSparkCrunched = 0x86;

  /// <summary>Method 0x87: Spark Crunched + Packed (LZW + RLE with RISC OS extension).</summary>
  public const byte MethodSparkCrunchedPacked = 0x87;

  /// <summary>Method 0x88: Spark Crunched variable (LZW 9-13 bit with RISC OS extension).</summary>
  public const byte MethodSparkCrunchedVariable = 0x88;

  /// <summary>Method 0x89: Spark Squashed (LZW with RISC OS extension).</summary>
  public const byte MethodSparkSquashed = 0x89;

  /// <summary>Method 0xFF: Compressed (Spark "Squashed", Unix compress style LZW).</summary>
  public const byte MethodCompressed = 0xFF;

  /// <summary>Length of the filename field in the standard header (13 bytes, null-terminated).</summary>
  public const int FileNameLength = 13;

  /// <summary>Size of the RISC OS extension appended after the standard ARC header (load + exec + attributes).</summary>
  public const int RiscOsExtensionSize = 12;

  /// <summary>The RLE repeat marker byte (same as ARC).</summary>
  public const byte RleMarker = 0x90;

  /// <summary>Maximum bits for LZW codes in variable methods.</summary>
  public const int LzwMaxBits = 13;

  /// <summary>Minimum bits for LZW codes.</summary>
  public const int LzwMinBits = 9;

  /// <summary>
  /// Returns <see langword="true"/> if the method byte has bit 7 set,
  /// indicating a Spark extended method with RISC OS metadata.
  /// </summary>
  /// <param name="method">The compression method byte.</param>
  /// <returns><see langword="true"/> if the method is a Spark extension.</returns>
  public static bool IsSparkExtended(byte method) => (method & 0x80) != 0;

  /// <summary>
  /// Gets the base ARC method code by stripping the Spark extension bit.
  /// </summary>
  /// <param name="method">The compression method byte.</param>
  /// <returns>The base method code (0x00-0x09).</returns>
  public static byte GetBaseMethod(byte method) => (byte)(method & 0x7F);
}

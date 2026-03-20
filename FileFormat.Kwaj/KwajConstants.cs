namespace FileFormat.Kwaj;

/// <summary>
/// Constants for the Microsoft KWAJ compressed file format, produced by
/// COMPRESS.EXE and used in some Windows setup packages.
/// </summary>
public static class KwajConstants {
  /// <summary>
  /// The 8-byte file magic: ASCII "KWAJ" followed by 0x88, 0xF0, 0x27, 0xD1.
  /// </summary>
  public static ReadOnlySpan<byte> Magic => [0x4B, 0x57, 0x41, 0x4A, 0x88, 0xF0, 0x27, 0xD1];

  /// <summary>Length of the magic signature in bytes.</summary>
  public const int MagicLength = 8;

  /// <summary>Offset of the compression method field (uint16 LE).</summary>
  public const int MethodOffset = 8;

  /// <summary>Offset of the compressed data start offset field (uint16 LE).</summary>
  public const int DataOffsetOffset = 10;

  /// <summary>Offset of the header flags field (uint16 LE).</summary>
  public const int FlagsOffset = 12;

  /// <summary>Total size of the fixed (non-optional) header portion.</summary>
  public const int FixedHeaderSize = 14;

  /// <summary>
  /// Header flag bit 0: the four-byte decompressed data length field is present.
  /// </summary>
  public const ushort FlagHasDecompressedLength = 0x0001;

  /// <summary>Header flag bit 1: two unknown bytes are present.</summary>
  public const ushort FlagUnknown1 = 0x0002;

  /// <summary>Header flag bit 2: two unknown bytes are present.</summary>
  public const ushort FlagUnknown2 = 0x0004;

  /// <summary>
  /// Header flag bit 3: a null-terminated original filename string is present.
  /// </summary>
  public const ushort FlagHasFilename = 0x0008;

  // -------------------------------------------------------------------------
  // Compression method codes
  // -------------------------------------------------------------------------

  /// <summary>Method 0 — no compression; data is stored verbatim.</summary>
  public const ushort MethodStore = 0;

  /// <summary>Method 1 — each byte is XOR-ed with 0xFF.</summary>
  public const ushort MethodXor = 1;

  /// <summary>
  /// Method 2 — SZDD-style LZSS compression (not supported by this library).
  /// </summary>
  public const ushort MethodLzss = 2;

  /// <summary>
  /// Method 3 — LZ + Huffman compression, similar to LZH (not supported by this library).
  /// </summary>
  public const ushort MethodLzHuffman = 3;

  /// <summary>
  /// Method 4 — MSZIP (Deflate with 32 KB block reset), identical to the
  /// algorithm used inside Microsoft Cabinet files.
  /// </summary>
  public const ushort MethodMsZip = 4;
}

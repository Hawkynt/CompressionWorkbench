namespace FileFormat.Wim;

/// <summary>
/// Constants for the Windows Imaging (WIM) file format.
/// </summary>
public static class WimConstants {
  /// <summary>
  /// The WIM file magic bytes: "MSWIM\0\0\0" (8 bytes).
  /// </summary>
  public static ReadOnlySpan<byte> Magic => "MSWIM\0\0\0"u8;

  /// <summary>
  /// Length of the magic signature in bytes.
  /// </summary>
  public const int MagicLength = 8;

  /// <summary>
  /// Total size of the WIM file header in bytes (version 1.13).
  /// </summary>
  public const int HeaderSize = 208;

  /// <summary>
  /// WIM format version field value for version 1.13 (0x00010D00, little-endian).
  /// </summary>
  public const uint Version = 0x00010D00u;

  /// <summary>
  /// Default chunk size for compressed resources: 32 768 bytes (32 KB).
  /// </summary>
  public const int DefaultChunkSize = 32 * 1024;

  /// <summary>
  /// Size of a RESHDR_DISK_SHORT structure (packed size+flags, offset, original size).
  /// </summary>
  public const int ReshdrDiskShortSize = 24;

  /// <summary>
  /// Size of a single lookup table entry in the resource table, in bytes.
  /// Each entry holds: RESHDR_DISK_SHORT (24 bytes) + part number (2) + ref count (4) + SHA-1 hash (20).
  /// </summary>
  public const int LookupTableEntrySize = 50;

  // -------------------------------------------------------------------------
  // Compression type codes (stored in the header flags field)
  // -------------------------------------------------------------------------

  /// <summary>No compression — resources are stored verbatim.</summary>
  public const uint CompressionNone = 0;

  /// <summary>XPRESS (LZ Xpress plain) compression.</summary>
  public const uint CompressionXpress = 1;

  /// <summary>LZX compression.</summary>
  public const uint CompressionLzx = 2;

  /// <summary>LZMS compression.</summary>
  public const uint CompressionLzms = 3;

  /// <summary>XPRESS Huffman compression.</summary>
  public const uint CompressionXpressHuffman = 4;

  // -------------------------------------------------------------------------
  // Header flag bits (WimFlags field in the header)
  // -------------------------------------------------------------------------

  /// <summary>Flag bit indicating the WIM uses XPRESS compression.</summary>
  public const uint FlagXpressCompression = 0x00020000u;

  /// <summary>Flag bit indicating the WIM uses LZX compression.</summary>
  public const uint FlagLzxCompression = 0x00040000u;

  /// <summary>Flag bit indicating the WIM uses LZMS compression.</summary>
  public const uint FlagLzmsCompression = 0x00080000u;

  /// <summary>Flag bit indicating the WIM uses XPRESS Huffman compression.</summary>
  public const uint FlagXpressHuffmanCompression = 0x00400000u;

  // -------------------------------------------------------------------------
  // Resource flags
  // -------------------------------------------------------------------------

  /// <summary>Resource flag: resource is stored uncompressed.</summary>
  public const uint ResourceFlagUncompressed = 0u;

  /// <summary>Resource flag bit 0: resource data is compressed.</summary>
  public const uint ResourceFlagCompressed = 1u;

  /// <summary>Resource flag bit 1: resource contains image metadata.</summary>
  public const uint ResourceFlagMetadata = 2u;

  // -------------------------------------------------------------------------
  // LZX parameters
  // -------------------------------------------------------------------------

  /// <summary>
  /// Default LZX window size exponent used by WIM (window = 2^15 = 32 768 bytes).
  /// </summary>
  public const int LzxWindowBits = 15;
}

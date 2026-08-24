namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Constants for the XPRESS (LZ Xpress) compression algorithm used in WIM images and NTFS compression.
/// </summary>
public static class XpressConstants {
  /// <summary>Maximum sliding window size (8192 bytes — 13-bit offset field).</summary>
  public const int WindowSize = 8192;

  /// <summary>Minimum match length.</summary>
  public const int MinMatch = 3;

  /// <summary>Maximum representable match length (limited by the multi-byte length encoding).</summary>
  public const int MaxMatch = 65538;

  /// <summary>Number of flag bits per flag group (one 32-bit flag word covers 32 items).</summary>
  public const int FlagGroupSize = 32;

  /// <summary>
  /// The sentinel value in the extended-length byte stream indicating a 16-bit length follows.
  /// </summary>
  public const int LengthSentinel8 = 255;

  /// <summary>
  /// The sentinel value in the 16-bit length field indicating a 32-bit length follows.
  /// </summary>
  public const int LengthSentinel16 = 0;

  // ---- XPRESS Huffman variant ----

  /// <summary>Number of symbols in the XPRESS Huffman alphabet (256 literals + 256 match symbols).</summary>
  public const int HuffSymbolCount = 512;

  /// <summary>Uncompressed chunk size for the XPRESS Huffman variant (64 KiB).</summary>
  public const int HuffChunkSize = 65536;

  /// <summary>
  /// Largest match offset the XPRESS Huffman variant can name. The symbol
  /// carries the offset's power of two in four bits, so an offset of 65 536
  /// would need a seventeenth one and a symbol past the end of the alphabet.
  /// </summary>
  public const int HuffWindowSize = 65535;

  /// <summary>
  /// Longest match the XPRESS Huffman variant can name: a length past this has
  /// no room in the sixteen bits that carry it.
  /// </summary>
  public const int HuffMaxMatch = 65535;

  /// <summary>Size in bytes of the Huffman table header in each chunk (512 nibbles = 256 bytes).</summary>
  public const int HuffTableHeaderBytes = 256;

  /// <summary>Maximum Huffman code length for the XPRESS Huffman variant.</summary>
  public const int HuffMaxCodeLength = 15;
}

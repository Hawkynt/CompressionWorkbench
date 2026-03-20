namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Constants for the Brotli compressed data format (RFC 7932).
/// </summary>
public static class BrotliConstants {
  /// <summary>Maximum window size in bits (16 MB).</summary>
  public const int MaxWindowBits = 24;

  /// <summary>Minimum window size in bits (1 KB).</summary>
  public const int MinWindowBits = 10;

  /// <summary>Maximum number of block types per category.</summary>
  public const int MaxBlockTypes = 256;

  /// <summary>Number of literal context modes.</summary>
  public const int NumLiteralContextModes = 4;

  /// <summary>Number of distance context values.</summary>
  public const int NumDistanceContextValues = 4;

  /// <summary>Maximum number of direct distance codes.</summary>
  public const int MaxDirectDistanceCodes = 120;

  /// <summary>Maximum distance postfix bits.</summary>
  public const int MaxDistancePostfixBits = 3;

  /// <summary>Number of insert-and-copy length code symbols.</summary>
  public const int NumInsertAndCopyLengthCodes = 704;

  /// <summary>Maximum Huffman code length in bits.</summary>
  public const int MaxHuffmanCodeLength = 15;

  /// <summary>Number of code length code symbols.</summary>
  public const int NumCodeLengthCodes = 18;

  /// <summary>Alphabet size for literal prefix codes.</summary>
  public const int LiteralAlphabetSize = 256;

  /// <summary>Maximum number of block-type symbols.</summary>
  public const int MaxBlockTypeSymbols = 258;

  /// <summary>Order of code length code lengths (RFC 7932 Section 3.5).</summary>
  public static ReadOnlySpan<byte> CodeLengthCodeOrder => [
    1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15
  ];

  /// <summary>Lookup table for insert length codes (RFC 7932 Section 5).</summary>
  internal static readonly (int BaseValue, int ExtraBits)[] InsertLengthTable = [
    (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0),
    (6, 1), (8, 1), (10, 2), (14, 2), (18, 3), (26, 3),
    (34, 4), (50, 4), (66, 5), (98, 5), (130, 6), (194, 6),
    (322, 7), (578, 7), (1090, 8), (2114, 8), (4162, 9), (8258, 9)
  ];

  /// <summary>Lookup table for copy length codes (RFC 7932 Table 7).</summary>
  internal static readonly (int BaseValue, int ExtraBits)[] CopyLengthTable = [
    (2, 0), (3, 0), (4, 0), (5, 0), (6, 0), (7, 0), (8, 0), (9, 0),
    (10, 1), (12, 1), (14, 2), (18, 2), (22, 3), (30, 3),
    (38, 4), (54, 4), (70, 5), (102, 5), (134, 6), (198, 7),
    (326, 8), (582, 9), (1094, 10), (2118, 24)
  ];

  /// <summary>
  /// Context mode lookup table for LSB6 mode (context = last byte &amp; 0x3f).
  /// </summary>
  internal static byte[] BuildLsb6ContextLookup() {
    var lut = new byte[256];
    for (var i = 0; i < 256; ++i)
      lut[i] = (byte)(i & 0x3F);
    return lut;
  }

  /// <summary>
  /// Context mode lookup table for MSB6 mode (context = last byte &gt;&gt; 2).
  /// </summary>
  internal static byte[] BuildMsb6ContextLookup() {
    var lut = new byte[256];
    for (var i = 0; i < 256; ++i)
      lut[i] = (byte)(i >> 2);
    return lut;
  }

  /// <summary>
  /// Context mode lookup table for UTF8 mode (RFC 7932 Appendix B).
  /// </summary>
  internal static byte[] BuildUtf8ContextLookup() {
    var lut = new byte[256];
    // 0: ASCII control or space
    // 1-3: based on byte categories
    for (var i = 0; i < 256; ++i)
      lut[i] = i switch {
        < 0x20 or 0x7F => 0, // control
        < 0x80 => (byte)(i is >= 0x61 and <= 0x7A ? 2 : 1), // alphanumeric
        < 0xC0 => (byte)(2 + (i & 0x3F)), // continuation
        _ => (byte)(i < 0xE0 ? 2 : 3) // leading
      };
    return lut;
  }

  /// <summary>
  /// Context mode lookup table for Signed mode.
  /// </summary>
  internal static byte[] BuildSignedContextLookup() {
    var lut = new byte[256];
    for (var i = 0; i < 256; ++i) {
      // The signed context maps byte b to (b < 128) ? (b >> 2) : ((256 - b) >> 2)
      var v = i < 128 ? i : 256 - i;
      lut[i] = (byte)(v >> 2);
    }
    return lut;
  }

  /// <summary>
  /// Block length prefix code table (RFC 7932 Section 6).
  /// </summary>
  internal static readonly (int BaseValue, int ExtraBits)[] BlockLengthTable = [
    (1, 2), (5, 2), (9, 2), (13, 2),
    (17, 3), (25, 3), (33, 3), (41, 3),
    (49, 4), (65, 4), (81, 4), (97, 4),
    (113, 5), (145, 5), (177, 5), (209, 5),
    (241, 6), (305, 6), (369, 7), (497, 8),
    (753, 9), (1265, 10), (2289, 11), (4337, 12),
    (8433, 13), (16625, 24)
  ];
}

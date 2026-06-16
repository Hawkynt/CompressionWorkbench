namespace FileFormat.Szdd;

/// <summary>
/// Constants for the Microsoft SZDD / COMPRESS.EXE file format.
/// Used in old Windows setup files with extensions such as .Z_, .DL_, .EX_, etc.
/// </summary>
internal static class SzddConstants {
  /// <summary>
  /// The 8-byte magic sequence that begins every SZDD file:
  /// ASCII "SZDD" followed by 0x88, 0xF0, 0x27, 0x33.
  /// </summary>
  public static ReadOnlySpan<byte> Magic => "SZDD"u8;

  /// <summary>Continuation bytes that follow the "SZDD" ASCII portion of the magic.</summary>
  public static ReadOnlySpan<byte> MagicSuffix => [0x88, 0xF0, 0x27, 0x33];

  /// <summary>
  /// The 8-byte magic of the older "SZ " (QBasic / pre-SZDD) COMPRESS variant:
  /// ASCII "SZ " followed by 0x88, 0xF0, 0x27, 0x33, 0xD1. Produced by the
  /// 1980s Microsoft <c>COMPRESS.EXE</c> and QBasic distribution tooling; the
  /// later SZDD format replaced byte 2 (<c>0x20</c> → <c>'D'</c>) and the
  /// trailing 0xD1, and added a 2-byte mode/missing-char field. Neither this
  /// toolkit's old reader nor 7-Zip groks it; the LZSS body is otherwise the
  /// same 4096-byte ring as SZDD.
  /// </summary>
  public static ReadOnlySpan<byte> QBasicMagic => [0x53, 0x5A, 0x20, 0x88, 0xF0, 0x27, 0x33, 0xD1];

  /// <summary>Total length of the magic field (bytes 0-7).</summary>
  public const int MagicLength = 8;

  /// <summary>Total size of the SZDD header in bytes (magic + mode + missing-char + u32 length).</summary>
  public const int HeaderSize = 14;

  /// <summary>
  /// Header size of the old "SZ " variant: 8-byte magic + a 4-byte little-endian
  /// uncompressed length at offset 8; the LZSS stream begins at offset 12. There
  /// is no compression-mode byte and no missing-character field.
  /// </summary>
  public const int QBasicHeaderSize = 12;

  /// <summary>
  /// Compression mode byte for the standard LZSS variant used by COMPRESS.EXE.
  /// Stored at header offset 8.
  /// </summary>
  public const byte CompressionModeA = 0x41; // 'A'

  /// <summary>Size of the LZSS ring buffer (sliding window) in bytes.</summary>
  public const int WindowSize = 4096;

  /// <summary>Byte value used to pre-fill the ring buffer.</summary>
  public const byte WindowFill = 0x20; // space

  /// <summary>Initial write position in the ring buffer (4096 - 18 = 4078).</summary>
  public const int WindowInitPos = 4078;

  /// <summary>Minimum match length that qualifies for a back-reference.</summary>
  public const int MinMatchLength = 3;

  /// <summary>Maximum match length encodable in the 4-bit length field.</summary>
  public const int MaxMatchLength = 18; // (0x0F) + MinMatchLength
}

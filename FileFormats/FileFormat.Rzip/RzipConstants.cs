namespace FileFormat.Rzip;

/// <summary>
/// Constants for the RZIP format. Every value here is part of the wire format:
/// encoder and decoder must agree on all of them.
/// </summary>
internal static class RzipConstants {

  /// <summary>Magic bytes: "RZIP" (0x52, 0x5A, 0x49, 0x50).</summary>
  public static ReadOnlySpan<byte> Magic => "RZIP"u8;

  /// <summary>Major version number.</summary>
  public const byte VersionMajor = 2;

  /// <summary>Minor version number.</summary>
  public const byte VersionMinor = 1;

  /// <summary>Header size in bytes: magic (4) + version (2) + original size (4).</summary>
  public const int HeaderSize = 10;

  /// <summary>Tag byte introducing a run of literal bytes in the token stream.</summary>
  public const byte TagLiteral = 0;

  /// <summary>Tag byte introducing a copy from earlier output in the token stream.</summary>
  public const byte TagMatch = 1;

  /// <summary>
  /// Width of the rolling-hash window, and the shortest run worth encoding as a match.
  /// </summary>
  public const int MinMatch = 16;

  /// <summary>Multiplier of the polynomial rolling hash.</summary>
  public const uint RollingHashBase = 257;

  /// <summary>Maximum number of candidate positions retained per hash value.</summary>
  public const int HashBucketCapacity = 64;

  /// <summary>Maximum number of candidate positions examined per input position.</summary>
  public const int CandidateSearchLimit = 32;

  /// <summary>
  /// Longest Huffman code the literal stage will emit. A frequency distribution needing
  /// longer codes falls back to raw storage rather than widening the code table.
  /// </summary>
  public const int MaxCodeLength = 24;

  /// <summary>Literal stream stored as raw bytes.</summary>
  public const byte LiteralModeRaw = 0;

  /// <summary>Literal stream stored as canonical Huffman codes preceded by a length table.</summary>
  public const byte LiteralModeHuffman = 1;

  /// <summary>Number of entries in the literal code-length table.</summary>
  public const int LiteralAlphabetSize = 256;
}

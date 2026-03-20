namespace FileFormat.Bzip2;

/// <summary>
/// Constants for the bzip2 file format.
/// </summary>
internal static class Bzip2Constants {
  /// <summary>Block header magic: 6 bytes representing pi digits "1AY&amp;SY".</summary>
  public const long BlockHeaderMagic = 0x314159265359;

  /// <summary>End-of-stream magic: sqrt(pi) digits.</summary>
  public const long BlockEndMagic = 0x177245385090;

  /// <summary>Stream magic "BZ".</summary>
  public const ushort StreamMagic = 0x425A;

  /// <summary>Version byte 'h' for Huffman coding.</summary>
  public const byte VersionByte = 0x68;

  /// <summary>Maximum block size (9 * 100,000).</summary>
  public const int MaxBlockSize = 900_000;

  /// <summary>Maximum alphabet size (256 bytes + RUNA + RUNB).</summary>
  public const int MaxAlphaSize = 258;

  /// <summary>Maximum number of Huffman tables.</summary>
  public const int MaxTrees = 6;

  /// <summary>Maximum number of selectors.</summary>
  public const int MaxSelectors = 32768;

  /// <summary>Run-length symbol A (represents bit 0 in bijective base-2).</summary>
  public const int RunA = 0;

  /// <summary>Run-length symbol B (represents bit 1 in bijective base-2).</summary>
  public const int RunB = 1;

  /// <summary>Symbols per selector group.</summary>
  public const int GroupSize = 50;

  /// <summary>Maximum Huffman code length in bzip2.</summary>
  public const int MaxCodeLength = 20;
}

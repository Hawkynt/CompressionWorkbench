namespace Compression.Core.Dictionary.Csc;

/// <summary>
/// Constants of the libcsc stream format (Fu Siyuan, public domain, https://github.com/fusiyuan2010/CSC).
/// Every value here is fixed by the format — matching it is the specification.
/// </summary>
internal static class CscConstants {
  /// <summary>Size of the property header at the start of every stream.</summary>
  public const int PropertySize = 10;

  /// <summary>Granularity of the analyzer, the LZ window copy and the match-finder reserve.</summary>
  public const int MinBlockSize = 8 * 1024;

  /// <summary>Smallest window libcsc accepts.</summary>
  public const uint MinDictionarySize = 32 * 1024;

  /// <summary>Largest window libcsc accepts.</summary>
  public const uint MaxDictionarySize = 1024u * 1024 * 1024;

  /// <summary>Compressed stream block size the reference encoder writes.</summary>
  public const uint DefaultCscBlockSize = 64 * 1024;

  /// <summary>Uncompressed bytes the reference encoder hands to one segment.</summary>
  public const uint DefaultRawBlockSize = 2 * 1024 * 1024;

  /// <summary>Upper bound (exclusive) of the 24-bit block size fields.</summary>
  public const uint MaxBlockSizeField = 1 << 24;

  /// <summary>Initial value of every adaptive 12-bit probability.</summary>
  public const uint ProbabilityInit = 2048;

  // Block types written with EncodeInt in front of every block.
  public const uint TypeNone = 0x00;
  public const uint TypeNormal = 0x01;
  public const uint TypeEnglishText = 0x02;
  public const uint TypeExecutable = 0x03;
  public const uint TypeFast = 0x04;
  public const uint TypeNoLz = 0x05;
  public const uint TypeEntropy = 0x07;
  public const uint TypeBad = 0x08;
  public const uint TypeEndOfStream = 0x09;
  public const uint TypeDelta = 0x10;
  public const uint TypeSkip = 0x1E;

  /// <summary>Number of delta channel layouts the format knows.</summary>
  public const uint DeltaChannelLayouts = 5;

  /// <summary>Channel count per delta layout (<c>type - TypeDelta</c> indexes it).</summary>
  public static ReadOnlySpan<byte> DeltaChannels => [1, 2, 3, 4, 8];

  /// <summary>
  /// First distance of every distance slot. Slots 0-2 carry their distance directly, slot <c>s</c>
  /// above that covers <c>DistanceBase[s] .. DistanceBase[s + 1] - 1</c> with <c>s - 2</c> extra bits.
  /// </summary>
  public static ReadOnlySpan<uint> DistanceBase => [
    0, 1, 2, 3,
    5, 9, 17, 33,
    65, 129, 257, 513,
    1025, 2049, 4097, 8193,
    16385, 32769, 65537, 131073,
    262145, 524289, 1048577, 2097153,
    4194305, 8388609, 16777217, 33554433,
    67108865, 134217729, 268435457, 536870913,
    1073741825,
  ];

  /// <summary>4-bit bit reversal; the low four distance bits are coded LSB first through it.</summary>
  public static ReadOnlySpan<byte> Reverse4 => [0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15];

  /// <summary>Probability-table offset of the distance-slot tree for an encoded match length 0..6+.</summary>
  public static int DistanceSlotOffset(uint encodedLength) => encodedLength switch {
    0 => 0,
    1 or 2 => 16 * ((int)encodedLength - 1) + 8,
    3 or 4 or 5 => 32 * ((int)encodedLength - 3) + 8 + 16 * 2,
    _ => 32 * 3 + 8 + 16 * 2,
  };

  /// <summary>Bit width of the distance-slot tree for an encoded match length.</summary>
  public static int DistanceSlotBits(uint encodedLength) => encodedLength switch {
    0 => 3,
    1 or 2 => 4,
    _ => 5,
  };

  /// <summary>Total size of the distance-slot probability table.</summary>
  public const int DistanceSlotTableSize = 8 + 16 * 2 + 32 * 4;

  /// <summary>An encoded (dist 64, length 0) match terminates an LZ block.</summary>
  public const uint EndOfBlockDistance = 64;

  /// <summary>Encoded length that escapes into the long-length continuation.</summary>
  public const uint LongLengthEscape = 143;
}

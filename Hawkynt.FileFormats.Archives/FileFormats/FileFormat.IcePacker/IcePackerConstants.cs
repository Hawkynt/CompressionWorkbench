namespace FileFormat.IcePacker;

/// <summary>Header identifiers and limits of the Pack-Ice formats.</summary>
internal static class IcePackerConstants {
  /// <summary><c>ICE!</c>: Pack-Ice 2.31 and later, byte-oriented bit stream.</summary>
  public const uint MagicV2 = 0x49434521;

  /// <summary><c>Ice!</c>: Pack-Ice 2.0 to 2.20 (and, as a trailer, 1.1 to 1.14).</summary>
  public const uint MagicV1 = 0x49636521;

  /// <summary><c>TMM!</c>, <c>TSM!</c>, <c>SHE!</c>: renamed Pack-Ice 2.x headers seen in demos and games.</summary>
  public static readonly uint[] MagicV1Aliases = [0x544D4D21, 0x54534D21, 0x53484521];

  /// <summary>Header: identifier, packed size (header included) and unpacked size, all big-endian.</summary>
  public const int HeaderSize = 12;

  /// <summary>Longest match the 2.31 count code can express.</summary>
  public const int MaxMatchLength = 1033;

  /// <summary>Farthest two-byte match.</summary>
  public const int MaxDistance2 = 576;

  /// <summary>Largest encoded distance of a longer match (the 12-bit class tops out at 288 + 4095).</summary>
  public const int MaxEncodedDistance = 4383;

  /// <summary>Longest literal run one flag can introduce.</summary>
  public const int MaxLiteralRun = 33037;
}

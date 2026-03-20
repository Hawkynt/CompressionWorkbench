namespace FileFormat.PowerPacker;

/// <summary>
/// Constants for the Amiga PowerPacker (PP20) crunched file format.
/// PowerPacker was created by Nico Fran&#231;ois in 1988 and became the most widely
/// used file cruncher on the Amiga platform.
/// </summary>
internal static class PowerPackerConstants {

  /// <summary>
  /// The 4-byte magic signature at the start of every PP20-crunched file:
  /// ASCII "PP20" (0x50 0x50 0x32 0x30).
  /// </summary>
  public static ReadOnlySpan<byte> Magic => "PP20"u8;

  /// <summary>
  /// Alternative magic for encrypted PowerPacker files: ASCII "PX20" (0x50 0x58 0x32 0x30).
  /// Detection only; decryption is not supported.
  /// </summary>
  public static ReadOnlySpan<byte> PX20Magic => "PX20"u8;

  /// <summary>Length of the magic field in bytes.</summary>
  public const int MagicLength = 4;

  /// <summary>
  /// Size of the efficiency table immediately following the magic.
  /// Contains 4 bytes specifying the bit counts for the 4 offset size classes.
  /// </summary>
  public const int EfficiencyTableSize = 4;

  /// <summary>Offset of the efficiency table from the start of the file.</summary>
  public const int EfficiencyTableOffset = 4;

  /// <summary>
  /// Size of the decrunch info at the end of the file.
  /// Bytes 0-2: original (decompressed) size as a 24-bit big-endian integer.
  /// Byte 3: number of bits to skip at the start of the reverse bit stream.
  /// </summary>
  public const int DecrunchInfoSize = 4;

  /// <summary>
  /// Total overhead: 4 (magic) + 4 (efficiency) + 4 (decrunch info) = 12 bytes minimum.
  /// </summary>
  public const int MinFileSize = MagicLength + EfficiencyTableSize + DecrunchInfoSize;

  /// <summary>
  /// Default efficiency table for general-purpose data.
  /// Values are bit counts for offset classes 0 through 3.
  /// </summary>
  public static ReadOnlySpan<byte> DefaultEfficiency => [9, 10, 11, 12];

  /// <summary>Number of offset classes in the PP20 scheme.</summary>
  public const int OffsetClasses = 4;
}

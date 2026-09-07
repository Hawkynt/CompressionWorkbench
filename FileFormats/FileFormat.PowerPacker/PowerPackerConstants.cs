namespace FileFormat.PowerPacker;

/// <summary>
/// Constants for the Amiga PowerPacker (PP20) crunched file format.
/// PowerPacker was created by Nico Fran&#231;ois in 1988 and became the most widely
/// used file cruncher on the Amiga platform.
/// </summary>
internal static class PowerPackerConstants {

  private static readonly byte[] _fastEfficiency = [9, 9, 9, 9];
  private static readonly byte[] _mediocreEfficiency = [9, 10, 10, 10];
  private static readonly byte[] _goodEfficiency = [9, 10, 11, 11];
  private static readonly byte[] _veryGoodEfficiency = [9, 10, 12, 12];
  private static readonly byte[] _bestEfficiency = [9, 10, 12, 13];

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

  /// <summary>Number of offset classes in the PP20 scheme.</summary>
  public const int OffsetClasses = 4;

  /// <summary>
  /// Size of the efficiency table immediately following the magic.
  /// Contains 4 bytes specifying the bit counts for the 4 offset size classes.
  /// </summary>
  public const int EfficiencyTableSize = OffsetClasses;

  /// <summary>Offset of the efficiency table from the start of the file.</summary>
  public const int EfficiencyTableOffset = MagicLength;

  /// <summary>
  /// Size of the decrunch info at the end of the file.
  /// Bytes 0-2: original (decompressed) size as a 24-bit big-endian integer.
  /// Byte 3: number of bits to skip at the start of the reverse bit stream.
  /// </summary>
  public const int DecrunchInfoSize = 4;

  /// <summary>
  /// PP20 stores its packed bitstream as whole Amiga longwords, so the smallest
  /// structurally writable file is 16 bytes: header + one packed longword + trailer.
  /// </summary>
  public const int MinFileSize = MagicLength + EfficiencyTableSize + sizeof(uint) + DecrunchInfoSize;

  /// <summary>Largest original size representable by the 24-bit trailer field.</summary>
  public const int MaxOriginalSize = 0x00FF_FFFF;

  /// <summary>Traditional default efficiency preset.</summary>
  public static ReadOnlySpan<byte> DefaultEfficiency => _goodEfficiency;

  /// <summary>Returns the four offset widths for a historical efficiency preset.</summary>
  public static ReadOnlySpan<byte> GetEfficiency(PowerPackerEfficiency efficiency) => efficiency switch {
    PowerPackerEfficiency.Fast => _fastEfficiency,
    PowerPackerEfficiency.Mediocre => _mediocreEfficiency,
    PowerPackerEfficiency.Good => _goodEfficiency,
    PowerPackerEfficiency.VeryGood => _veryGoodEfficiency,
    PowerPackerEfficiency.Best => _bestEfficiency,
    _ => throw new ArgumentOutOfRangeException(nameof(efficiency)),
  };
}

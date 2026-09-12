namespace FileFormat.CompactPro;

/// <summary>
/// Constants for the Compact Pro (.cpt) archive format.
/// </summary>
internal static class CompactProConstants {
  /// <summary>Magic byte at offset 0 of the volume header.</summary>
  public const byte Magic = 0x01;

  /// <summary>Size of the volume header: magic byte (1) + entry count (2) = 3 bytes.</summary>
  public const int VolumeHeaderSize = 3;

  /// <summary>Entry type: file.</summary>
  public const byte EntryTypeFile = 0;

  /// <summary>Entry type: folder start.</summary>
  public const byte EntryTypeFolder = 1;

  /// <summary>Entry type: end-of-folder marker.</summary>
  public const byte EntryTypeEnd = 2;

  /// <summary>Compression method: stored (no compression).</summary>
  public const byte MethodStored = 0;

  /// <summary>Compression method: simple RLE (escape byte 0x90).</summary>
  public const byte MethodRle = 1;

  /// <summary>Compression method: LZ77 + RLE (the main compression method).</summary>
  public const byte MethodLzRle = 2;

  /// <summary>Compression method: LZ77 + Huffman (better ratio).</summary>
  public const byte MethodLzHuff = 3;

  /// <summary>RLE escape byte used by method 1.</summary>
  public const byte RleEscape = 0x90;

  /// <summary>Maximum filename length in a Compact Pro archive.</summary>
  public const int FileNameMaxLength = 63;

  /// <summary>
  /// Mac epoch: January 1, 1904 00:00:00 UTC.
  /// Compact Pro timestamps are seconds since this epoch.
  /// </summary>
  public static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>
  /// CRC-16/CCITT polynomial (normal, non-reflected form).
  /// Compact Pro uses the forward (non-reflected) variant with init=0.
  /// </summary>
  public const ushort Crc16Polynomial = 0x1021;
}

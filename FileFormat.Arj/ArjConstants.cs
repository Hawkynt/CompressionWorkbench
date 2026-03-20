namespace FileFormat.Arj;

/// <summary>
/// Constants for the ARJ archive format.
/// </summary>
public static class ArjConstants {
  /// <summary>
  /// The two-byte magic number that marks the start of every ARJ header (0xEA60, little-endian).
  /// </summary>
  public const ushort HeaderId = 0xEA60;

  /// <summary>
  /// Archiver version number written into archive headers.
  /// </summary>
  public const byte ArchiverVersion = 11;

  /// <summary>
  /// Minimum archiver version required to extract (stored method only).
  /// </summary>
  public const byte MinVersionToExtract = 1;

  // -------------------------------------------------------------------------
  // Host OS codes (byte 7 of the first-header section)
  // -------------------------------------------------------------------------

  /// <summary>Host OS: MS-DOS.</summary>
  public const byte OsDos = 0;

  /// <summary>Host OS: PRIMOS.</summary>
  public const byte OsPrimos = 1;

  /// <summary>Host OS: UNIX.</summary>
  public const byte OsUnix = 2;

  /// <summary>Host OS: Amiga.</summary>
  public const byte OsAmiga = 3;

  /// <summary>Host OS: Mac OS.</summary>
  public const byte OsMacOs = 4;

  /// <summary>Host OS: OS/2.</summary>
  public const byte OsOs2 = 5;

  /// <summary>Host OS: Apple IIe.</summary>
  public const byte OsApple2E = 6;

  /// <summary>Host OS: Windows 95/NT.</summary>
  public const byte OsWindows = 7;

  // -------------------------------------------------------------------------
  // Compression method codes (byte 9 of the first-header section)
  // -------------------------------------------------------------------------

  /// <summary>Compression method 0: store (no compression).</summary>
  public const byte MethodStore = 0;

  /// <summary>Compression method 1: compressed (LZ77+Huffman, primary).</summary>
  public const byte MethodCompressed1 = 1;

  /// <summary>Compression method 2: compressed (alternate).</summary>
  public const byte MethodCompressed2 = 2;

  /// <summary>Compression method 3: compressed (alternate).</summary>
  public const byte MethodCompressed3 = 3;

  /// <summary>Compression method 4: store (no-compression, fastest).</summary>
  public const byte MethodStoreFast = 4;

  // -------------------------------------------------------------------------
  // File type codes (byte 10 of the first-header section)
  // -------------------------------------------------------------------------

  /// <summary>File type: binary file.</summary>
  public const byte FileTypeBinary = 0;

  /// <summary>File type: text file.</summary>
  public const byte FileTypeText = 1;

  /// <summary>File type: comment/archive header.</summary>
  public const byte FileTypeComment = 2;

  /// <summary>File type: directory.</summary>
  public const byte FileTypeDirectory = 3;

  // -------------------------------------------------------------------------
  // ARJ flag bits (byte 8 of the first-header section)
  // -------------------------------------------------------------------------

  /// <summary>ARJ flag: garbled (encrypted).</summary>
  public const byte FlagGarbled = 0x01;

  /// <summary>ARJ flag: volume continuation.</summary>
  public const byte FlagVolume = 0x04;

  /// <summary>ARJ flag: extended file information present.</summary>
  public const byte FlagExtFile = 0x08;

  /// <summary>ARJ flag: path translated (directory separators converted).</summary>
  public const byte FlagPathSep = 0x10;

  /// <summary>ARJ flag: backup flag (from backup software).</summary>
  public const byte FlagBackup = 0x20;

  // -------------------------------------------------------------------------
  // Fixed sizes
  // -------------------------------------------------------------------------

  /// <summary>
  /// Byte offset within the basic header where the first-header section begins (byte 4).
  /// The CRC of the basic header is computed over this section.
  /// </summary>
  public const int FirstHeaderOffset = 4;

  /// <summary>
  /// Minimum length of the first-header section (bytes 4 through 33 inclusive, before filename).
  /// </summary>
  public const int FirstHeaderMinSize = 30;
}

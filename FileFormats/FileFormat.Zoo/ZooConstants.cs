namespace FileFormat.Zoo;

/// <summary>
/// Constants for the Zoo archive format.
/// </summary>
public static class ZooConstants {
  /// <summary>
  /// Magic number present in the archive header and every directory entry (0xFDC4A7DC).
  /// </summary>
  public const uint Magic = 0xFDC4A7DC;

  /// <summary>
  /// Default archive header text (ASCII, null-terminated, padded to 20 bytes).
  /// </summary>
  public const string DefaultHeaderText = "ZOO 2.10 Archive.\x1a";

  /// <summary>
  /// Total size in bytes of the archive header.
  /// </summary>
  public const int ArchiveHeaderSize = 34;

  /// <summary>
  /// Size in bytes of the fixed part of a directory entry (before the filename).
  /// Covers: tag(4) + type(1) + method(1) + nextOffset(4) + dataOffset(4) +
  ///         date(2) + time(2) + crc16(2) + origSize(4) + compSize(4) +
  ///         majorVer(1) + minorVer(1) + deleted(1) + structure(1) +
  ///         commentOffset(4) + commentLength(2) = 38 bytes.
  /// </summary>
  public const int DirectoryEntryFixedSize = 38;

  /// <summary>
  /// Maximum short filename length (13 bytes including null terminator, so 12 characters).
  /// </summary>
  public const int MaxShortNameLength = 12;

  /// <summary>
  /// Entry type for a standard file entry.
  /// </summary>
  public const byte TypeFile = 1;

  /// <summary>
  /// Entry type for a file entry that carries a long (extended) filename.
  /// </summary>
  public const byte TypeLongName = 2;

  /// <summary>
  /// Compression method: file is stored without compression.
  /// </summary>
  public const byte MethodStore = 0;

  /// <summary>
  /// Compression method: file is compressed with LZW (variable-width, 9–13 bits, LSB-first).
  /// </summary>
  public const byte MethodLzw = 1;

  /// <summary>
  /// Initial LZW code width in bits.
  /// </summary>
  public const int LzwMinBits = 9;

  /// <summary>
  /// Maximum LZW code width in bits (standard Zoo).
  /// </summary>
  public const int LzwMaxBits = 13;

  /// <summary>
  /// Major version number written into headers created by this library.
  /// </summary>
  public const byte MajorVersion = 2;

  /// <summary>
  /// Minor version number written into headers created by this library.
  /// </summary>
  public const byte MinorVersion = 1;
}

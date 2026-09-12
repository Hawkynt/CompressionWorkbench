namespace FileFormat.Zoo;

/// <summary>
/// Constants for the Zoo archive format.
/// </summary>
public static class ZooConstants {
  /// <summary>Magic number present in the archive header and every directory entry.</summary>
  public const uint Magic = 0xFDC4A7DC;

  /// <summary>Default human-readable archive header text.</summary>
  public const string DefaultHeaderText = "ZOO 2.10 Archive.\x1a";

  /// <summary>Minimum/legacy type-0 Zoo archive-header size.</summary>
  public const int MinimumArchiveHeaderSize = 34;

  /// <summary>Canonical Zoo 2.x type-1 archive-header size.</summary>
  public const int ArchiveHeaderSize = 42;

  /// <summary>Canonical size of a type-1 directory entry including its 13-byte short filename.</summary>
  public const int DirectoryEntryType1Size = 51;

  /// <summary>Fixed size of a type-2 directory entry before its variable section.</summary>
  public const int DirectoryEntryType2FixedSize = 56;

  /// <summary>Fixed part of any directory entry, up to the 13-byte short filename at offset 38.</summary>
  public const int DirectoryEntryFixedSize = 38;

  /// <summary>Offset of the variable-part length within a type-2 directory entry.</summary>
  internal const int DirectoryVariableLengthOffset = 51;

  /// <summary>Offset of the type-2 directory-entry CRC.</summary>
  internal const int DirectoryCrcOffset = 54;

  /// <summary>Maximum short filename length (13 bytes including null terminator).</summary>
  public const int MaxShortNameLength = 12;

  /// <summary>Entry type for a standard short-name file entry.</summary>
  public const byte TypeFile = 1;

  /// <summary>Entry type for a file entry with a variable portable-name section.</summary>
  public const byte TypeLongName = 2;

  /// <summary>Archive-header type used by Zoo 2.x extended headers.</summary>
  public const byte ArchiveHeaderTypeExtended = 1;

  /// <summary>Compression method: file is stored without compression.</summary>
  public const byte MethodStore = 0;

  /// <summary>Compression method: normal Zoo LZW.</summary>
  public const byte MethodLzw = 1;

  /// <summary>Initial LZW code width in bits.</summary>
  public const int LzwMinBits = 9;

  /// <summary>Maximum LZW code width in bits.</summary>
  public const int LzwMaxBits = 13;

  /// <summary>Timezone sentinel used when no timezone is known.</summary>
  public const byte NoTimezone = 127;

  /// <summary>Portable pathname/filesystem syntax identifier.</summary>
  public const ushort SystemIdPortable = 2;

  /// <summary>Five-byte marker placed immediately before member payloads.</summary>
  public static ReadOnlySpan<byte> FileLeader => "@)#(\0"u8;

  /// <summary>Minimum Zoo version required to extract Store and normal LZW entries.</summary>
  public const byte ExtractMajorVersion = 1;

  /// <summary>Minimum Zoo version required to extract Store and normal LZW entries.</summary>
  public const byte ExtractMinorVersion = 0;
}

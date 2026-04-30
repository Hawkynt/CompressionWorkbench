namespace FileFormat.Slf;

internal static class SlfConstants {
  /// <summary>Library header is fixed-size: 256 (LibName) + 256 (LibPath) + 4 + 4 + 2 + 2 + 1 + 3 + 4 = 532 bytes.</summary>
  internal const int HeaderSize = 532;

  /// <summary>Each entry record is fixed-size: 256 (FileName) + 4 + 4 + 1 + 3 + 8 + 2 + 2 = 280 bytes.</summary>
  internal const int EntrySize = 280;

  /// <summary>Width of the LibName, LibPath, and FileName fixed-width ASCII fields.</summary>
  internal const int NameFieldSize = 256;

  /// <summary>Last byte must be the null terminator, so payload tops out at 255 bytes.</summary>
  internal const int MaxNameLength = 255;

  /// <summary>Header version JA2 ships with — written as UInt16 LE.</summary>
  internal const ushort DefaultVersion = 0x0200;

  /// <summary>State byte for an active entry; archives compacted on read have only these.</summary>
  internal const byte StateActive = 0x00;

  /// <summary>State byte for a tombstoned entry — JA2 keeps the slot but skips the data on load.</summary>
  internal const byte StateDeleted = 0xFF;

  /// <summary>Sane upper bound for entry count to reject obviously corrupted headers fast.</summary>
  internal const int MaxPlausibleEntries = 1_000_000;
}

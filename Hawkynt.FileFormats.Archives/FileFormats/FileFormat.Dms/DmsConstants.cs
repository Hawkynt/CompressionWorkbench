namespace FileFormat.Dms;

/// <summary>Constants of the Amiga DMS (Disk Masher System) format, as used by libxad and xDMS.</summary>
internal static class DmsConstants {
  internal const uint MagicValue = 0x444D5321;     // "DMS!"
  internal const ushort TrackSignature = 0x5452;   // "TR"
  internal const int FileHeaderSize = 56;
  internal const int TrackHeaderSize = 20;

  internal const int ModeNone = 0;
  internal const int ModeSimple = 1;
  internal const int ModeQuick = 2;
  internal const int ModeMedium = 3;
  internal const int ModeDeep = 4;
  internal const int ModeHeavy1 = 5;
  internal const int ModeHeavy2 = 6;

  /// <summary>Track flag: keep the decompressor state for the next track.</summary>
  internal const int FlagNoInit = 1 << 0;
  /// <summary>Track flag: Heavy track carries new Huffman tables.</summary>
  internal const int FlagHeavyTables = 1 << 1;
  /// <summary>Track flag: Heavy output is RLE-coded as well.</summary>
  internal const int FlagHeavyRle = 1 << 2;
  /// <summary>Not stored: marks a Heavy 2 (8 KiB window) decode internally.</summary>
  internal const int FlagHeavy2Own = 1 << 3;

  internal const uint InfoEncrypted = 1 << 1;
  internal const uint InfoHighDensity = 1 << 4;

  /// <summary>Track number of a FILE_ID.DIZ text track.</summary>
  internal const int TrackDiz = 80;

  /// <summary>Bytes in one double-density Amiga cylinder (2 heads × 11 sectors × 512).</summary>
  internal const int CylinderSize = 2 * 11 * 512;

  internal const int StandardCylinders = 80;
}

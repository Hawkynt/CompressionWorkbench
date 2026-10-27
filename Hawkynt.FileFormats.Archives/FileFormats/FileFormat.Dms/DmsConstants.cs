namespace FileFormat.Dms;

/// <summary>
/// Constants for the Amiga DMS (Disk Masher System) format.
/// </summary>
internal static class DmsConstants {
  /// <summary>Magic bytes "DMS!" (big-endian 0x444D5321).</summary>
  internal static readonly byte[] Magic = [0x44, 0x4D, 0x53, 0x21];

  /// <summary>Magic value as a 32-bit unsigned integer.</summary>
  internal const uint MagicValue = 0x444D5321;

  /// <summary>Track header signature "TR" (0x5452).</summary>
  internal const ushort TrackSignature = 0x5452;

  /// <summary>Size of the file header in bytes.</summary>
  internal const int FileHeaderSize = 56;

  /// <summary>Size of a track header in bytes.</summary>
  internal const int TrackHeaderSize = 20;

  // ── Compression modes ───────────────────────────────────────────────────

  /// <summary>No compression — raw track data.</summary>
  internal const int ModeNone = 0;

  /// <summary>Simple run-length encoding.</summary>
  internal const int ModeSimpleRle = 1;

  /// <summary>Quick — LZ77.</summary>
  internal const int ModeQuick = 2;

  /// <summary>Medium — LZ77 + RLE.</summary>
  internal const int ModeMedium = 3;

  /// <summary>Deep — LZ77 with heavy search.</summary>
  internal const int ModeDeep = 4;

  /// <summary>Heavy1 — LZ77 + Huffman, no init.</summary>
  internal const int ModeHeavy1 = 5;

  /// <summary>Heavy2 — LZ77 + Huffman, with deep search init.</summary>
  internal const int ModeHeavy2 = 6;

  // ── Amiga disk geometry ─────────────────────────────────────────────────

  /// <summary>Sectors per track for double-density Amiga disks.</summary>
  internal const int SectorsPerTrack = 11;

  /// <summary>Bytes per sector.</summary>
  internal const int BytesPerSector = 512;

  /// <summary>Bytes per track (single side): 11 * 512 = 5632.</summary>
  internal const int TrackSize = SectorsPerTrack * BytesPerSector;

  /// <summary>Bytes per cylinder (2 sides): 2 * 5632 = 11264.</summary>
  internal const int CylinderSize = TrackSize * 2;

  /// <summary>Total tracks for a standard DD disk (80 cylinders * 2 sides).</summary>
  internal const int StandardTrackCount = 160;

  // ── RLE ─────────────────────────────────────────────────────────────────

  /// <summary>RLE escape byte used in Simple RLE mode.</summary>
  internal const byte RleEscape = 0x90;

  // ── CRC ─────────────────────────────────────────────────────────────────

  /// <summary>CRC-CCITT polynomial (non-reflected).</summary>
  internal const ushort CrcPolynomial = 0x1021;
}

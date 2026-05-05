namespace FileFormat.Sarc;

internal static class SarcConstants {

  // Section magics
  internal const string MagicSarc = "SARC";
  internal const string MagicSfat = "SFAT";
  internal const string MagicSfnt = "SFNT";

  // Header sizes
  // SARC header layout: magic(4)+HeaderSize(2)+BOM(2)+FileSize(4)+DataOffset(4)+Version(2)+Reserved(2)
  // = 20 bytes = 0x14, matching observed values from real Switch SARC files.
  internal const int SarcHeaderSize = 0x14;
  internal const int SarcHeaderBytes = 20;
  internal const int SfatHeaderSize = 0x0C; // SFAT header bytes
  internal const int SfatEntrySize = 0x10;  // 16 bytes per SFAT entry
  internal const int SfntHeaderSize = 0x08; // SFNT header bytes

  // Byte order marks
  internal const ushort BomLittleEndian = 0xFEFF;
  internal const ushort BomBigEndian = 0xFFFE;

  // Default version (0x0100 = v1.0); written by Switch tooling
  internal const ushort DefaultVersion = 0x0100;

  // Default hash multiplier; matches OatmealDome SarcLib / NSARC reference implementations
  internal const uint DefaultHashKey = 0x00000065;

  // Flag in upper 8 bits of AttrAndNameOffset indicating the entry has a name in SFNT
  internal const uint NamePresentFlag = 0x01000000;

  // Switch SDK convention: data region begins at a 256-byte aligned offset
  internal const int DataAlignment = 0x100;

  // Names are NUL-terminated and padded to a 4-byte boundary so that the (offset/4) field stays valid
  internal const int NameAlignment = 4;
}

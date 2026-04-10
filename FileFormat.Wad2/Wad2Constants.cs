namespace FileFormat.Wad2;

internal static class Wad2Constants {
  internal const string MagicWad2String = "WAD2";
  internal const string MagicWad3String = "WAD3";

  /// <summary>12-byte file header: magic(4) + numEntries(4) + dirOffset(4).</summary>
  internal const int HeaderSize = 12;

  /// <summary>32-byte directory entry: offset(4) + diskSize(4) + size(4) + type(1) + compression(1) + pad(2) + name(16).</summary>
  internal const int DirectoryEntrySize = 32;

  /// <summary>Maximum entry name length in bytes.</summary>
  internal const int MaxNameLength = 16;

  // Entry type constants
  internal const byte TypePalette   = 0x40; // '@'
  internal const byte TypeStatusPic = 0x42; // 'B'
  internal const byte TypeTexture   = 0x43; // 'C'
  internal const byte TypeMipTex    = 0x44; // 'D'
  internal const byte TypeRaw       = 0x45; // 'E'
}

namespace FileFormat.CramFs;

internal static class CramFsConstants {
  /// <summary>Little-endian magic number at offset 0 of a CramFS image.</summary>
  public const uint MagicLE = 0x28CD3D45;

  /// <summary>Big-endian magic number (byte-swapped LE magic).</summary>
  public const uint MagicBE = 0x453DCD28;

  /// <summary>ASCII signature stored at superblock offset 16 (16 bytes).</summary>
  public const string Signature = "Compressed ROMFS";

  /// <summary>Total size of the superblock in bytes.</summary>
  public const int SuperblockSize = 76;

  /// <summary>Byte offset of the root inode inside the superblock.</summary>
  public const int RootInodeOffset = 60;

  /// <summary>Size of a single cramfs inode in bytes.</summary>
  public const int InodeSize = 12;

  /// <summary>Size of each decompressed page block in bytes.</summary>
  public const int PageSize = 4096;

  // Feature flags (superblock word 2)
  public const uint FlagFsidVersion2    = 1 << 0;
  public const uint FlagSortedDirs      = 1 << 1;
  public const uint FlagHoles           = 1 << 8;
  public const uint FlagWrongSignature  = 1 << 9;
  public const uint FlagShiftedRootOffset = 1 << 10;

  // Unix mode type bits
  public const ushort S_IFMT   = 0xF000;
  public const ushort S_IFREG  = 0x8000;
  public const ushort S_IFDIR  = 0x4000;
  public const ushort S_IFLNK  = 0xA000;
}

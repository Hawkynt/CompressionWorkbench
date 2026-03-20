namespace FileFormat.SquashFs;

internal static class SquashFsConstants {
  public const uint Magic = 0x73717368; // "sqsh" in little-endian = bytes 68 73 71 73

  public const int SuperblockSize = 96;
  public const int MetadataBlockMaxSize = 8192;
  public const ushort MetadataUncompressedFlag = 0x8000;
  public const ushort MetadataSizeMask = 0x7FFF;

  // Compression types
  public const ushort CompressionGzip = 1;
  public const ushort CompressionLzma = 2;
  public const ushort CompressionLzo  = 3;
  public const ushort CompressionXz   = 4;
  public const ushort CompressionLz4  = 5;
  public const ushort CompressionZstd = 6;

  // Superblock flags
  public const ushort FlagUncInode     = 0x0001;
  public const ushort FlagUncData      = 0x0002;
  public const ushort FlagUncFragments = 0x0008;
  public const ushort FlagNoFragments  = 0x0020;
  public const ushort FlagAlwaysFrag   = 0x0040;
  public const ushort FlagUncXattrs    = 0x0800;

  // Inode types
  public const ushort InodeBasicDir     = 1;
  public const ushort InodeBasicFile    = 2;
  public const ushort InodeBasicSymlink = 3;
  public const ushort InodeBasicBlkDev = 4;
  public const ushort InodeBasicChrDev = 5;
  public const ushort InodeBasicFifo   = 6;
  public const ushort InodeBasicSocket = 7;
  public const ushort InodeExtDir      = 8;
  public const ushort InodeExtFile     = 9;
  public const ushort InodeExtSymlink  = 10;
  public const ushort InodeExtBlkDev  = 11;
  public const ushort InodeExtChrDev  = 12;
  public const ushort InodeExtFifo    = 13;
  public const ushort InodeExtSocket  = 14;

  // Block size flag
  public const uint BlockUncompressedFlag = 0x01000000;

  // Fragment sentinel: no fragment
  public const uint NoFragment = 0xFFFFFFFF;

  // Fragment table: 512 entries per metadata block
  public const int FragmentEntriesPerBlock = 512;
  public const int FragmentEntrySize = 16;

  // ID table: 2048 IDs per metadata block (8192 / 4)
  public const int IdsPerBlock = 2048;

  // Invalid table offset
  public const ulong InvalidTable = 0xFFFFFFFFFFFFFFFF;
}

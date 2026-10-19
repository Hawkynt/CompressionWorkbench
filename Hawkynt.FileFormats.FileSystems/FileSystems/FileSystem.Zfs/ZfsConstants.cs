namespace FileSystem.Zfs;

/// <summary>
/// ZFS on-disk constants as recorded in <c>docs/ZFS-ON-DISK.md</c> (copied exactly from
/// the ZFS On-Disk Specification and the OpenZFS headers: these are agreed values, not
/// expression).
/// </summary>
internal static class ZfsConstants {
  public const ulong UberblockMagic = 0x00BAB10C;
  public const ulong EmbeddedChecksumMagic = 0x0210DA7AB10C7A11;
  public const ulong SaMagic = 0x2F505A;

  public const int LabelSize = 256 * 1024;
  public const int LabelCount = 4;
  public const int NvListOffset = 16 * 1024;
  public const int NvListSize = 112 * 1024;
  public const int UberblockRingOffset = 128 * 1024;
  public const int UberblockRingSize = 128 * 1024;
  public const int MinUberblockShift = 10;
  public const int MaxUberblockShift = 13;

  /// <summary>Where a leaf device's allocatable space starts: two labels plus the boot area.</summary>
  public const long DataStart = 4L * 1024 * 1024;

  public const int SectorShift = 9;
  public const int GangHeaderSize = 512;
  public const int BlockPointerSize = 128;
  public const int DnodeSlotSize = 512;

  // Checksum ids.
  public const byte ChecksumInherit = 0;
  public const byte ChecksumOn = 1;
  public const byte ChecksumOff = 2;
  public const byte ChecksumLabel = 3;
  public const byte ChecksumGangHeader = 4;
  public const byte ChecksumZilog = 5;
  public const byte ChecksumFletcher2 = 6;
  public const byte ChecksumFletcher4 = 7;
  public const byte ChecksumSha256 = 8;
  public const byte ChecksumZilog2 = 9;
  public const byte ChecksumNoParity = 10;
  public const byte ChecksumSha512 = 11;
  public const byte ChecksumSkein = 12;
  public const byte ChecksumEdonR = 13;
  public const byte ChecksumBlake3 = 14;

  // Compression ids.
  public const byte CompressInherit = 0;
  public const byte CompressOn = 1;
  public const byte CompressOff = 2;
  public const byte CompressLzjb = 3;
  public const byte CompressEmpty = 4;
  public const byte CompressGzip1 = 5;
  public const byte CompressGzip9 = 13;
  public const byte CompressZle = 14;
  public const byte CompressLz4 = 15;
  public const byte CompressZstd = 16;

  // Object types.
  public const byte OtObjectDirectory = 1;
  public const byte OtDslDir = 12;
  public const byte OtDslDataset = 16;
  public const byte OtZnode = 17;
  public const byte OtPlainFileContents = 19;
  public const byte OtDirectoryContents = 20;
  public const byte OtMasterNode = 21;
  public const byte OtSa = 44;

  // Object-set types.
  public const ulong OstMeta = 1;
  public const ulong OstZfs = 2;
  public const ulong OstZvol = 3;
  public const int ObjsetTypeOffset = 0x2C0;

  public const byte DnodeFlagSpillBlkptr = 1 << 2;

  // ZAP block types and magics.
  public const ulong ZbtLeaf = 0x8000000000000000UL;
  public const ulong ZbtHeader = 0x8000000000000001UL;
  public const ulong ZbtMicro = 0x8000000000000003UL;
  public const ulong ZapMagic = 0x2F52AB2ABUL;
  public const uint ZapLeafMagic = 0x2AB1EAF;

  // Directory entry value: object id in bits 0-47, DT_* type in bits 60-63.
  public const ulong DirentObjectMask = (1UL << 48) - 1;
  public const int DirentTypeShift = 60;

  // POSIX file types (mode & S_IFMT).
  public const ulong ModeTypeMask = 0xF000;
  public const ulong ModeDirectory = 0x4000;
  public const ulong ModeRegular = 0x8000;
  public const ulong ModeSymlink = 0xA000;

  /// <summary>Size of a version ≤ 4 <c>znode_phys_t</c>; a short symlink target follows it in the bonus.</summary>
  public const int OldZnodeSize = 264;
}

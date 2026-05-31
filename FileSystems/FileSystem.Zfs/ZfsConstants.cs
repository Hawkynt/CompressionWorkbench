#pragma warning disable CS1591
namespace FileSystem.Zfs;

/// <summary>
/// ZFS on-disk constants. Values from OpenZFS <c>include/sys/*.h</c>.
/// </summary>
internal static class ZfsConstants {
  // Uberblock magic — stored as LE uint64 at offset 0 of each uberblock.
  public const ulong UberblockMagic = 0x00BAB10C;

  // Label geometry.
  public const int LabelSize = 256 * 1024;
  public const int BootHeaderOffset = 8 * 1024;     // after 8KB VTOC pad
  public const int NvListOffset = 16 * 1024;        // after 8KB VTOC + 8KB boot
  public const int NvListSize = 112 * 1024;
  public const int UberblockArrayOffset = 128 * 1024;
  public const int UberblockSize = 1024;            // for version 28
  public const int UberblockCount = 128;

  // Pool version — 28 is pre-feature-flags, easier to target.
  public const ulong PoolVersion = 28;

  // Pool state.
  public const ulong PoolStateActive = 0;

  // ashift (log2 of sector size). 9 = 512-byte sectors.
  public const uint Ashift = 9;
  public const uint SectorSize = 1u << (int)Ashift;

  // Block size for our dnode/metadata blocks (16 KB is the default minimum dnode block size).
  public const int DnodeBlockSize = 16 * 1024;
  public const int DnodeSize = 512;                 // bytes per dnode_phys_t
  public const int DnodesPerBlock = DnodeBlockSize / DnodeSize;

  // Checksum types (<c>include/sys/zio_checksum.h</c>).
  public const byte ZioChecksumOff = 2;
  public const byte ZioChecksumFletcher2 = 6;
  public const byte ZioChecksumFletcher4 = 7;
  public const byte ZioChecksumSha256 = 8;
  public const byte ZioChecksumLabel = 4;

  // Compression types (<c>include/sys/zio_compress.h</c>).
  public const byte ZioCompressOff = 2;

  // Object types (<c>include/sys/dmu.h</c>).
  public const byte DmuOtNone = 0;
  public const byte DmuOtObjectDirectory = 1;       // ZAP (name → obj)
  public const byte DmuOtObjectArray = 2;
  public const byte DmuOtDslDir = 12;
  public const byte DmuOtDslDirChildMap = 13;
  public const byte DmuOtDslDsSnapMap = 15;
  public const byte DmuOtDslProps = 16;
  public const byte DmuOtDslDataset = 17;
  public const byte DmuOtZap = 20;                  // generic ZAP
  public const byte DmuOtPlainFileContents = 19;
  public const byte DmuOtDirectoryContents = 20;    // aliased w/ ZAP
  public const byte DmuOtMasterNode = 21;
  public const byte DmuOtDeletedFiles = 22;
  public const byte DmuOtPackedNvlist = 24;

  // DMU Objset types.
  public const byte DmuOstNone = 0;
  public const byte DmuOstMeta = 1;
  public const byte DmuOstZfs = 2;

  // Dnode flags.
  public const byte DnodeFlagUsedBytes = 1;

  // ZAP magics.
  public const ulong ZbtMicro = 0x8000000000000003UL;  // MZAP_ENT_PHYS / microzap

  // ZFS directory ZAP entry value encoding (<c>include/sys/zfs_znode.h</c>):
  // the object id occupies the low 48 bits and the file type (the upper nibble of
  // the POSIX mode, i.e. <c>IFTODT</c>) occupies the high 4 bits:
  //   value = (uint64)type &lt;&lt; 60 | objid
  public const int ZfsDirentTypeShift = 60;
  public const ulong ZfsDirentObjMask = (1UL << 48) - 1;

  // File types as stored in the directory-entry value high bits (BSD d_type / IFTODT).
  public const ulong ZfsDirentTypeDir = 4;   // DT_DIR
  public const ulong ZfsDirentTypeReg = 8;   // DT_REG

  // POSIX mode bits for the znode bonus (<c>S_IFDIR</c> / <c>S_IFREG</c>).
  public const ulong ModeIfDir = 0x4000;
  public const ulong ModeIfReg = 0x8000;

  // Objset header size within an objset_phys_t block.
  public const int ObjsetPhysSize = 1024;

  // Well-known MOS object ID for object directory.
  public const ulong MosObjectDirectoryId = 1;
}

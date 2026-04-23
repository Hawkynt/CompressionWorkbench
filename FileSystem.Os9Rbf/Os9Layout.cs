#pragma warning disable CS1591
namespace FileSystem.Os9Rbf;

/// <summary>
/// Geometry of the Microware OS-9 RBF (Random-Block-File) reference image used
/// by this writer — a 35-track DSDD 5.25" CoCo floppy: 2 sides × 18 sectors/track
/// × 35 tracks × 256 bytes/sector = 322 560 bytes (~315 KB). Cluster size = 1
/// sector. Sector numbering is "LSN" (Logical Sector Number); multi-byte fields
/// are big-endian on disk.
/// </summary>
internal static class Os9Layout {
  public const int SectorSize = 256;
  public const int Sides = 2;
  public const int SectorsPerTrack = 18;
  public const int Tracks = 35;
  public const int TotalSectors = Sides * SectorsPerTrack * Tracks; // 1260
  public const int TotalBytes = TotalSectors * SectorSize;          // 322 560

  // Bitmap allocation: 1 bit per cluster. Cluster size = 1 sector, so we need
  // TotalSectors bits = TotalSectors / 8 bytes (rounded up).
  public const int ClusterSizeSectors = 1;
  public const int BitmapBits = TotalSectors;
  public const int BitmapBytes = (BitmapBits + 7) / 8;

  // Sector layout used by the writer.
  public const int IdentificationLsn = 0;          // sector 0: Pd_ identification
  public const int BitmapLsn = 1;                  // bitmap starts at LSN 1
  public const int BitmapSectors = (BitmapBytes + SectorSize - 1) / SectorSize;

  // Identification sector field offsets (per OS-9 Technical Reference).
  public const int Pd_DD_TOT = 0;   // u24 BE total sectors
  public const int Pd_DD_TKS = 3;   // u8 sectors/track
  public const int Pd_DD_MAP = 4;   // u16 BE bitmap length in bytes
  public const int Pd_DD_BIT = 6;   // u16 BE cluster size in sectors
  public const int Pd_DD_DIR = 8;   // u24 BE root directory descriptor LSN
  public const int Pd_DD_OWN = 11;  // u16 BE owner ID
  public const int Pd_DD_ATT = 13;  // u8 attributes
  public const int Pd_DD_DSK = 14;  // u16 BE disk ID
  public const int Pd_DD_FMT = 16;  // u8 format byte
  public const int Pd_DD_SPT = 17;  // u16 BE sectors/track
  public const int Pd_DD_RES = 19;  // u16 BE reserved (0)
  public const int Pd_DD_BT = 21;   // u24 BE bootstrap LSN
  public const int Pd_DD_BSZ = 24;  // u16 BE bootstrap size
  public const int Pd_DD_DAT = 26;  // 5 bytes YY MM DD HH MM
  public const int Pd_DD_NAM = 31;  // ASCII volume name, last char has bit 7 set

  // File descriptor field offsets.
  public const int FD_ATT = 0;      // u8 attributes (bit 7 = directory)
  public const int FD_OWN = 1;      // u16 BE owner
  public const int FD_DAT = 3;      // 5 bytes last-modified date
  public const int FD_LNK = 8;      // u8 link count
  public const int FD_SIZ = 9;      // u32 BE file size in bytes
  public const int FD_CRE = 13;     // 3 bytes creation date YY MM DD
  public const int FD_SEG = 16;     // segment list start

  // Each segment = 5 bytes: u24 BE start LSN, u16 BE sector count.
  public const int SegmentBytes = 5;
  // 48 segments fit in (256 - 16) / 5 = 48 bytes — enough for our use cases.
  public const int MaxSegmentsPerFd = (SectorSize - FD_SEG) / SegmentBytes;

  // Directory entry: 32 bytes.
  public const int DirEntryBytes = 32;
  public const int DirEntryNameMaxBytes = 29;       // 28 ASCII + last-char-with-MSB
  public const int DirEntryFdLsnOffset = 29;        // u24 BE
  public const int DirEntriesPerSector = SectorSize / DirEntryBytes; // 8

  // File attribute bits.
  public const byte FAttr_Directory = 0x80;
  public const byte FAttr_Sharable = 0x40;
  public const byte FAttr_PublicExec = 0x20;
  public const byte FAttr_PublicWrite = 0x10;
  public const byte FAttr_PublicRead = 0x08;
  public const byte FAttr_OwnerExec = 0x04;
  public const byte FAttr_OwnerWrite = 0x02;
  public const byte FAttr_OwnerRead = 0x01;

  public const byte DefaultDirAttr = FAttr_Directory | FAttr_PublicRead | FAttr_PublicExec |
                                     FAttr_OwnerRead | FAttr_OwnerWrite | FAttr_OwnerExec;
  public const byte DefaultFileAttr = FAttr_PublicRead | FAttr_OwnerRead | FAttr_OwnerWrite;
}

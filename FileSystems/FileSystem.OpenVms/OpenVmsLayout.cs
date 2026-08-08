#pragma warning disable CS1591
namespace FileSystem.OpenVms;

/// <summary>
/// Volume geometry for the CompressionWorkbench OpenVMS Files-11 ODS-2 writer.
/// These constants pin every LBN that the writer, reader, and in-place
/// modifier agree on. The numbers are NOT load-bearing on a real OpenVMS
/// instance — VMS mountability is out of scope (per the descriptor's
/// honest-scope notice) — but the writer, reader and in-place modifier
/// MUST agree exactly, otherwise an Add or Remove will desync the
/// allocation bitmap from the file headers.
///
/// <para>
/// Layout (512-byte LBNs):
/// </para>
/// <list type="bullet">
///   <item><c>LBN 0</c>            — boot block (zeros)</item>
///   <item><c>LBN 1</c>            — home block ("DECFILE11A " at byte 0x1E8 inside; "workbench-layout" marker at byte 132)</item>
///   <item><c>LBN 2 .. 17</c>     — BITMAP.SYS (16 LBNs = 65 536 bits coverage; one bit per LBN)</item>
///   <item><c>LBN 18 .. 273</c>   — INDEXF.SYS (256 file headers × 512 bytes; FH N at LBN 18 + N − 1)</item>
///   <item><c>LBN 274</c>          — 000000.DIR (root directory, single block initially; chains forward if it grows)</item>
///   <item><c>LBN 275 ..</c>       — data area, allocated by walking BITMAP.SYS for runs of free bits</item>
/// </list>
/// </summary>
public static class OpenVmsLayout {
  /// <summary>Files-11 block size — 512 bytes per LBN by spec.</summary>
  public const int BlockSize = 512;

  /// <summary>Total volume size in LBNs. 8192 × 512 = 4 MB default volume.</summary>
  public const int VolumeBlocks = 8192;

  /// <summary>Total volume size in bytes (<see cref="VolumeBlocks"/> × <see cref="BlockSize"/>).</summary>
  public const int VolumeBytes = VolumeBlocks * BlockSize;

  /// <summary>LBN of the boot block (zeros, reserved by ODS-2).</summary>
  public const int BootBlockLbn = 0;

  /// <summary>LBN of the home block (where "DECFILE11A " lives at +0x1E8).</summary>
  public const int HomeBlockLbn = 1;

  /// <summary>First LBN of BITMAP.SYS — the allocation bitmap.</summary>
  public const int BitmapStartLbn = 2;

  /// <summary>BITMAP.SYS size in LBNs. 16 × 512 × 8 = 65 536 bits — covers a 32 MB volume with headroom.</summary>
  public const int BitmapBlockCount = 16;

  /// <summary>First LBN past BITMAP.SYS (where INDEXF.SYS starts).</summary>
  public const int IndexFileStartLbn = BitmapStartLbn + BitmapBlockCount;

  /// <summary>Maximum number of files = number of File Headers reserved in INDEXF.SYS.</summary>
  public const int MaxFiles = 256;

  /// <summary>INDEXF.SYS span in LBNs (1 FH per LBN since FH = 512 bytes).</summary>
  public const int IndexFileBlockCount = MaxFiles;

  /// <summary>LBN of the 000000.DIR (root directory) first block.</summary>
  public const int RootDirectoryLbn = IndexFileStartLbn + IndexFileBlockCount;

  /// <summary>First LBN of the user-data area (after metadata + root dir).</summary>
  public const int DataAreaStartLbn = RootDirectoryLbn + 1;

  /// <summary>workbench-layout layout marker inside the home block at byte offset 132.</summary>
  /// <remarks>The value spells nothing: a marker that reads as words names whoever chose them.</remarks>
  public static readonly byte[] LayoutMarker =
    [0x9B, 0xF2, 0x19, 0x8C, 0x05, 0xAD, 0x14, 0xE0, 0x1B, 0xC6, 0x92];

  /// <summary>Byte offset inside the home block where the layout marker lives.</summary>
  public const int LayoutMarkerOffset = 132;

  /// <summary>Reserved File-ID numbers per ODS-2 spec.</summary>
  public const int IndexFileId = 1;
  /// <summary>Reserved File-ID number for the BITMAP.SYS storage bitmap (ODS-2 reserves FID 2).</summary>
  public const int BitmapFileId = 2;
  /// <summary>Reserved File-ID for the bad-block file (unused here; reserved per spec).</summary>
  public const int BadBlockFileId = 3;
  /// <summary>Reserved File-ID for the root directory 000000.DIR (ODS-2 reserves FID 4).</summary>
  public const int RootDirectoryFileId = 4;
  /// <summary>Reserved File-ID for the core image file (unused here; reserved per spec).</summary>
  public const int CoreImageFileId = 5;
  /// <summary>Reserved File-ID for the volume-set list (unused here; reserved per spec).</summary>
  public const int VolumeSetListFileId = 6;

  /// <summary>First File-ID number available for user files.</summary>
  public const int FirstUserFileId = 7;

  // ── File Header field layout (offsets inside the 512-byte FH block) ──

  public const int FhIdOffset = 0;       // FH2$B_IDOFFSET (in words)
  public const int FhMpOffset = 1;       // FH2$B_MPOFFSET (in words)
  public const int FhAcOffset = 2;       // FH2$B_ACOFFSET (in words)
  public const int FhRsOffset = 3;       // FH2$B_RSOFFSET (in words)
  public const int FhSegNum = 4;         // FH2$W_SEG_NUM (LE u16)
  public const int FhStrucLev = 6;       // FH2$W_STRUCLEV (LE u16, 0x0201 for ODS-2)
  public const int FhFidNum = 8;         // FH2$W_FID_NUM (LE u16)
  public const int FhFidSeq = 10;        // FH2$W_FID_SEQ (LE u16)
  public const int FhFidRvnNmx = 12;     // FH2$W_FID_RVN + NMX
  public const int FhExtFid = 14;        // FH2$W_EXT_FID
  public const int FhFileChar = 40;      // FH2$L_FILECHAR
  public const int FhRecAttr = 44;       // FH2$W_RECATTR (32 bytes)
  /// <summary>The record-attributes area, which says how long the file is.</summary>
  /// <remarks>
  /// A reader takes the end-of-file block from here, and refuses to look at any
  /// header past it — so a file whose attributes are blank has no headers a reader
  /// will open, however well formed they are. Both block counts are longwords
  /// stored high word first, which is how VMS wrote them.
  /// </remarks>
  public const int FhRecattr = 20;       // FH2$W_RECATTR
  public const int FhRecattrHighBlock = FhRecattr + 4;   // FAT$L_HIBLK, allocated
  public const int FhRecattrEndBlock = FhRecattr + 8;    // FAT$L_EFBLK, one past the last used

  public const int FhUsedSize = 80;      // size in bytes (writer-internal, 8 bytes)
  public const int FhAllocSize = 88;     // allocation in LBNs (writer-internal, 4 bytes)
  public const int FhChecksum = 510;     // FH2$W_CHECKSUM (LE u16)

  /// <summary>Words of the map area that hold retrieval pointers.</summary>
  public const int FhMapInUse = 58;      // FH2$B_MAP_INUSE

  /// <summary>The retrieval-pointer format this writer emits: count then a long block number.</summary>
  public const int RetrievalFormat2 = 2;

  /// <summary>Bytes one of those takes: three words.</summary>
  public const int RetrievalPointerBytes = 6;

  /// <summary>Blocks one can describe, its count being one less than the blocks in fourteen bits.</summary>
  public const int MaxBlocksPerPointer = 1 << 14;

  // Ident area starts at byte 128 (= 64 words); fits 20-char file name + meta.
  public const int FhIdentAreaOffset = 128;
  public const int FhFileNameLength = 20;

  // Map area starts at byte 256 (= 128 words); fits up to (510-256)/8 ≈ 31 pointer pairs.
  public const int FhMapAreaOffset = 256;

  // ── Home-block field offsets (inside the 512-byte home block) ──

  public const int HbHomeLbn = 0x000;        // HM2$L_HOMELBN
  public const int HbAltHomeLbn = 0x004;     // HM2$L_ALHOMELBN
  public const int HbAltIdxLbn = 0x008;      // HM2$L_ALTIDXLBN
  public const int HbStrucLev = 0x00C;       // HM2$W_STRUCLEV
  public const int HbCluster = 0x00E;        // HM2$W_CLUSTER
  public const int HbHomeVbn = 0x010;        // HM2$W_HOMEVBN
  public const int HbIbMapVbn = 0x016;       // HM2$W_IBMAPVBN
  public const int HbIbMapLbn = 0x018;       // HM2$L_IBMAPLBN
  public const int HbMaxFiles = 0x01C;       // HM2$L_MAXFILES
  public const int HbIbMapSize = 0x020;      // HM2$W_IBMAPSIZE
  public const int HbOwnerUic = 0x02C;       // HM2$W_VOLOWNER
  public const int HbFormatString = 0x1F0;   // "DECFILE11B  " for Files-11 Level 2

  /// <summary>Where the home block keeps the sum of the 255 words ahead of it.</summary>
  /// <remarks>
  /// These offsets are the Files-11 home block as an ODS-2 reader lays it out, not
  /// as this writer once guessed: the volume name at 0x1D8, the structure name at
  /// 0x1CC and the format at 0x1F0, with the two sums at 0x3A and 0x1FE. Written
  /// eight bytes early, the format string simply is not where a reader looks and
  /// the volume is turned away before anything else is read.
  /// </remarks>
  public const int HbChecksum2 = 0x1FE;      // HM2$W_CHECKSUM2

  /// <summary>Sum of the first twenty-nine words, which a reader also checks.</summary>
  public const int HbChecksum1 = 0x03A;      // HM2$W_CHECKSUM1

  /// <summary>Where the volume's serial number sits.</summary>
  public const int HbSerialNumber = 0x1C8;   // HM2$L_SERIALNUM

  /// <summary>The structure name, twelve characters.</summary>
  public const int HbStructureName = 0x1CC;  // HM2$T_STRUCNAME

  /// <summary>The owner's name, twelve characters.</summary>
  public const int HbOwnerName = 0x1E4;      // HM2$T_OWNERNAME
  public const int HbVolumeName = 0x1D8;     // 12-char ASCII volume label

  /// <summary>
  /// Number of LBNs the fixed metadata occupies: boot block, home block,
  /// BITMAP.SYS, INDEXF.SYS and the root directory. Everything below this LBN
  /// is written before any file payload, so a volume can be emitted as a small
  /// metadata prefix followed by the payloads streamed into place.
  /// </summary>
  public const int MetadataBlockCount = DataAreaStartLbn;

  /// <summary>Size in bytes of the fixed metadata prefix.</summary>
  public const int MetadataBytes = MetadataBlockCount * BlockSize;

  /// <summary>
  /// Computes the byte offset of an LBN inside the volume. The result is 64-bit:
  /// a volume larger than 4 GB has LBNs whose byte offset does not fit in an int.
  /// </summary>
  public static long LbnToByteOffset(long lbn) => lbn * BlockSize;

  /// <summary>Computes the byte offset of File Header <paramref name="fid"/> (1-based) inside INDEXF.SYS.</summary>
  public static long FileHeaderByteOffset(int fid) => LbnToByteOffset(IndexFileStartLbn + fid - 1);
}

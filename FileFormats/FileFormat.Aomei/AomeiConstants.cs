#pragma warning disable CS1591
namespace FileFormat.Aomei;

/// <summary>
/// Wire-format constants for the AOMEI Backupper image format, recovered from
/// reverse engineering of <c>ambakdrv.sys</c>, <c>ammntdrv.sys</c>,
/// <c>ImgFile.dll</c>, <c>Compress.dll</c> and <c>Encrypt.dll</c>.
/// The vendor source tree is named <c>BRCloudv2</c> per the embedded PDB
/// paths <c>E:\BRCloudv2\src\ImgFile\*.cpp</c> recovered from the binaries.
/// </summary>
public static class AomeiConstants {

  /// <summary>5-byte ASCII signature <c>BIFH\</c> ("Backup Image File Header").
  /// Bytes <c>0x42 0x49 0x46 0x48 0x5C</c>. Doubles as the family-detection
  /// magic for both <c>.adi</c> and <c>.afi</c>.</summary>
  public static readonly byte[] BifhMagicAscii = [0x42, 0x49, 0x46, 0x48, 0x5C];

  /// <summary>Four-byte little-endian <c>'BIFH'</c> = 0x48464942 — the
  /// <see cref="BrFileHead.Flag"/> field at offset 0 of the head struct.
  /// Per <c>ImgFile.dll!ImageFile.cpp</c> assert <c>Head.Flag=='HFIB'</c>
  /// (the stored u32 is reversed in the assert text because of x86 little-
  /// endian display).</summary>
  public const uint BifhFlag = 0x48464942u;

  /// <summary>Four-byte little-endian <c>'BIFT'</c> = 0x54464942 — the
  /// <see cref="BrFileTail.Flag"/> field at offset 0 of the tail struct.
  /// Per <c>ImgFile.dll!ImageFile.cpp</c> assert <c>Tail.Flag=='TFIB'</c>.</summary>
  public const uint BiftFlag = 0x54464942u;

  /// <summary><c>BR_IMAGE_FILE_HEAD</c> size: 0x65C (1628) bytes. Verified at
  /// the <c>ASSERT(Head.Size == sizeof(BR_IMAGE_FILE_HEAD))</c> check at
  /// <c>ImgFile.dll!ImageFile.cpp</c> assert site (cmp r/m32 imm32 = 0x65C
  /// observed at the assert preamble).</summary>
  public const int BifhSize = 0x65C;

  /// <summary><c>BR_IMAGE_FILE_TAIL</c> size: 0x674 (1652) bytes. Verified at
  /// the matching <c>ASSERT(Tail.Size == sizeof(BR_IMAGE_FILE_TAIL))</c>
  /// (cmp r/m32 imm32 = 0x674 observed at the assert preamble).</summary>
  public const int BiftSize = 0x674;

  /// <summary>Size of the <c>BR_STANDARD_HEADER</c> tagged-record prefix
  /// shared by the file head, file tail and every INFO / INDEX record. This
  /// codebase currently emits + reads a compatible 12-byte alias of the
  /// vendor's 16-byte layout (the 4-byte Reserved trailer is omitted), so
  /// sealed-CRC records round-trip through our own reader. See
  /// <see cref="VendorStandardHeaderSize"/> for the verified vendor value
  /// and the <see cref="BrStandardHeader"/> XML docs for the layout
  /// rationale.</summary>
  public const int StandardHeaderSize = 12;

  /// <summary>Vendor-documented size of <c>BR_STANDARD_HEADER</c>: 16 bytes
  /// per the disassembled assert preamble <c>cmp ecx, 0x10</c> immediately
  /// before the <c>Length&gt;=sizeof(BR_STANDARD_HEADER)</c> assert string
  /// at <c>ImgFile.dll!FlbImageWriter.cpp</c>. The on-disk struct layout is:
  /// <code>
  /// struct BR_STANDARD_HEADER {
  ///   uint32_t Type;     // offset 0 — INFO_TYPE_* / INDEX_TYPE_* tag (also the
  ///                      //           BIFH / BIFT magic for the file head / tail)
  ///   uint32_t Size;     // offset 4 — total record bytes INCLUDING this header
  ///   uint32_t Crc32;    // offset 8 — zlib CRC32 over the record with this field zeroed
  ///   uint32_t Reserved; // offset 12 — observed-zero in every recovered sample
  /// };
  /// </code>
  /// Pinned here so future wire-compat work can swap our 12-byte alias to
  /// the 16-byte vendor layout without losing the recovered fact.</summary>
  public const int VendorStandardHeaderSize = 16;

  /// <summary>Offset within <see cref="StandardHeaderSize"/> of the
  /// <c>Crc32</c> field. Used by the verifier to zero it before
  /// re-computing. Same offset in both the current 12-byte alias and the
  /// 16-byte vendor layout.</summary>
  public const int Crc32FieldOffset = 8;

  // ─── INFO_TYPE_* enumeration ────────────────────────────────────────────
  // All confirmed via numeric tag tracing in ImgFile.dll: the writer stores
  // each tag into the BR_STANDARD_HEADER on the stack with `mov [stack], imm32`
  // or pushes it as the 4th cdecl arg to GetImageInfo / AddImageInfo. The
  // numeric value was matched back to the assert string by xref'ing the
  // assert site.

  /// <summary><c>INFO_TYPE_IMAGE_COMPRESS</c> = 0x105 — 0x18-byte record
  /// carrying the compress method + level. Confirmed by the writer-side
  /// stack-built header in <c>AddImageInfo(INFO_TYPE_IMAGE_COMPRESS, ...)</c>.</summary>
  public const ushort InfoTypeImageCompress = 0x0105;

  /// <summary><c>INFO_TYPE_IMAGE_ENCRYPT</c> = 0x106 — 0x18-byte record
  /// carrying the encrypt method + key length. Confirmed by the writer-side
  /// stack-built header in <c>AddImageInfo(INFO_TYPE_IMAGE_ENCRYPT, ...)</c>.</summary>
  public const ushort InfoTypeImageEncrypt = 0x0106;

  /// <summary><c>INFO_TYPE_IMAGE_PASSWORD</c> = 0x107 — 0x20-byte record
  /// carrying MD5(UTF-16LE(password)). Confirmed at
  /// <c>AddImageInfo(INFO_TYPE_IMAGE_PASSWORD, &amp;Psw, sizeof(Psw))</c>.</summary>
  public const ushort InfoTypeImagePassword = 0x0107;

  /// <summary><c>INFO_TYPE_IMAGE_SPLIT_SIZE</c> = 0x104 — record carrying
  /// the split-volume size threshold for multi-file backups. Confirmed by
  /// the writer-side stack-built header at
  /// <c>AddImageInfo(INFO_TYPE_IMAGE_SPLIT_SIZE, &amp;Split, sizeof(Split))</c>
  /// emitting a 0x18-byte record with Type=0x104.</summary>
  public const ushort InfoTypeImageSplitSize = 0x0104;

  /// <summary><c>INFO_TYPE_IMAGE_COMMENT</c> = 0x108 — variable-size record
  /// carrying a UTF-16LE comment string. Confirmed by the writer-side call
  /// <c>AddImageInfo(INFO_TYPE_IMAGE_COMMENT, pStruct, Size)</c> pushing
  /// 0x108 immediately before the vtable call.</summary>
  public const ushort InfoTypeImageComment = 0x0108;

  /// <summary><c>INFO_TYPE_BACKUP_TYPE</c> = 0x10C — 0x14-byte record
  /// <c>{Type=0x10C, Size, Crc32, Reserved, kind:u32}</c>.</summary>
  public const ushort InfoTypeBackupType = 0x010C;

  /// <summary><c>INFO_TYPE_BACKUP_TIME</c> = 0x10B — record carrying the
  /// backup-creation timestamp. Numeric value recovered from the
  /// <c>m_pReader-&gt;GetImageInfo(0, &amp;BackupTime, uLen, INFO_TYPE_BACKUP_TIME)</c>
  /// callsite (cdecl push 0x10B).</summary>
  public const ushort InfoTypeBackupTime = 0x010B;

  /// <summary><c>INFO_TYPE_BACKUP_OPTION</c> = 0x10D — record carrying the
  /// disk-level backup options struct. Confirmed via cdecl push at the
  /// <c>GetImageInfo(0, &amp;BakOp, uLen, INFO_TYPE_BACKUP_OPTION)</c> callsite.</summary>
  public const ushort InfoTypeBackupOption = 0x010D;

  /// <summary><c>INFO_TYPE_DISK_INFO</c> = 0x102 — record carrying a
  /// <c>BASIC_DISK_INFO_EX</c> / <c>DDM_DISK_INFO_EX</c> struct. Confirmed
  /// via cdecl push at the <c>GetImageInfo(i, pDisk, uLen, INFO_TYPE_DISK_INFO)</c>
  /// callsite (push 0x102 immediately before <c>call [edx+0x2C]</c>).</summary>
  public const ushort InfoTypeDiskInfo = 0x0102;

  /// <summary><c>INFO_TYPE_VOLUME_INFO</c> = 0x103 — record carrying a
  /// <c>PART_INFO_EX</c> / <c>DDM_VOLUME_INFO</c> struct. Confirmed via
  /// virtual-call push at the
  /// <c>pVol-&gt;GetVolumeInfo(0, pPart, uLen, INFO_TYPE_VOLUME_INFO)</c>
  /// callsite (push 0x103 immediately before <c>call [eax+0x14]</c>).</summary>
  public const ushort InfoTypeVolumeInfo = 0x0103;

  /// <summary><c>INFO_TYPE_FLB_BACKUP_OPTION</c> = 0x113 — file-level
  /// backup option record. Recovered via cdecl push at
  /// <c>GetImageInfo(0, &amp;BackupOpt, uLen, INFO_TYPE_FLB_BACKUP_OPTION)</c>.</summary>
  public const ushort InfoTypeFlbBackupOption = 0x0113;

  /// <summary><c>INFO_TYPE_FLB_BACKUP_OPTION_EX</c> = 0x116 — extended
  /// file-level backup option record. Recovered via cdecl push at the
  /// corresponding GetImageInfo callsite.</summary>
  public const ushort InfoTypeFlbBackupOptionEx = 0x0116;

  /// <summary><c>INFO_TYPE_FLB_PATH_LIST</c> = 0x112 — list of backed-up
  /// source paths.</summary>
  public const ushort InfoTypeFlbPathList = 0x0112;

  // ─── INDEX_TYPE_* enumeration ───────────────────────────────────────────
  // All five values confirmed by the assert preamble pattern
  // `cmp dword ptr [reg+disp], imm32` immediately preceding each
  // `INDEX_TYPE_xxx==Head.Type` assert in ImgFile.dll.

  /// <summary><c>INDEX_TYPE_ROOT</c> = 0x200 — root-level index node
  /// containing a <c>SubList</c> of <c>(Offset, Size, Type)</c> tuples
  /// referencing every other top-level record (INFO records + the
  /// per-volume / per-file-tree sub-indices). Recovered at the assert
  /// <c>INDEX_TYPE_ROOT==Head.Type</c> with <c>cmp [reg+disp], 0x200</c>
  /// preamble.</summary>
  public const ushort IndexTypeRoot = 0x0200;

  /// <summary><c>INDEX_TYPE_VOLUME</c> = 0x201 — per-volume index node
  /// holding a <c>SubList</c> of regions and per-volume INFO records.
  /// Recovered at the matching <c>cmp [reg+disp], 0x201</c> preamble.</summary>
  public const ushort IndexTypeVolume = 0x0201;

  /// <summary><c>INDEX_TYPE_DATABLOCK</c> = 0x202 — index of sector-data
  /// blocks for disk / partition (.adi) backups. Holds a packed array of
  /// <see cref="VendorVdbEntrySize"/>-byte <c>BR_IMAGE_INDEX_ENTRY_VDB</c>
  /// entries (RegNo, BlockNo, ImgOffset, NewSize, OldSize, Crc32).
  /// Recovered at the assert <c>INDEX_TYPE_DATABLOCK==Head.Type</c> with
  /// the <c>cmp [reg+disp], 0x202</c> preamble.</summary>
  public const ushort IndexTypeDataBlock = 0x0202;

  /// <summary><c>INDEX_TYPE_DIRTREE</c> = 0x300 — file-level directory
  /// tree index for .afi backups. Recovered at the matching
  /// <c>cmp [reg+disp], 0x300</c> preamble at the assert site
  /// <c>pHead-&gt;Type==INDEX_TYPE_DIRTREE</c>.</summary>
  public const ushort IndexTypeDirTree = 0x0300;

  /// <summary><c>INDEX_TYPE_DATAAREA</c> = 0x301 — file-level data-area
  /// index for .afi backups. Holds a packed array of
  /// <c>BR_IMAGE_INDEX_ENTRY_FDB</c> entries (BlockNo, ImgOffset, NewSize,
  /// OldSize, Crc32 — no RegNo because there's only one logical "region"
  /// for file backups). Recovered at the assert
  /// <c>INDEX_TYPE_DATAAREA==Head.Type</c> with the
  /// <c>cmp [reg+disp], 0x301</c> preamble.</summary>
  public const ushort IndexTypeDataArea = 0x0301;

  // ─── BR_IMAGE_INDEX header layout (after BR_STANDARD_HEADER) ────────────
  //
  // Per the assert `pIndex->EntrySize==sizeof(BR_IMAGE_INDEX_ENTRY_VDB)` and
  // the cmp preamble `cmp dword ptr [edx+0x18], 0x20` at the same callsite,
  // the index header has:
  //   offset 0x00..0x0F : BR_STANDARD_HEADER (16 bytes)
  //   offset 0x10..0x13 : ??? (reserved / observed-zero in passive RE)
  //   offset 0x14..0x17 : EntryCount (u32)
  //   offset 0x18..0x1B : EntrySize  (u32) - 0x20 for VDB, unknown for FDB
  //   offset 0x1C+      : EntryCount * EntrySize bytes of entry payload
  // Total BR_IMAGE_INDEX header before entries = 28 bytes (0x1C).

  /// <summary>Byte offset of the <c>EntryCount</c> field within a
  /// <c>BR_IMAGE_INDEX</c> record (relative to the start of the record,
  /// i.e. including the 16-byte BR_STANDARD_HEADER). Recovered from the
  /// writer pattern <c>mov [esi+0x14], EntryCount</c>.</summary>
  public const int VendorIndexEntryCountOffset = 0x14;

  /// <summary>Byte offset of the <c>EntrySize</c> field within a
  /// <c>BR_IMAGE_INDEX</c> record. Recovered from the reader pattern
  /// <c>cmp dword ptr [edx+0x18], 0x20</c> at the EntrySize assert.</summary>
  public const int VendorIndexEntrySizeOffset = 0x18;

  /// <summary>Byte offset within a <c>BR_IMAGE_INDEX</c> record where the
  /// packed entry array begins (i.e. immediately after the EntrySize field).
  /// Equal to <see cref="VendorIndexEntrySizeOffset"/> + 4.</summary>
  public const int VendorIndexEntriesOffset = 0x1C;

  /// <summary>Size of a single <c>BR_IMAGE_INDEX_ENTRY_VDB</c> volume-data
  /// block descriptor: 0x20 (32) bytes. Recovered from the
  /// <c>cmp [edx+0x18], 0x20</c> preamble before the
  /// <c>EntrySize==sizeof(BR_IMAGE_INDEX_ENTRY_VDB)</c> assert.</summary>
  public const int VendorVdbEntrySize = 0x20;

  // ─── BR_IMAGE_FILE_TAIL body fields (partial) ───────────────────────────
  //
  // The tail body holds split-volume bookkeeping. The reader accesses
  // `m_Tail.DataOffInSet` (u64) and `m_Tail.DataLenInSet` (u64) when
  // computing where this volume's payload lives in the full image set.
  // Their byte offsets within the 0x674-byte tail are not pinned by
  // passive RE because the field accesses happen via a typed C++ struct
  // with full layout knowledge, not via numeric `disp` immediates.

  /// <summary>True when the <c>BR_IMAGE_FILE_TAIL</c> body is known to
  /// contain at minimum a <c>DataOffInSet</c> (u64) and a
  /// <c>DataLenInSet</c> (u64) field — used by the reader to map this
  /// volume's logical position within a multi-file split image set.
  /// Pinned for awareness; byte offsets remain undetermined.</summary>
  public const bool TailBodyHasDataOffInSet = true;

  // ─── Compress / encrypt method codes ────────────────────────────────────

  /// <summary>Compress method codes recovered from the <c>BRCompress</c>
  /// dispatch in <c>Compress.dll!FUN_180001040</c>. The numeric mapping for
  /// LZ4 vs zlib is only proven by the threshold check
  /// <c>method &gt;= 0x1000B</c> selecting the zlib path; treat unknown
  /// values as opaque.</summary>
  public const uint CompressMethodNone = 0;
  /// <summary>LZ4 raw-block compressor — the small-buffer path.</summary>
  public const uint CompressMethodLz4 = 1;
  /// <summary>Threshold above which the zlib inflate path is selected.</summary>
  public const uint CompressMethodZlibThreshold = 0x1000B;

  /// <summary>UTF-16 magic string that, when MD5-substituted via the
  /// scheduled-task context, lets the AOMEI service decrypt unattended
  /// backups. The literal misspelling ("Schdule") is preserved from the
  /// binary at <c>ImgFile.dll!18006baa0</c>.</summary>
  public const string SchedulerMagicPassword = "AomeiTech.SchduleTask";

  // ─── Identified C++ source-tree paths ───────────────────────────────────

  /// <summary>The vendor codename for the source tree from which AOMEI
  /// Backupper's image-format library is built. Embedded in the installer
  /// as <c>BRCloudv2</c> and in <c>ImgFile.dll</c>'s PDB path
  /// <c>E:\BRCloudv2\src\ImgFile\ImageFile.cpp</c> alongside other
  /// <c>BRCloudv2\src\ImgFile\</c> source files (BlockContainer.cpp,
  /// BrFileWin.cpp, DataConvert.cpp, DsImgTask.cpp, FlbDataRegion.cpp,
  /// FlbDirEntry.cpp, FlbFileRegion.cpp, FlbImage.cpp, FlbImageReader.cpp,
  /// FlbImageWriter.cpp, FlbImgTask.cpp, Image.cpp, ImageFile.cpp,
  /// ImageFileSet.cpp, ImageReader.cpp, ImageReaderHelp.cpp,
  /// ImageVolume.cpp, ImageWriter.cpp, ImageWriterHelp.cpp, ImgTaskMgr.cpp,
  /// ImgWriteCache.cpp).</summary>
  public const string VendorSourceTreeCodename = "BRCloudv2";
}

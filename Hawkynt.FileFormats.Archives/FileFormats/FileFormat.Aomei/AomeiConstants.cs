#pragma warning disable CS1591
namespace FileFormat.Aomei;

/// <summary>
/// Wire-format constants for the AOMEI Backupper image format.
/// </summary>
public static class AomeiConstants {

  /// <summary>5-byte ASCII signature <c>BIFH\</c> ("Backup Image File Header").
  /// Bytes <c>0x42 0x49 0x46 0x48 0x5C</c>. Doubles as the family-detection
  /// magic for both <c>.adi</c> and <c>.afi</c>.</summary>
  public static readonly byte[] BifhMagicAscii = [0x42, 0x49, 0x46, 0x48, 0x5C];

  /// <summary>Four-byte little-endian <c>'BIFH'</c> = 0x48464942 — the
  /// <see cref="BrFileHead.Flag"/> field at offset 0 of the head struct
  /// (read as a big-endian character constant it spells <c>'HFIB'</c>).</summary>
  public const uint BifhFlag = 0x48464942u;

  /// <summary>Four-byte little-endian <c>'BIFT'</c> = 0x54464942 — the
  /// <see cref="BrFileTail.Flag"/> field at offset 0 of the tail struct.</summary>
  public const uint BiftFlag = 0x54464942u;

  /// <summary><c>BR_IMAGE_FILE_HEAD</c> size: 0x65C (1628) bytes; the head's
  /// <c>Size</c> field must equal this value.</summary>
  public const int BifhSize = 0x65C;

  /// <summary><c>BR_IMAGE_FILE_TAIL</c> size: 0x674 (1652) bytes; the tail's
  /// <c>Size</c> field must equal this value.</summary>
  public const int BiftSize = 0x674;

  /// <summary>Size of the <c>BR_STANDARD_HEADER</c> tagged-record prefix
  /// shared by the file head, file tail and every INFO / INDEX record. This
  /// codebase currently emits + reads a compatible 12-byte alias of the
  /// vendor's 16-byte layout (the 4-byte Reserved trailer is omitted), so
  /// sealed-CRC records round-trip through our own reader. See
  /// <see cref="VendorStandardHeaderSize"/> for the vendor value
  /// and the <see cref="BrStandardHeader"/> XML docs for the layout
  /// rationale.</summary>
  public const int StandardHeaderSize = 12;

  /// <summary>Vendor size of <c>BR_STANDARD_HEADER</c>: 16 bytes; a record
  /// shorter than this is invalid. The on-disk struct layout is:
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
  /// the 16-byte vendor layout without losing the fact.</summary>
  public const int VendorStandardHeaderSize = 16;

  /// <summary>Offset within <see cref="StandardHeaderSize"/> of the
  /// <c>Crc32</c> field. Used by the verifier to zero it before
  /// re-computing. Same offset in both the current 12-byte alias and the
  /// 16-byte vendor layout.</summary>
  public const int Crc32FieldOffset = 8;

  // ─── INFO_TYPE_* enumeration ────────────────────────────────────────────

  /// <summary><c>INFO_TYPE_IMAGE_COMPRESS</c> = 0x105 — 0x18-byte record
  /// carrying the compress method + level.</summary>
  public const ushort InfoTypeImageCompress = 0x0105;

  /// <summary><c>INFO_TYPE_IMAGE_ENCRYPT</c> = 0x106 — 0x18-byte record
  /// carrying the encrypt method + key length.</summary>
  public const ushort InfoTypeImageEncrypt = 0x0106;

  /// <summary><c>INFO_TYPE_IMAGE_PASSWORD</c> = 0x107 — 0x20-byte record
  /// carrying MD5(UTF-16LE(password)).</summary>
  public const ushort InfoTypeImagePassword = 0x0107;

  /// <summary><c>INFO_TYPE_IMAGE_SPLIT_SIZE</c> = 0x104 — 0x18-byte record
  /// carrying the split-volume size threshold for multi-file backups.</summary>
  public const ushort InfoTypeImageSplitSize = 0x0104;

  /// <summary><c>INFO_TYPE_IMAGE_COMMENT</c> = 0x108 — variable-size record
  /// carrying a UTF-16LE comment string.</summary>
  public const ushort InfoTypeImageComment = 0x0108;

  /// <summary><c>INFO_TYPE_BACKUP_TYPE</c> = 0x10C — 0x14-byte record
  /// <c>{Type=0x10C, Size, Crc32, Reserved, kind:u32}</c>.</summary>
  public const ushort InfoTypeBackupType = 0x010C;

  /// <summary><c>INFO_TYPE_BACKUP_TIME</c> = 0x10B — record carrying the
  /// backup-creation timestamp.</summary>
  public const ushort InfoTypeBackupTime = 0x010B;

  /// <summary><c>INFO_TYPE_BACKUP_OPTION</c> = 0x10D — record carrying the
  /// disk-level backup options struct.</summary>
  public const ushort InfoTypeBackupOption = 0x010D;

  /// <summary><c>INFO_TYPE_DISK_INFO</c> = 0x102 — record carrying a
  /// <c>BASIC_DISK_INFO_EX</c> / <c>DDM_DISK_INFO_EX</c> struct.</summary>
  public const ushort InfoTypeDiskInfo = 0x0102;

  /// <summary><c>INFO_TYPE_VOLUME_INFO</c> = 0x103 — record carrying a
  /// <c>PART_INFO_EX</c> / <c>DDM_VOLUME_INFO</c> struct.</summary>
  public const ushort InfoTypeVolumeInfo = 0x0103;

  /// <summary><c>INFO_TYPE_FLB_BACKUP_OPTION</c> = 0x113 — file-level
  /// backup option record.</summary>
  public const ushort InfoTypeFlbBackupOption = 0x0113;

  /// <summary><c>INFO_TYPE_FLB_BACKUP_OPTION_EX</c> = 0x116 — extended
  /// file-level backup option record.</summary>
  public const ushort InfoTypeFlbBackupOptionEx = 0x0116;

  /// <summary><c>INFO_TYPE_FLB_PATH_LIST</c> = 0x112 — list of backed-up
  /// source paths.</summary>
  public const ushort InfoTypeFlbPathList = 0x0112;

  /// <summary><c>INFO_TYPE_FLB_SUB_ENTRY_LIST</c> = 0x110 — file-level sub-
  /// entry list. Resolves the lower half of the prior
  /// <c>{0x110, 0x111, 0x128}</c> candidate set.</summary>
  public const ushort InfoTypeFlbSubEntryList = 0x0110;

  /// <summary><c>INFO_TYPE_FLB_FILE_DATA_BLOCK_LIST</c> = 0x111 — file-level
  /// data-block list. Resolves the upper half of the prior
  /// <c>{0x110, 0x111, 0x128}</c> candidate set.</summary>
  public const ushort InfoTypeFlbFileDataBlockList = 0x0111;

  /// <summary><c>INFO_TYPE_VOLUME_DATA_REGION</c> = 0x109 — per-region
  /// volume-data record carrying a <c>BR_IMAGE_INFO_VOLUME_DATA_REGION</c>
  /// (<see cref="VendorVolumeDataRegionSize"/> bytes).</summary>
  public const ushort InfoTypeVolumeDataRegion = 0x0109;

  /// <summary><c>sizeof(BR_IMAGE_INFO_VOLUME_DATA_REGION)</c> = 0x30 (48)
  /// bytes.</summary>
  public const int VendorVolumeDataRegionSize = 0x30;

  // ─── INDEX_TYPE_* enumeration ───────────────────────────────────────────

  /// <summary><c>INDEX_TYPE_ROOT</c> = 0x200 — root-level index node
  /// containing a <c>SubList</c> of <c>(Offset, Size, Type)</c> tuples
  /// referencing every other top-level record (INFO records + the
  /// per-volume / per-file-tree sub-indices).</summary>
  public const ushort IndexTypeRoot = 0x0200;

  /// <summary><c>INDEX_TYPE_VOLUME</c> = 0x201 — per-volume index node
  /// holding a <c>SubList</c> of regions and per-volume INFO records.</summary>
  public const ushort IndexTypeVolume = 0x0201;

  /// <summary><c>INDEX_TYPE_DATABLOCK</c> = 0x202 — index of sector-data
  /// blocks for disk / partition (.adi) backups. Holds a packed array of
  /// <see cref="VendorVdbEntrySize"/>-byte <c>BR_IMAGE_INDEX_ENTRY_VDB</c>
  /// entries (RegNo, BlockNo, ImgOffset, NewSize, OldSize, Crc32).</summary>
  public const ushort IndexTypeDataBlock = 0x0202;

  /// <summary><c>INDEX_TYPE_DIRTREE</c> = 0x300 — file-level directory
  /// tree index for .afi backups.</summary>
  public const ushort IndexTypeDirTree = 0x0300;

  /// <summary><c>INDEX_TYPE_DATAAREA</c> = 0x301 — file-level data-area
  /// index for .afi backups. Holds a packed array of
  /// <c>BR_IMAGE_INDEX_ENTRY_FDB</c> entries (BlockNo, ImgOffset, NewSize,
  /// OldSize, Crc32 — no RegNo because there's only one logical "region"
  /// for file backups).</summary>
  public const ushort IndexTypeDataArea = 0x0301;

  // ─── BR_IMAGE_INDEX header layout (after BR_STANDARD_HEADER) ────────────
  //
  //   offset 0x00..0x0F : BR_STANDARD_HEADER (16 bytes)
  //   offset 0x10..0x13 : ??? (reserved / observed-zero)
  //   offset 0x14..0x17 : EntryCount (u32)
  //   offset 0x18..0x1B : EntrySize  (u32) - 0x20 for VDB, unknown for FDB
  //   offset 0x1C+      : EntryCount * EntrySize bytes of entry payload
  // Total BR_IMAGE_INDEX header before entries = 28 bytes (0x1C).

  /// <summary>Byte offset of the <c>EntryCount</c> field within a
  /// <c>BR_IMAGE_INDEX</c> record (relative to the start of the record,
  /// i.e. including the 16-byte BR_STANDARD_HEADER).</summary>
  public const int VendorIndexEntryCountOffset = 0x14;

  /// <summary>Byte offset of the <c>EntrySize</c> field within a
  /// <c>BR_IMAGE_INDEX</c> record.</summary>
  public const int VendorIndexEntrySizeOffset = 0x18;

  /// <summary>Byte offset within a <c>BR_IMAGE_INDEX</c> record where the
  /// packed entry array begins (i.e. immediately after the EntrySize field).
  /// Equal to <see cref="VendorIndexEntrySizeOffset"/> + 4.</summary>
  public const int VendorIndexEntriesOffset = 0x1C;

  /// <summary>Size of a single <c>BR_IMAGE_INDEX_ENTRY_VDB</c> volume-data
  /// block descriptor: 0x20 (32) bytes — the <c>EntrySize</c> of a
  /// data-block index.</summary>
  public const int VendorVdbEntrySize = 0x20;

  // ─── Shipped BR_IMAGE_INDEX layout (12-byte standard-header alias) ──────
  //
  // The vendor's BR_IMAGE_INDEX header is 0x1C bytes (16-byte STD header +
  // Reserved + EntryCount + EntrySize), but this codebase ships a wire-
  // compatible 12-byte alias of BR_STANDARD_HEADER (see
  // <see cref="StandardHeaderSize"/>), so the index layout in containers
  // emitted by <see cref="AomeiWriter"/> is shifted by 4 bytes vs vendor:
  //
  //   offset 0x00..0x0B : BR_STANDARD_HEADER (12 bytes shipped alias)
  //   offset 0x0C..0x0F : Reserved (u32, observed-zero, kept for symmetry
  //                       with the vendor layout)
  //   offset 0x10..0x13 : EntryCount (u32, little-endian)
  //   offset 0x14..0x17 : EntrySize  (u32, little-endian)
  //   offset 0x18+      : EntryCount * EntrySize bytes of VDB payload
  //
  // Total BR_IMAGE_INDEX header before entries = 24 bytes (0x18). These
  // offsets pin the byte positions the
  // <see cref="AomeiInPlaceModifier"/> patches on every append.

  /// <summary>Byte offset of the <c>EntryCount</c> field within a
  /// shipped <c>BR_IMAGE_INDEX</c> record (relative to the start of the
  /// record, using this codebase's 12-byte BR_STANDARD_HEADER alias).
  /// Shifted -4 bytes vs the 16-byte vendor layout: vendor pin at +0x14
  /// becomes shipped pin at +0x10.</summary>
  public const int ShippedIndexEntryCountOffset = 0x10;

  /// <summary>Byte offset of the <c>EntrySize</c> field within a shipped
  /// <c>BR_IMAGE_INDEX</c> record. Shifted -4 bytes vs vendor: vendor pin
  /// at +0x18 becomes shipped pin at +0x14.</summary>
  public const int ShippedIndexEntrySizeOffset = 0x14;

  /// <summary>Byte offset within a shipped <c>BR_IMAGE_INDEX</c> record
  /// where the packed VDB entry array begins. Shifted -4 bytes vs vendor:
  /// vendor pin at +0x1C becomes shipped pin at +0x18.</summary>
  public const int ShippedIndexEntriesOffset = 0x18;

  /// <summary>Total bytes the shipped <c>BR_IMAGE_INDEX</c> header
  /// occupies before its entry array. Equal to
  /// <see cref="ShippedIndexEntriesOffset"/>.</summary>
  public const int ShippedIndexHeaderSize = ShippedIndexEntriesOffset;

  /// <summary>Sentinel <c>NewSize</c> value marking a tombstone VDB
  /// entry. The <see cref="AomeiInPlaceModifier"/> appends a tombstone
  /// (sharing the target's <c>RegNo</c>) on Remove; the reader's
  /// latest-entry-wins gate drops the chunk from the live entry view.
  /// Picked at <c>0xFFFFFFFFu</c> because real VDB entries can never
  /// legitimately carry it (the payload would be 4 GiB) and the value
  /// round-trips losslessly through any tooling that treats the field
  /// as opaque u32.</summary>
  public const uint TombstoneNewSizeSentinel = 0xFFFFFFFFu;

  // ─── BR_IMAGE_INDEX_ENTRY_VDB field byte offsets ───────────────────────
  //
  //   0x00 RegNo     u32
  //   0x04 BlockNo   u64
  //   0x0C ImgOffset u64
  //   0x14 OldSize   u32 (decoded payload size)
  //   0x18 NewSize   u32 (stored payload size; equals OldSize when uncompressed)
  //   0x1C Crc32     u32 (BRCrc32 over the decoded payload)

  /// <summary>Byte offset of <c>RegNo</c> (u32) within a
  /// <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x00.</summary>
  public const int VendorVdbEntryRegNoOffset = 0x00;

  /// <summary>Byte offset of <c>BlockNo</c> (u64) within a
  /// <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x04 (spanning 0x04..0x0B).</summary>
  public const int VendorVdbEntryBlockNoOffset = 0x04;

  /// <summary>Byte offset of <c>ImgOffset</c> (u64) within a
  /// <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x0C (spanning 0x0C..0x13).</summary>
  public const int VendorVdbEntryImgOffsetOffset = 0x0C;

  /// <summary>Byte offset of <c>OldSize</c> (u32, decoded payload size)
  /// within a <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x14. The decoded payload
  /// length must equal this value.</summary>
  public const int VendorVdbEntryOldSizeOffset = 0x14;

  /// <summary>Byte offset of <c>NewSize</c> (u32, compressed/stored payload
  /// size) within a <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x18. When
  /// <c>NewSize == OldSize</c> the payload is stored without compression.</summary>
  public const int VendorVdbEntryNewSizeOffset = 0x18;

  /// <summary>Byte offset of <c>Crc32</c> (u32, BRCrc32 over the decoded
  /// payload) within a <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x1C.</summary>
  public const int VendorVdbEntryCrc32Offset = 0x1C;

  // ─── BR_IMAGE_FILE_TAIL body fields ────────────────────────────────────
  //
  //   0x620 DataLenInSet u64
  //   0x628 DataOffInSet u64
  //   0x664 trailing BR_STANDARD_HEADER (Reserved, Crc32, Size, Flag)

  /// <summary>Byte offset of <c>DataLenInSet</c> (u64) within the
  /// 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x620.</summary>
  public const int VendorTailBodyDataLenInSetOffset = 0x620;

  /// <summary>Byte offset of <c>DataOffInSet</c> (u64) within the
  /// 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x628. A logical image-set
  /// offset maps to the per-volume file offset
  /// <c>Offset - DataOffInSet + sizeof(BR_IMAGE_FILE_HEAD)</c>.</summary>
  public const int VendorTailBodyDataOffInSetOffset = 0x628;

  // ─── BR_IMAGE_FILE_TAIL trailing BR_STANDARD_HEADER position ───────────
  //
  // The tail's BR_STANDARD_HEADER appears AT THE END of the 0x674-byte
  // struct (vs the file head where it sits at offset 0):
  //
  //   Flag 'BIFT' at buffer offset 0x670
  //   Size 0x674  at buffer offset 0x66c
  //   Crc32       at buffer offset 0x668 (computed with the field zeroed)
  //
  // The 4-byte slot at 0x664 is the Reserved word (observed zero; mirrors
  // the head's documented Reserved field). Total trailing header = 16 bytes
  // at [0x664..0x673].

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Reserved word within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x664.
  /// Mirrors the head's documented Reserved offset; observed zero in every
  /// recovered sample but not validated (only the {CRC, Size, Flag} fields
  /// are checked).</summary>
  public const int VendorTailTrailerReservedOffset = 0x664;

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Crc32 field within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x668.
  /// The CRC is BRCrc32 over the whole 0x674 bytes with this field
  /// zeroed.</summary>
  public const int VendorTailTrailerCrc32Offset = 0x668;

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Size field within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x66C
  /// (value 0x674).</summary>
  public const int VendorTailTrailerSizeOffset = 0x66C;

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Flag field within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x670.
  /// Value 0x54464942 = 'BIFT' little-endian.</summary>
  public const int VendorTailTrailerFlagOffset = 0x670;

  /// <summary>True when the <c>BR_IMAGE_FILE_TAIL</c> body is known to
  /// contain at minimum a <c>DataOffInSet</c> (u64) and a
  /// <c>DataLenInSet</c> (u64) field — used by the reader to map this
  /// volume's logical position within a multi-file split image set.
  /// Pinned for awareness; byte offsets now also pinned via
  /// <see cref="VendorTailBodyDataOffInSetOffset"/> and
  /// <see cref="VendorTailBodyDataLenInSetOffset"/>.</summary>
  public const bool TailBodyHasDataOffInSet = true;

  // ─── Compress / encrypt method codes ────────────────────────────────────

  /// <summary>Compress method codes. The numeric mapping for LZ4 vs zlib is
  /// only proven by the threshold <c>method &gt;= 0x1000B</c> selecting the
  /// zlib path; treat unknown values as opaque.</summary>
  public const uint CompressMethodNone = 0;
  /// <summary>LZ4 raw-block compressor — the small-buffer path.</summary>
  public const uint CompressMethodLz4 = 1;
  /// <summary>Threshold above which the zlib inflate path is selected.</summary>
  public const uint CompressMethodZlibThreshold = 0x1000B;

  /// <summary>UTF-16 magic string that, when MD5-substituted via the
  /// scheduled-task context, lets the AOMEI service decrypt unattended
  /// backups. The literal misspelling ("Schdule") is part of the
  /// constant.</summary>
  public const string SchedulerMagicPassword = "AomeiTech.SchduleTask";
}

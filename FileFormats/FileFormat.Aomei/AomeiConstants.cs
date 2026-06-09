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

  /// <summary><c>INFO_TYPE_FLB_SUB_ENTRY_LIST</c> = 0x110 — file-level sub-
  /// entry list emitted by <c>FlbImageWriter</c>. Pinned by the cdecl
  /// <c>push 0x110</c> at <c>ImgFile.dll!0x1000a2d0</c>, fifteen
  /// instructions before the assert-text xref at <c>0x1000a2fd</c> to the
  /// AddInfo callsite string
  /// <c>AddInfo(INFO_TYPE_FLB_SUB_ENTRY_LIST, &amp;m_vSubEntList[0], ...)</c>.
  /// Resolves the lower half of the prior <c>{0x110, 0x111, 0x128}</c>
  /// candidate set.</summary>
  public const ushort InfoTypeFlbSubEntryList = 0x0110;

  /// <summary><c>INFO_TYPE_FLB_FILE_DATA_BLOCK_LIST</c> = 0x111 — file-level
  /// data-block list emitted by <c>FlbImageWriter</c>. Pinned by the cdecl
  /// <c>push 0x111</c> at <c>ImgFile.dll!0x1000a341</c>, fifteen
  /// instructions before the assert-text xref at <c>0x1000a36e</c> to
  /// <c>AddInfo(INFO_TYPE_FLB_FILE_DATA_BLOCK_LIST, &amp;m_vDataBlockList[0], ...)</c>.
  /// Resolves the upper half of the prior <c>{0x110, 0x111, 0x128}</c>
  /// candidate set.</summary>
  public const ushort InfoTypeFlbFileDataBlockList = 0x0111;

  /// <summary><c>INFO_TYPE_VOLUME_DATA_REGION</c> = 0x109 — per-region
  /// volume-data record carrying a <c>BR_IMAGE_INFO_VOLUME_DATA_REGION</c>
  /// (0x30 bytes per the matching <c>cmp eax, 0x30</c> preamble). Pinned by
  /// the loop-body <c>cmp dword ptr [ebp-0x34], 0x109</c> at
  /// <c>ImgFile.dll!ImageVolume.cpp+0x1002554c</c> immediately before the
  /// <c>jne</c> branch that targets the assert-text xref at
  /// <c>0x100257ae</c> for <c>Region.Header.Type==INFO_TYPE_VOLUME_DATA_REGION</c>.
  /// The same switch arm continues into the
  /// <c>uLen==sizeof(BR_IMAGE_INFO_VOLUME_DATA_REGION)</c> assert at
  /// <c>0x1002587a</c> guarded by <c>cmp eax, 0x30; jne 0x10025846</c>.</summary>
  public const ushort InfoTypeVolumeDataRegion = 0x0109;

  /// <summary>Vendor-pinned <c>sizeof(BR_IMAGE_INFO_VOLUME_DATA_REGION)</c>
  /// = 0x30 (48) bytes. Pinned by the <c>cmp eax, 0x30</c> preamble at
  /// <c>0x10025558</c> immediately before the corresponding sizeof assert
  /// at <c>0x1002587a</c>.</summary>
  public const int VendorVolumeDataRegionSize = 0x30;

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

  // ─── BR_IMAGE_INDEX_ENTRY_VDB field byte offsets ───────────────────────
  //
  // Pinned by triangulating three independent code paths in ImgFile.dll
  // (32-bit i386 build, sections .text at vma 0x10001000):
  //
  // (1) ReadBlock(this, u32, VDB) at vma 0x10026060: the function takes
  //     the VDB struct by value via 32 bytes pushed at [ebp+0xc..ebp+0x2b]
  //     and stages them locally as two xmm0 moves
  //     ([ebp-0x2c..ebp-0x1d] = vdb[0..0xf];
  //      [ebp-0x1c..ebp-0x0d] = vdb[0x10..0x1f]).
  //     The subsequent `psrldq xmm0, 0x4` at 0x1002608b then `movd eax,
  //     xmm0` at 0x10026090 extracts the dword at vdb[0x14] and pushes it
  //     as the malloc(OldSize) size for the pOld buffer. The first
  //     ReadData call at 0x100261ce pushes
  //     [ebp-0x20] / [ebp-0x1c] (= vdb[0x0c..0x13]) as the u64 ImgOffset.
  //
  // (2) Wrapper at vma 0x10027c91 iterates a VDB array via
  //     `shl eax, 5; add eax, [ecx+0x38]; movups xmm0, [eax];
  //      movups xmm0, [eax+0x10]; movaps [esp+0x50], xmm0` so [esp+0x50]
  //     mirrors vdb[0x10..0x1f]. The BRCrc32 call at 0x10027d02 takes
  //     [esp+0x54] (= vdb[0x14]) as the byte-count and the post-CRC
  //     `cmp eax, [esp+0x5c]` at 0x10027d0b compares the computed CRC
  //     against vdb[0x1c..0x1f]; the `jne 0x10027d99` branch lands on
  //     the assert-text xref `Crc32==vdb.Crc32`.
  //
  // (3) Outer GetBlock(vdb.RegNo, vdb.BlockNo, ...) prep at vma 0x10026d8a
  //     issues `movups xmm1, [edx+ecx]; movups xmm0, [edx+ecx+0x10];
  //      movd esi, xmm1; psrldq xmm0, 0x4; movd edx, xmm0;
  //      psrldq xmm1, 0x8; movd ecx, xmm1` then pushes esi/edx/ecx (in
  //     C-order) as the first three args of GetBlock. esi = vdb[0..3] is
  //     pushed first ⇒ first C arg ⇒ RegNo (u32 per the function
  //     signature); edx = vdb[4..7] is the low half of BlockNo; ecx =
  //     vdb[8..0xb] is the high half of BlockNo (u64).

  /// <summary>Byte offset of <c>RegNo</c> (u32) within a
  /// <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x00. Pinned by the GetBlock prep at
  /// <c>0x10026d8a</c>: `movd esi, xmm1` (where xmm1 = vdb[0..0xf]) gives
  /// vdb[0..3], pushed first in C-order ⇒ first arg of
  /// <c>GetBlock(vdb.RegNo, vdb.BlockNo, ...)</c>.</summary>
  public const int VendorVdbEntryRegNoOffset = 0x00;

  /// <summary>Byte offset of <c>BlockNo</c> (u64) within a
  /// <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x04. Pinned by the GetBlock prep at
  /// <c>0x10026d8a</c>: `psrldq xmm0, 4; movd edx, xmm0` extracts
  /// vdb[4..7] as BlockNo_low and `psrldq xmm1, 8; movd ecx, xmm1`
  /// extracts vdb[8..0xb] as BlockNo_high. Both pushed in the second/third
  /// C-order positions ⇒ a single u64 spanning 0x04..0x0B.</summary>
  public const int VendorVdbEntryBlockNoOffset = 0x04;

  /// <summary>Byte offset of <c>ImgOffset</c> (u64) within a
  /// <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x0C. Pinned by the first ReadData
  /// call at <c>0x100261ce</c> which pushes
  /// <c>[ebp-0x20]</c> and <c>[ebp-0x1c]</c> as the u64 ImgOffset. Those
  /// locals correspond to vdb[0x0C..0x0F] (low) and vdb[0x10..0x13]
  /// (high) per the function-prologue xmm copy at
  /// <c>0x10026070..10026085</c>.</summary>
  public const int VendorVdbEntryImgOffsetOffset = 0x0C;

  /// <summary>Byte offset of <c>OldSize</c> (u32, decoded payload size)
  /// within a <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x14. Pinned by two
  /// independent paths: (a) the malloc-of-pOld at <c>0x10026094</c> uses
  /// the dword at vdb[0x14] (extracted via `psrldq xmm0, 4; movd eax,
  /// xmm0` from the second xmm0 = vdb[0x10..0x1f]); (b) the post-Decode
  /// equality check at <c>0x10026254..0x1002625a</c>
  /// (`mov eax, [ebp-0x18]; cmp eax, [ebp-0x38]`) compares vdb[0x14]
  /// against the returned OldLen and the jne path leads to the assert
  /// <c>vdb.OldSize==OldLen</c>. [ebp-0x18] = vdb[0x14] per the prologue
  /// xmm layout.</summary>
  public const int VendorVdbEntryOldSizeOffset = 0x14;

  /// <summary>Byte offset of <c>NewSize</c> (u32, compressed/stored payload
  /// size) within a <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x18. Pinned by the
  /// pNew malloc at <c>0x100261b0..0x100261b3</c> (`push [ebp-0x14];
  /// call malloc`) followed by the ReadData call at <c>0x100261ce</c> that
  /// passes <c>lea ebx, [ebp-0x14]</c> as the in-out length pointer. Per
  /// the prologue layout [ebp-0x14] = vdb[0x18]. Verified by the
  /// pre-decode no-compression shortcut at <c>0x100262a1</c>:
  /// `mov eax, [ebp-0x14]; cmp eax, [ebp-0x18]` checks
  /// <c>vdb.NewSize == vdb.OldSize</c> before bypassing Decode.</summary>
  public const int VendorVdbEntryNewSizeOffset = 0x18;

  /// <summary>Byte offset of <c>Crc32</c> (u32, BRCrc32 over the decoded
  /// payload) within a <c>BR_IMAGE_INDEX_ENTRY_VDB</c>: 0x1C. Pinned by
  /// the post-BRCrc32 comparison at <c>0x10027d0b</c>: `cmp eax,
  /// [esp+0x5c]` — [esp+0x5c] = vdb[0x1c] per the wrapper-side
  /// `movaps [esp+0x50], xmm0` at <c>0x10027ca8</c> that mirrors the
  /// second VDB xmm half. The jne lands on the
  /// <c>Crc32==vdb.Crc32</c> assert-text xref at
  /// <c>0x10027d99</c>.</summary>
  public const int VendorVdbEntryCrc32Offset = 0x1C;

  // ─── BR_IMAGE_FILE_TAIL body fields ────────────────────────────────────
  //
  // Pinned by reader-side `mov reg, [reg+disp]` at the two access points
  // identified by the bounds-check assert text:
  //
  //   * Read path at vma 0x10017150 (m_pFile->Read with -DataOffInSet
  //     translation): mov ebx,[ecx+0xc8c] / mov edi,[ecx+0xc88] →
  //     m_Tail.DataOffInSet high/low at object offsets 0xc88..0xc8f.
  //   * Write path at vma 0x100172d4 (mirror) plus the upper-bound check
  //     at 0x10017342..0x1001734e (`add esi,[edi+0xc80] /
  //     mov ecx,[edi+0xc84] / adc ecx,eax`) → m_Tail.DataLenInSet high/low
  //     at object offsets 0xc80..0xc87.
  //   * m_Tail itself is at this+0x660 per the tail-load routine at
  //     0x10017a90: `rep movsd` at 0x10017cf3 copies the 0x674-byte tail
  //     buffer from [ebp-0x678] to [edi+0x660], so m_Tail occupies
  //     object offsets [0x660..0xcd4). Subtracting the base from each
  //     field's object offset yields the in-struct byte offset.

  /// <summary>Byte offset of <c>DataLenInSet</c> (u64) within the
  /// 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x620. Computed as
  /// <c>0xc80 - 0x660</c> from the read-side ALU pair
  /// <c>add esi,[edi+0xc80] / mov ecx,[edi+0xc84]</c> at <c>0x10017342</c>
  /// and the tail-base <c>[edi+0x660]</c> established by the
  /// <c>rep movsd</c> at <c>0x10017cf3</c>.</summary>
  public const int VendorTailBodyDataLenInSetOffset = 0x620;

  /// <summary>Byte offset of <c>DataOffInSet</c> (u64) within the
  /// 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x628. Computed as
  /// <c>0xc88 - 0x660</c> from the read-side load pair
  /// <c>mov ebx,[ecx+0xc8c] / mov edi,[ecx+0xc88]</c> at <c>0x100171b0</c>
  /// and the tail-base <c>[edi+0x660]</c>. The reader uses
  /// <c>Offset - DataOffInSet + sizeof(BR_IMAGE_FILE_HEAD)</c> to
  /// translate logical image-set offsets to per-volume file offsets.</summary>
  public const int VendorTailBodyDataOffInSetOffset = 0x628;

  // ─── BR_IMAGE_FILE_TAIL trailing BR_STANDARD_HEADER position ───────────
  //
  // The tail's BR_STANDARD_HEADER appears AT THE END of the 0x674-byte
  // struct (vs the file head where it sits at offset 0). Pinned by the
  // post-Read sanity checks at 0x10017b9a / 0x10017bfd / 0x10017c60:
  //
  //   `cmp dword [ebp-0x8],  'BIFT'`   ← Flag at buffer offset 0x670
  //   `cmp dword [ebp-0xc],  0x674`    ← Size at buffer offset 0x66c
  //   `mov esi, [ebp-0x10]; mov [ebp-0x10],0; call BRCrc32` ← CRC at 0x668
  //
  // The 4-byte slot at 0x664 is the Reserved word (unverified-zero by
  // passive RE; mirrors the head's documented Reserved field). Total
  // trailing header = 16 bytes at [0x664..0x673].

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Reserved word within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x664.
  /// Mirrors the head's documented Reserved offset; observed zero in every
  /// recovered sample but the cmp at this position is not explicit in the
  /// reader (only the {CRC, Size, Flag} fields are directly checked).</summary>
  public const int VendorTailTrailerReservedOffset = 0x664;

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Crc32 field within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x668.
  /// Pinned by the post-Read CRC verification block at
  /// <c>0x10017c60..0x10017c84</c>: the reader loads the stored value
  /// from <c>[ebp-0x10]</c> (= buffer offset 0x668), zeroes it, calls
  /// BRCrc32 over the whole 0x674 bytes, and compares against the
  /// pre-zero value.</summary>
  public const int VendorTailTrailerCrc32Offset = 0x668;

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Size field within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x66C.
  /// Pinned by <c>cmp dword ptr [ebp-0xc], 0x674</c> at <c>0x10017bfd</c>
  /// — [ebp-0xc] maps to buffer offset (0x678 - 0xc) = 0x66c.</summary>
  public const int VendorTailTrailerSizeOffset = 0x66C;

  /// <summary>Byte offset of the trailing <c>BR_STANDARD_HEADER</c>'s
  /// Flag field within the 0x674-byte <c>BR_IMAGE_FILE_TAIL</c>: 0x670.
  /// Pinned by <c>cmp dword ptr [ebp-0x8], 0x54464942</c> at
  /// <c>0x10017b9a</c> — [ebp-0x8] maps to buffer offset
  /// (0x678 - 0x8) = 0x670. Value 0x54464942 = 'BIFT' little-endian.</summary>
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

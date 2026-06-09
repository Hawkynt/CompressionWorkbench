#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Aomei;

/// <summary>
/// <c>BR_IMAGE_INDEX</c> — the index-record header layout that follows the
/// 16-byte <c>BR_STANDARD_HEADER</c> for <see cref="AomeiConstants.IndexTypeDataBlock"/>
/// (0x202), <see cref="AomeiConstants.IndexTypeDataArea"/> (0x301) and the
/// other <c>INDEX_TYPE_*</c> record families recovered from
/// <c>ImgFile.dll!ImageVolume.cpp</c>.
///
/// <para>
/// Layout (relative to the start of the record, i.e. including the
/// 16-byte vendor BR_STANDARD_HEADER):
/// <code>
/// struct BR_IMAGE_INDEX {
///   BR_STANDARD_HEADER Header;  // offset 0x00..0x0F  (16 bytes)
///   uint32_t Reserved;          // offset 0x10..0x13  (observed-zero)
///   uint32_t EntryCount;        // offset 0x14..0x17
///   uint32_t EntrySize;         // offset 0x18..0x1B  (= 0x20 for VDB entries)
///   // BR_IMAGE_INDEX_ENTRY_xxx Entries[EntryCount];   // offset 0x1C+
/// };
/// </code>
/// Pinned by the writer-side store <c>mov [esi+0x14], EntryCount</c> at the
/// DATABLOCK emit site and the reader-side <c>cmp [edx+0x18], 0x20</c>
/// preamble before the
/// <c>pIndex-&gt;EntrySize==sizeof(BR_IMAGE_INDEX_ENTRY_VDB)</c> assert.
/// </para>
///
/// <para>
/// This class is a passive data carrier — it parses + emits the header
/// fields, but does <i>not</i> yet write a complete DATABLOCK / DATAAREA
/// record because (a) we don't ship a real BR_STANDARD_HEADER wire layout
/// yet (the current 12-byte alias is documented in
/// <see cref="AomeiConstants.StandardHeaderSize"/>) and (b) we don't have
/// the exact byte offsets of the per-entry fields beyond their type-name
/// list (RegNo, BlockNo, ImgOffset, NewSize, OldSize, Crc32). The
/// <see cref="VendorEntriesOffset"/> constant pins the entries' start
/// offset within the record so the future wire-compat work can advance
/// against a verified fact.
/// </para>
/// </summary>
public sealed class BrImageIndex {

  /// <summary>Byte offset of the entry array from the start of the record,
  /// in the vendor's 16-byte-header layout: 0x1C.</summary>
  public const int VendorEntriesOffset = AomeiConstants.VendorIndexEntriesOffset;

  /// <summary>Byte offset of <c>EntryCount</c> from the start of the record.</summary>
  public const int VendorEntryCountOffset = AomeiConstants.VendorIndexEntryCountOffset;

  /// <summary>Byte offset of <c>EntrySize</c> from the start of the record.</summary>
  public const int VendorEntrySizeOffset = AomeiConstants.VendorIndexEntrySizeOffset;

  /// <summary>The <c>INDEX_TYPE_*</c> tag in the embedded
  /// <c>BR_STANDARD_HEADER</c>.</summary>
  public ushort Type { get; init; }

  /// <summary>Number of entries in the packed array following the header.</summary>
  public uint EntryCount { get; init; }

  /// <summary>Byte size of one entry. For
  /// <see cref="AomeiConstants.IndexTypeDataBlock"/> this is
  /// <see cref="AomeiConstants.VendorVdbEntrySize"/> = 0x20.</summary>
  public uint EntrySize { get; init; }

  /// <summary>Initialises a new index header.</summary>
  public BrImageIndex(ushort type, uint entryCount, uint entrySize) {
    this.Type = type;
    this.EntryCount = entryCount;
    this.EntrySize = entrySize;
  }

  /// <summary>Reads the <c>EntryCount</c> / <c>EntrySize</c> fields from a
  /// <c>BR_IMAGE_INDEX</c> record laid out in the vendor 16-byte-header
  /// format. Returns <c>false</c> when the record is shorter than 0x1C
  /// bytes (the minimum size of an empty index).</summary>
  public static bool TryReadVendor(ReadOnlySpan<byte> record, out uint entryCount, out uint entrySize) {
    entryCount = 0;
    entrySize = 0;
    if (record.Length < AomeiConstants.VendorIndexEntriesOffset)
      return false;
    entryCount = BinaryPrimitives.ReadUInt32LittleEndian(record[VendorEntryCountOffset..]);
    entrySize = BinaryPrimitives.ReadUInt32LittleEndian(record[VendorEntrySizeOffset..]);
    return true;
  }
}

/// <summary>
/// <c>BR_IMAGE_INDEX_ENTRY_VDB</c> — the 0x20-byte volume-data block
/// descriptor that populates an <see cref="AomeiConstants.IndexTypeDataBlock"/>
/// record's entry array. Pinned size <see cref="AomeiConstants.VendorVdbEntrySize"/>
/// = 0x20.
///
/// <para>
/// Field name list recovered from <c>ImgFile.dll!ImageVolume.cpp</c> via
/// the <c>m_pConvert-&gt;Decode(pNew, vdb.NewSize, pOld, OldLen)</c>,
/// <c>m_pImgSet-&gt;ReadData(vdb.ImgOffset, pNew, vdb.NewSize)</c>,
/// <c>vdb.NewSize==vdb.OldSize</c>, <c>Crc32==vdb.Crc32</c> and
/// <c>GetBlock(vdb.RegNo, vdb.BlockNo, Buff, BufLen, Bitmap, BmpLen, nCrc)</c>
/// access patterns:
/// </para>
/// <list type="bullet">
///   <item><description><c>RegNo</c> — region index into the volume's
///     <c>m_vtrDataRegion</c> array.</description></item>
///   <item><description><c>BlockNo</c> — block index within that region
///     (qword in the FlbDataRegion code path; treated as u64 here for
///     symmetry).</description></item>
///   <item><description><c>ImgOffset</c> — byte offset inside the image
///     set where the compressed + encrypted payload + bitmap is stored.</description></item>
///   <item><description><c>NewSize</c> — size of the stored
///     payload (post compress / encrypt).</description></item>
///   <item><description><c>OldSize</c> — size of the decoded payload
///     (BufLen + BmpLen, where BufLen is the sector data and BmpLen is
///     the cluster-allocation bitmap).</description></item>
///   <item><description><c>Crc32</c> — BRCrc32 over the decoded
///     <c>OldSize</c> bytes.</description></item>
/// </list>
///
/// <para>
/// The exact byte offset of each field within the 0x20-byte entry is not
/// pinned by passive RE — the C++ code accesses each as a typed struct
/// member. A plausible natural layout (u32 RegNo, u32 BlockNo, u64
/// ImgOffset, u32 NewSize, u32 OldSize, u32 Crc32 = 32 bytes total) is
/// captured by <see cref="PlausibleLayout"/> but is <b>not</b> yet
/// validated against a real .adi sample, so this class only carries the
/// fields as a passive data structure rather than encoding them.
/// </para>
/// </summary>
public sealed class BrImageIndexEntryVdb {

  /// <summary>Region index. Picks one of the volume's data regions.</summary>
  public uint RegNo { get; init; }

  /// <summary>Block index within the region.</summary>
  public ulong BlockNo { get; init; }

  /// <summary>Byte offset within the image-set where the stored payload
  /// (compressed + encrypted bytes + cluster bitmap) begins.</summary>
  public ulong ImgOffset { get; init; }

  /// <summary>Byte size of the stored payload.</summary>
  public uint NewSize { get; init; }

  /// <summary>Byte size of the decoded payload (= sector data + bitmap).</summary>
  public uint OldSize { get; init; }

  /// <summary>BRCrc32 over the decoded payload.</summary>
  public uint Crc32 { get; init; }

  /// <summary>
  /// Plausible natural-alignment layout sketch — kept for reference only:
  /// <code>
  /// 0x00 .. 0x03  RegNo:u32
  /// 0x04 .. 0x07  BlockNo:u32_low      (or zero-extension of the qword)
  /// 0x08 .. 0x0F  ImgOffset:u64
  /// 0x10 .. 0x13  NewSize:u32
  /// 0x14 .. 0x17  OldSize:u32
  /// 0x18 .. 0x1B  Crc32:u32
  /// 0x1C .. 0x1F  pad / reserved
  /// </code>
  /// This adds up to 0x20 bytes and matches the access pattern observed in
  /// the disassembly, but the field ordering and exact widths could differ
  /// (e.g. BlockNo could be u64 occupying 0x04..0x0B with ImgOffset shifted
  /// to 0x0C..0x13). Not used by any encode / decode path yet.
  /// </summary>
  public const string PlausibleLayout =
    "RegNo:u32, BlockNo:u32, ImgOffset:u64, NewSize:u32, OldSize:u32, Crc32:u32, Pad:u32";
}

/// <summary>
/// <c>BR_IMAGE_INDEX_ENTRY_FDB</c> — file-level data-block descriptor that
/// populates an <see cref="AomeiConstants.IndexTypeDataArea"/> record's
/// entry array. The vendor size is not directly pinned — there's no
/// <c>cmp [reg+0x18], imm</c> for the FDB assert site visible — but the
/// access pattern in <c>ImgFile.dll!FlbDataRegion.cpp</c> shows the same
/// fields as VDB minus <c>RegNo</c> (file backups have no multi-region
/// notion):
/// <code>
/// fdb.ImgOffset, fdb.NewSize, fdb.OldSize, fdb.Crc32, Fdb.BlockNo
/// </code>
/// Carried here as a passive data class for forward compatibility; not
/// emitted by any current writer path.
/// </summary>
public sealed class BrImageIndexEntryFdb {

  /// <summary>Block index within the data area.</summary>
  public ulong BlockNo { get; init; }

  /// <summary>Byte offset in the image-set where the stored payload
  /// begins.</summary>
  public ulong ImgOffset { get; init; }

  /// <summary>Byte size of the stored payload.</summary>
  public uint NewSize { get; init; }

  /// <summary>Byte size of the decoded payload.</summary>
  public uint OldSize { get; init; }

  /// <summary>BRCrc32 over the decoded payload.</summary>
  public uint Crc32 { get; init; }
}

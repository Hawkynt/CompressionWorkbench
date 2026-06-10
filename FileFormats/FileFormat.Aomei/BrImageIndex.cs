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

  /// <summary>Reads the <c>EntryCount</c> / <c>EntrySize</c> fields from a
  /// <c>BR_IMAGE_INDEX</c> record laid out in this codebase's shipped
  /// 12-byte BR_STANDARD_HEADER alias format
  /// (<see cref="AomeiConstants.ShippedIndexEntryCountOffset"/> /
  /// <see cref="AomeiConstants.ShippedIndexEntrySizeOffset"/>).
  /// Returns <c>false</c> when the record is shorter than
  /// <see cref="AomeiConstants.ShippedIndexHeaderSize"/> bytes.</summary>
  public static bool TryReadShipped(ReadOnlySpan<byte> record, out uint entryCount, out uint entrySize) {
    entryCount = 0;
    entrySize = 0;
    if (record.Length < AomeiConstants.ShippedIndexHeaderSize)
      return false;
    entryCount = BinaryPrimitives.ReadUInt32LittleEndian(record[AomeiConstants.ShippedIndexEntryCountOffset..]);
    entrySize = BinaryPrimitives.ReadUInt32LittleEndian(record[AomeiConstants.ShippedIndexEntrySizeOffset..]);
    return true;
  }

  /// <summary>Builds a complete shipped <c>INDEX_TYPE_DATABLOCK</c>
  /// (0x202) record with the supplied VDB entries packed contiguously
  /// from offset <see cref="AomeiConstants.ShippedIndexEntriesOffset"/>.
  /// The BR_STANDARD_HEADER's <c>Size</c> field is set to the total
  /// record length and the CRC32 is sealed in place per
  /// <see cref="BrStandardHeader.SealCrc"/>.</summary>
  public static byte[] BuildDataBlockRecord(IReadOnlyList<BrImageIndexEntryVdb> entries) {
    ArgumentNullException.ThrowIfNull(entries);
    var entryCount = (uint)entries.Count;
    var entrySize = (uint)AomeiConstants.VendorVdbEntrySize;
    var totalSize = AomeiConstants.ShippedIndexHeaderSize + (int)(entryCount * entrySize);
    var buf = new byte[totalSize];
    new BrStandardHeader(
      (uint)totalSize,
      AomeiConstants.IndexTypeDataBlock,
      0
    ).Write(buf);
    // Reserved at +0x0C stays zero.
    BinaryPrimitives.WriteUInt32LittleEndian(
      buf.AsSpan(AomeiConstants.ShippedIndexEntryCountOffset, 4),
      entryCount);
    BinaryPrimitives.WriteUInt32LittleEndian(
      buf.AsSpan(AomeiConstants.ShippedIndexEntrySizeOffset, 4),
      entrySize);
    for (var i = 0; i < entries.Count; ++i)
      entries[i].Write(buf.AsSpan(
        AomeiConstants.ShippedIndexEntriesOffset + i * (int)entrySize,
        (int)entrySize));
    BrStandardHeader.SealCrc(buf);
    return buf;
  }
}

/// <summary>
/// <c>BR_IMAGE_INDEX_ENTRY_VDB</c> — the 0x20-byte volume-data block
/// descriptor that populates an <see cref="AomeiConstants.IndexTypeDataBlock"/>
/// record's entry array. Pinned size <see cref="AomeiConstants.VendorVdbEntrySize"/>
/// = 0x20 bytes; per-field byte offsets pinned by the constants in
/// <see cref="AomeiConstants"/> (see XML doc on each <c>VendorVdbEntry*</c>
/// for the per-field disassembly provenance).
///
/// <para>
/// Pinned layout (recovered by triangulating three independent code paths
/// in <c>ImgFile.dll</c>):
/// <code>
/// struct BR_IMAGE_INDEX_ENTRY_VDB {  // total 0x20 bytes
///   uint32_t RegNo;      // 0x00..0x03  — region index
///   uint64_t BlockNo;    // 0x04..0x0B  — block index within region
///   uint64_t ImgOffset;  // 0x0C..0x13  — byte offset within image set
///   uint32_t OldSize;    // 0x14..0x17  — decoded payload size
///   uint32_t NewSize;    // 0x18..0x1B  — stored (compressed) payload size
///   uint32_t Crc32;      // 0x1C..0x1F  — BRCrc32 over decoded payload
/// };
/// </code>
/// </para>
///
/// <para>
/// Field-name provenance from <c>ImgFile.dll!ImageVolume.cpp</c> via the
/// assert-text xref strings
/// <c>m_pConvert-&gt;Decode(pNew, vdb.NewSize, pOld, OldLen)</c>,
/// <c>m_pImgSet-&gt;ReadData(vdb.ImgOffset, pNew, vdb.NewSize)</c>,
/// <c>vdb.NewSize==vdb.OldSize</c>, <c>Crc32==vdb.Crc32</c> and
/// <c>GetBlock(vdb.RegNo, vdb.BlockNo, Buff, BufLen, Bitmap, BmpLen, nCrc)</c>:
/// </para>
/// <list type="bullet">
///   <item><description><c>RegNo</c> — region index into the volume's
///     <c>m_vtrDataRegion</c> array.</description></item>
///   <item><description><c>BlockNo</c> — u64 block index within that
///     region.</description></item>
///   <item><description><c>ImgOffset</c> — u64 byte offset inside the
///     image set where the compressed + encrypted payload + bitmap is
///     stored.</description></item>
///   <item><description><c>OldSize</c> — size of the decoded payload
///     (BufLen + BmpLen, where BufLen is the sector data and BmpLen is
///     the cluster-allocation bitmap).</description></item>
///   <item><description><c>NewSize</c> — size of the stored payload
///     (post compress / encrypt).</description></item>
///   <item><description><c>Crc32</c> — BRCrc32 over the decoded
///     <c>OldSize</c> bytes.</description></item>
/// </list>
/// </summary>
public sealed class BrImageIndexEntryVdb {

  /// <summary>Byte offset of <see cref="RegNo"/>: 0x00. Mirrors
  /// <see cref="AomeiConstants.VendorVdbEntryRegNoOffset"/>.</summary>
  public const int RegNoOffset = AomeiConstants.VendorVdbEntryRegNoOffset;

  /// <summary>Byte offset of <see cref="BlockNo"/>: 0x04. Mirrors
  /// <see cref="AomeiConstants.VendorVdbEntryBlockNoOffset"/>.</summary>
  public const int BlockNoOffset = AomeiConstants.VendorVdbEntryBlockNoOffset;

  /// <summary>Byte offset of <see cref="ImgOffset"/>: 0x0C. Mirrors
  /// <see cref="AomeiConstants.VendorVdbEntryImgOffsetOffset"/>.</summary>
  public const int ImgOffsetOffset = AomeiConstants.VendorVdbEntryImgOffsetOffset;

  /// <summary>Byte offset of <see cref="OldSize"/>: 0x14. Mirrors
  /// <see cref="AomeiConstants.VendorVdbEntryOldSizeOffset"/>.</summary>
  public const int OldSizeOffset = AomeiConstants.VendorVdbEntryOldSizeOffset;

  /// <summary>Byte offset of <see cref="NewSize"/>: 0x18. Mirrors
  /// <see cref="AomeiConstants.VendorVdbEntryNewSizeOffset"/>.</summary>
  public const int NewSizeOffset = AomeiConstants.VendorVdbEntryNewSizeOffset;

  /// <summary>Byte offset of <see cref="Crc32"/>: 0x1C. Mirrors
  /// <see cref="AomeiConstants.VendorVdbEntryCrc32Offset"/>.</summary>
  public const int Crc32Offset = AomeiConstants.VendorVdbEntryCrc32Offset;

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
  /// One-line description of the pinned VDB layout. Used by the metadata
  /// surface to broadcast the recovered facts to downstream forensic
  /// tooling without requiring callers to read the XML docs. Updated as
  /// of this commit to the disassembly-pinned ordering (was previously a
  /// plausibility sketch).
  /// </summary>
  public const string PlausibleLayout =
    "RegNo:u32@0x00, BlockNo:u64@0x04, ImgOffset:u64@0x0C, OldSize:u32@0x14, NewSize:u32@0x18, Crc32:u32@0x1C";

  /// <summary>Read a VDB entry from a 0x20-byte span at the disassembly-
  /// pinned field offsets.</summary>
  /// <exception cref="ArgumentException">When <paramref name="entry"/> is
  /// shorter than <see cref="AomeiConstants.VendorVdbEntrySize"/>.</exception>
  public static BrImageIndexEntryVdb Read(ReadOnlySpan<byte> entry) {
    if (entry.Length < AomeiConstants.VendorVdbEntrySize)
      throw new ArgumentException(
        $"Buffer too small for BR_IMAGE_INDEX_ENTRY_VDB ({entry.Length} < {AomeiConstants.VendorVdbEntrySize}).",
        nameof(entry));
    return new BrImageIndexEntryVdb {
      RegNo = BinaryPrimitives.ReadUInt32LittleEndian(entry[RegNoOffset..]),
      BlockNo = BinaryPrimitives.ReadUInt64LittleEndian(entry[BlockNoOffset..]),
      ImgOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry[ImgOffsetOffset..]),
      OldSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[OldSizeOffset..]),
      NewSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[NewSizeOffset..]),
      Crc32 = BinaryPrimitives.ReadUInt32LittleEndian(entry[Crc32Offset..]),
    };
  }

  /// <summary>Write this VDB entry into a 0x20-byte span at the
  /// disassembly-pinned field offsets.</summary>
  /// <exception cref="ArgumentException">When <paramref name="entry"/> is
  /// shorter than <see cref="AomeiConstants.VendorVdbEntrySize"/>.</exception>
  public void Write(Span<byte> entry) {
    if (entry.Length < AomeiConstants.VendorVdbEntrySize)
      throw new ArgumentException(
        $"Buffer too small for BR_IMAGE_INDEX_ENTRY_VDB ({entry.Length} < {AomeiConstants.VendorVdbEntrySize}).",
        nameof(entry));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[RegNoOffset..], this.RegNo);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[BlockNoOffset..], this.BlockNo);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[ImgOffsetOffset..], this.ImgOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[OldSizeOffset..], this.OldSize);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[NewSizeOffset..], this.NewSize);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[Crc32Offset..], this.Crc32);
  }
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

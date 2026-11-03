#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.EaseUs;

/// <summary>
/// On-disk structure pin for the EaseUS Todo Backup (<c>.pbd</c>)
/// container.
///
/// <para>
/// <b>Pinned shapes.</b>
/// Three concrete shapes that the previous chunk-stream-only reader
/// treated as opaque:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="HeaderBlockSize"/> = <c>0x4E8</c> (1256) bytes —
///     <b>not</b> the 12-byte top-of-file slice the previous reader
///     assumed. The header block starts at file offset 0 with
///     <c>"IMGF"</c>. The two community-known zlib substream headers at
///     file offsets <c>0x98</c> and <c>0x10F</c> (Rune-Server thread
///     694189) are therefore <i>inside</i> the 1256-byte header block —
///     they're header-bank sub-streams, not body chunks. The two header
///     fields immediately after the magic
///     (<see cref="HeaderSizeFieldOffset"/> = 4 and
///     <see cref="HeaderVersionFieldOffset"/> = 8) carry
///     <c>{"IMGF", 0x000004E8, 0x00010001}</c>.
///   </description></item>
///   <item><description>
///     <see cref="TrailerBlockSize"/> = <c>0xC0</c> (192) bytes at
///     <c>EOF-0xC0</c>, with the second IMGF marker located at
///     <c>trailer+0xBC</c> (i.e. the last four bytes of the file proper,
///     immediately ahead of the 0xFF padding run). The trailer body holds
///     188 structured bytes, zero-filled except for <c>"IMGF"</c> at
///     <see cref="TrailerMagicFieldOffset"/>, <c>0xC0</c> at
///     <see cref="TrailerSizeFieldOffset"/>, and <c>0x00010001</c> at
///     <see cref="TrailerVersionFieldOffset"/>.
///   </description></item>
///   <item><description>
///     The <see cref="IndxBlockMagic"/> (<c>"INDX"</c>) record: 16-byte
///     header (<c>"INDX"</c> + u32 current-offset + u32 total-length + u32
///     reserved) followed by an array of <see cref="IndxEntrySize"/> =
///     <c>0x18</c> (24-byte) entries, bounded by the running offset staying
///     under <c>[INDX+8]</c>. Each entry packs a 32-bit start key, a
///     run-length field whose low 10 bits encode the entry length
///     (mask <c>0x3FF</c>), and three more u32 payload fields. This
///     is the proprietary block-allocation table — it's what maps
///     logical sectors back to compressed-body chunks. We pin the
///     shape but explicitly do NOT advertise sector reconstruction:
///     the INDX block itself lives behind a zlib-compressed and
///     possibly AES-wrapped header bank, and the entry payload-field
///     semantics (cluster index? body-chunk offset?) need a sample
///     diff to nail down. <see cref="VolmBlockMagic"/> (<c>"VOLM"</c>)
///     is the per-partition record referenced from the trailer
///     (offsets <c>+0x10..+0x40</c> hold u16 cluster-shift, u32
///     sector count, u16 magic-tag-2); <see cref="FdirBlockMagic"/>
///     (<c>"FDIR"</c>) is a file-system-side directory record
///     (NTFS / FAT / ext4 walkers consume it after sector
///     reconstruction).
///   </description></item>
/// </list>
///
/// <para>
/// <b>What remains documented-TODO.</b>
/// Even with the header / trailer / INDX shapes pinned, sector
/// reconstruction stays Stage-0 because (a) the header-bank zlib
/// sub-streams at <c>0x98</c> and <c>0x10F</c> hold the INDX root +
/// VOLM table but the per-entry payload fields haven't been
/// disambiguated against a known-content sample (single-text-file v1
/// vs v2 backup), (b) the AES-256 key envelope wraps every body chunk
/// on encrypted backups and that key derivation isn't reproduced here,
/// and (c) the parent-chain / incremental-backup pointer chase across
/// <c>.pbd</c> chain-mates lives inside the trailer's 180 structured
/// bytes but only the magic + size + version fields at the tail
/// (<c>+0xB4..+0xBF</c>) are confirmed. Promotion past
/// <i>chunk-stream surfacing</i> to <i>filesystem walk</i> needs a
/// real-world <c>.pbd</c> corpus plus a sample diff — neither is in the
/// repo today.
/// </para>
/// </summary>
public static class EaseUsContainerIndex {

  // ---------------------------------------------------------------------
  // Header block — at file offset 0, size 0x4E8 (1256) bytes.
  // ---------------------------------------------------------------------

  /// <summary>Fixed header block size at file offset 0.</summary>
  public const int HeaderBlockSize = 0x4E8;

  /// <summary>"IMGF" magic at file offset 0 (and at <c>HeaderMagicFieldOffset</c> in the in-memory struct).</summary>
  public const int HeaderMagicFieldOffset = 0x00;

  /// <summary>U32 header-size field at file offset 4; writer pins to <c>0x000004E8</c> (matches <see cref="HeaderBlockSize"/>).</summary>
  public const int HeaderSizeFieldOffset = 0x04;

  /// <summary>U32 version-word field at file offset 8; writer pins to <c>0x00010001</c> (major=1 / minor=1).</summary>
  public const int HeaderVersionFieldOffset = 0x08;

  /// <summary>U32 header-size value expected at <see cref="HeaderSizeFieldOffset"/>.</summary>
  public const uint HeaderSizeFieldExpectedValue = 0x000004E8;

  /// <summary>U32 version-word value expected at <see cref="HeaderVersionFieldOffset"/>.</summary>
  public const uint HeaderVersionFieldExpectedValue = 0x00010001;

  /// <summary>
  /// Empirically-observed file offset of the first header-bank zlib
  /// substream inside every .pbd analysed in Rune-Server thread 694189
  /// (152 = 0x98). This sits INSIDE the <see cref="HeaderBlockSize"/>
  /// 1256-byte header block, not after it, so it's a header-bank
  /// sub-stream rather than a body chunk.
  /// </summary>
  public const int HeaderBankZlibSubstream1Offset = 0x98;

  /// <summary>
  /// Second header-bank zlib substream offset (271 = 0x10F) — also
  /// inside the header block. Per the Rune-Server analysis this and
  /// substream-1 are stable across v1 / v2 backup pairs while the
  /// body-region substreams shift by the payload-delta byte count.
  /// </summary>
  public const int HeaderBankZlibSubstream2Offset = 0x10F;

  // ---------------------------------------------------------------------
  // Trailer block — at file offset (EOF - 0xC0), size 0xC0 (192) bytes,
  // followed by a variable-length 0xFF padding run to the file's
  // nominal end.
  // ---------------------------------------------------------------------

  /// <summary>Fixed trailer block size at <c>EOF-0xC0</c>.</summary>
  public const int TrailerBlockSize = 0xC0;

  /// <summary>Offset (within the trailer block) of the trailer's <c>0x00010001</c> version word.</summary>
  public const int TrailerVersionFieldOffset = 0xB4;

  /// <summary>Offset (within the trailer block) of the trailer's <c>0xC0</c> size word.</summary>
  public const int TrailerSizeFieldOffset = 0xB8;

  /// <summary>Offset (within the trailer block) of the trailer's second "IMGF" magic.</summary>
  public const int TrailerMagicFieldOffset = 0xBC;

  /// <summary>U32 trailer-size value expected at <see cref="TrailerSizeFieldOffset"/>.</summary>
  public const uint TrailerSizeFieldExpectedValue = 0x000000C0;

  /// <summary>U32 trailer-version value expected at <see cref="TrailerVersionFieldOffset"/>.</summary>
  public const uint TrailerVersionFieldExpectedValue = 0x00010001;

  // ---------------------------------------------------------------------
  // Block magics that appear once the header-bank zlib sub-streams are
  // inflated. These don't appear in the raw .pbd bytes; they identify the
  // inflated tables carried by the header-bank substreams.
  // ---------------------------------------------------------------------

  /// <summary>Block-allocation table magic.</summary>
  public static readonly byte[] IndxBlockMagic = "INDX"u8.ToArray();

  /// <summary>Per-partition / volume record magic.</summary>
  public static readonly byte[] VolmBlockMagic = "VOLM"u8.ToArray();

  /// <summary>File-system directory record magic.</summary>
  public static readonly byte[] FdirBlockMagic = "FDIR"u8.ToArray();

  /// <summary>Reverse-index record magic.</summary>
  public static readonly byte[] RindBlockMagic = "RIND"u8.ToArray();

  /// <summary>Filter-record magic (a buffer-type tag, not a file-format magic).</summary>
  public static readonly byte[] FltrRecordMagic = "FLTR"u8.ToArray();

  /// <summary>
  /// Size in bytes of one entry in the INDX block-allocation array.
  /// </summary>
  public const int IndxEntrySize = 0x18;

  /// <summary>
  /// Bit mask applied to the low 32 bits of <c>entry[4..7]</c> in the
  /// INDX array to extract the run-length: <c>(entry[4..7] &amp; 0x3FF)</c>.
  /// </summary>
  public const uint IndxEntryLengthMask = 0x3FF;

  /// <summary>
  /// Size in bytes of the fixed INDX block header that precedes the
  /// 24-byte entry array. <c>+0x00..+0x03</c> = "INDX",
  /// <c>+0x04..+0x07</c> = first-entry offset,
  /// <c>+0x08..+0x0B</c> = total-length (used as the iteration cap),
  /// <c>+0x0C..+0x0F</c> = reserved / chain ptr.
  /// </summary>
  public const int IndxBlockHeaderSize = 0x10;

  // ---------------------------------------------------------------------
  // Helpers — small, side-effect-free probes that don't allocate.
  // ---------------------------------------------------------------------

  /// <summary>
  /// True if <paramref name="header"/> is at least <see cref="HeaderBlockSize"/>
  /// bytes and the embedded size + version fields match the writer-side
  /// constants. Useful as a fail-soft pre-flight before reading the
  /// full block.
  /// </summary>
  public static bool LooksLikeWellFormedHeader(ReadOnlySpan<byte> header) {
    if (header.Length < HeaderBlockSize) return false;
    if (!header[..4].SequenceEqual("IMGF"u8) && !header[..4].SequenceEqual("FIMG"u8)) return false;
    var sz = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(HeaderSizeFieldOffset, 4));
    if (sz != HeaderSizeFieldExpectedValue) return false;
    var ver = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(HeaderVersionFieldOffset, 4));
    return ver == HeaderVersionFieldExpectedValue;
  }

  /// <summary>
  /// True if <paramref name="trailer"/> is exactly <see cref="TrailerBlockSize"/>
  /// bytes and the size / version / magic words at the tail are the
  /// writer-side constants. Use after locating the trailer by scanning
  /// back from EOF past the 0xFF padding run.
  /// </summary>
  public static bool LooksLikeWellFormedTrailer(ReadOnlySpan<byte> trailer) {
    if (trailer.Length < TrailerBlockSize) return false;
    var sz = BinaryPrimitives.ReadUInt32LittleEndian(trailer.Slice(TrailerSizeFieldOffset, 4));
    if (sz != TrailerSizeFieldExpectedValue) return false;
    var ver = BinaryPrimitives.ReadUInt32LittleEndian(trailer.Slice(TrailerVersionFieldOffset, 4));
    if (ver != TrailerVersionFieldExpectedValue) return false;
    var magic = trailer.Slice(TrailerMagicFieldOffset, 4);
    return magic.SequenceEqual("IMGF"u8) || magic.SequenceEqual("FIMG"u8);
  }

  /// <summary>
  /// Returns the file offset where the trailer block starts for a .pbd
  /// of <paramref name="fileLength"/> bytes (the count of trailing 0xFF
  /// padding bytes excluded — pass them in <paramref name="trailingFfPadding"/>).
  /// </summary>
  public static long ComputeTrailerOffset(long fileLength, int trailingFfPadding) {
    var nominalEnd = fileLength - trailingFfPadding;
    return nominalEnd - TrailerBlockSize;
  }

  /// <summary>
  /// Dumps the structure constants as a key=value
  /// block suitable for embedding in <c>metadata.ini</c>.
  /// </summary>
  public static string DescribeStructure() {
    var sb = new StringBuilder();
    sb.Append("# EaseUS PBD structure constants — see EaseUsContainerIndex.cs.\n");
    sb.Append("header_block_size=").Append(HeaderBlockSize).Append('\n');
    sb.Append("header_size_field_offset=").Append(HeaderSizeFieldOffset).Append('\n');
    sb.Append("header_version_field_offset=").Append(HeaderVersionFieldOffset).Append('\n');
    sb.Append("header_size_field_expected_value=0x").Append(HeaderSizeFieldExpectedValue.ToString("X8")).Append('\n');
    sb.Append("header_version_field_expected_value=0x").Append(HeaderVersionFieldExpectedValue.ToString("X8")).Append('\n');
    sb.Append("header_bank_zlib_substream1_offset=").Append(HeaderBankZlibSubstream1Offset).Append('\n');
    sb.Append("header_bank_zlib_substream2_offset=").Append(HeaderBankZlibSubstream2Offset).Append('\n');
    sb.Append("trailer_block_size=").Append(TrailerBlockSize).Append('\n');
    sb.Append("trailer_version_field_offset=").Append(TrailerVersionFieldOffset).Append('\n');
    sb.Append("trailer_size_field_offset=").Append(TrailerSizeFieldOffset).Append('\n');
    sb.Append("trailer_magic_field_offset=").Append(TrailerMagicFieldOffset).Append('\n');
    sb.Append("trailer_size_field_expected_value=0x").Append(TrailerSizeFieldExpectedValue.ToString("X8")).Append('\n');
    sb.Append("trailer_version_field_expected_value=0x").Append(TrailerVersionFieldExpectedValue.ToString("X8")).Append('\n');
    sb.Append("indx_block_header_size=").Append(IndxBlockHeaderSize).Append('\n');
    sb.Append("indx_entry_size=").Append(IndxEntrySize).Append('\n');
    sb.Append("indx_entry_length_mask=0x").Append(IndxEntryLengthMask.ToString("X")).Append('\n');
    return sb.ToString();
  }
}

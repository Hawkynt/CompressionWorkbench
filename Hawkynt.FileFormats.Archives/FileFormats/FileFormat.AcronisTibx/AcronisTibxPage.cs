#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.AcronisTibx;

/// <summary>
///   Page-type discriminator for a 4 KiB <c>.tibx</c> page. Values are the one-byte tag at
///   page-frame offset <c>+0x1</c>, named in table order
///   <c>"UNKNOWN" / "HDR" / "LSM_LEAF" / "LSM_DIR" / "GOLOMB" / "DATA" / "CI"</c>.
/// </summary>
public enum AcronisTibxPageType : byte {
  /// <summary>Tag <c>0</c> — slot is unwritten / unallocated. String table position 0.</summary>
  Unknown = 0,
  /// <summary>
  ///   Tag <c>1</c> — page-zero archive header. The 4-byte content magic at the page-frame
  ///   data window (offset <c>+0x0</c>) is the ASCII string <c>"ARCH"</c>. String table position 1.
  /// </summary>
  Hdr = 1,
  /// <summary>
  ///   Tag <c>2</c> — LSM leaf page carrying a sorted sequence of key/value records (the
  ///   per-item attribute bodies). Content magic at <c>+0x8</c> is ASCII <c>"LEAF"</c>. String
  ///   table position 2.
  /// </summary>
  LsmLeaf = 2,
  /// <summary>
  ///   Tag <c>3</c> — LSM directory (internal-node) page indexing leaf children. Content magic
  ///   at <c>+0x8</c> is ASCII <c>"LDIR"</c>. String table position 3.
  /// </summary>
  LsmDir = 3,
  /// <summary>
  ///   Tag <c>4</c> — Golomb-coded auxiliary index page holding the per-page item-id
  ///   directory.
  ///   String table position 4.
  /// </summary>
  Golomb = 4,
  /// <summary>
  ///   Tag <c>5</c> — DATA page carrying extent payload for file content. The body is the raw
  ///   (or AES-wrapped) byte run referenced from leaf page records. String table position 5.
  /// </summary>
  Data = 5,
  /// <summary>
  ///   Tag <c>6</c> — Commit-info / checkpoint page. Content magic at <c>+0x8</c> is ASCII
  ///   <c>"ARCI"</c>. Linked into a chain rooted at the <c>chain_start_pg</c> field of the
  ///   page-zero header. String table position 6.
  /// </summary>
  Ci = 6,
}

/// <summary>
///   One parsed 4 KiB <c>.tibx</c> page. Carries the page-frame fields (type tag, stored CRC,
///   content magic), plus the LSM-specific sub-header fields (version, encoding, count, len,
///   zlen, seq, id) that every LEAF/LDIR page carries.
/// </summary>
/// <remarks>
///   <para>
///     <b>Page-frame layout</b> (8 bytes):
///     <list type="table">
///       <listheader><term>Offset</term><description>Field</description></listheader>
///       <item><term>+0x0</term><description><c>byte</c> <c>0x41</c> ('A') sentinel — every
///         typed page-frame leads with <c>'A'</c>.</description></item>
///       <item><term>+0x1</term><description><c>byte</c> page-type tag — see
///         <see cref="AcronisTibxPageType"/>. <c>0x00</c> is not a valid typed page.</description></item>
///       <item><term>+0x2</term><description><c>byte</c> reserved — <c>0x00</c> for typed
///         pages.</description></item>
///       <item><term>+0x3</term><description><c>byte</c> page-frame minor — typically zero.</description></item>
///       <item><term>+0x4..+0x7</term><description><c>uint32</c> BE — stored CRC32 (computed
///         over the full 4 KiB page with this field zeroed before computing).</description></item>
///     </list>
///     For the page-zero <c>HDR</c> page the byte at <c>+0x0</c> is also the first byte of
///     the 4-byte ASCII <c>"ARCH"</c> magic (<c>0x41 0x52 0x43 0x48</c>), so the sentinel
///     matches, but the page-type byte at <c>+0x1</c> reads <c>'R' = 0x52</c>: the page-zero
///     header is recognised by its full <c>"ARCH"</c> magic instead.
///   </para>
///   <para>
///     <b>LSM page sub-header</b> (LEAF / LDIR variants):
///     <list type="table">
///       <listheader><term>Offset (from page-frame start)</term><description>Field</description></listheader>
///       <item><term>+0x8..+0xb</term><description>4-byte ASCII content magic
///         (<c>"LEAF"</c> = <c>0x4641454c</c>, <c>"LDIR"</c> = <c>0x5249444c</c>).</description></item>
///       <item><term>+0xc</term><description><c>byte</c> version.</description></item>
///       <item><term>+0xd</term><description><c>byte</c> encoding (<c>3</c> = standard LSM,
///         <c>4</c> = alternative).</description></item>
///       <item><term>+0xe..+0xf</term><description><c>uint16</c> BE <c>count</c>.</description></item>
///       <item><term>+0x10..+0x13</term><description><c>uint32</c> BE <c>len</c>
///         (uncompressed body length).</description></item>
///       <item><term>+0x14..+0x17</term><description><c>uint32</c> BE <c>zlen</c>
///         (on-disk compressed body length).</description></item>
///       <item><term>+0x18..+0x1b</term><description><c>uint32</c> BE <c>seq</c>
///         (LSM-sequence ordinal).</description></item>
///       <item><term>+0x1c</term><description><c>byte</c> <c>id</c> (ctree index this page
///         belongs to, <c>0..nr_ctree-1</c>).</description></item>
///     </list>
///   </para>
///   <para>
///     <b>What this class does NOT decode.</b> The LSM record stream inside the LEAF page's
///     body (the actual file <c>(name, child_page_id)</c> directory tuples and the per-item
///     attribute records that would expose filenames) is Golomb-coded with adaptive per-page
///     bit-width parameters and gated by the optional per-page AES wrap. Reproducing that
///     decoder is the next stage and out of scope for this pass — the page-level summary
///     surfaced here lets a caller see what kinds of pages exist and where, but extracting
///     filenames from a LEAF page requires the record framing, which is not yet known.
///   </para>
/// </remarks>
public sealed class AcronisTibxPage {

  /// <summary>Page-frame size in bytes (always 4096).</summary>
  public const int PageSize = 0x1000;

  /// <summary>Byte offset of the page-type tag within the page frame.</summary>
  public const int PageTypeOffset = 0x1;

  /// <summary>Byte offset of the BE32 CRC32 within the page frame.</summary>
  public const int CrcOffset = 0x4;

  /// <summary>Byte offset of the 4-byte content magic within the page frame (LSM pages).</summary>
  public const int ContentMagicOffset = 0x8;

  /// <summary>1-based page index counted from the start of the container.</summary>
  public required long PageIndex { get; init; }

  /// <summary>Byte offset of this page within the container.</summary>
  public required long FileOffset { get; init; }

  /// <summary>Page-type tag at <c>+0x1</c>.</summary>
  public required AcronisTibxPageType PageType { get; init; }

  /// <summary>Stored BE32 CRC at <c>+0x4</c>. Zero for the page-zero HDR page (it uses a
  /// different layout — see <see cref="AcronisTibxReader"/>).</summary>
  public required uint StoredCrc { get; init; }

  /// <summary>
  ///   4-byte ASCII content magic. For HDR pages this is the page-zero <c>"ARCH"</c> tag
  ///   (read at <c>+0x0</c>). For LSM_LEAF this is <c>"LEAF"</c>, for LSM_DIR <c>"LDIR"</c>,
  ///   for CI <c>"ARCI"</c> (all read at <c>+0x8</c>). For GOLOMB / DATA / Unknown the
  ///   four bytes from the <c>+0x8</c> window are returned verbatim (those page types don't
  ///   carry a fixed ASCII magic).
  /// </summary>
  public required byte[] ContentMagic { get; init; }

  /// <summary>
  ///   Decoded sub-header for LSM_LEAF and LSM_DIR pages. <c>null</c> for HDR/GOLOMB/DATA/CI
  ///   pages — those page types carry different sub-headers we don't decode in this pass.
  /// </summary>
  public AcronisTibxLsmPageSubHeader? LsmSubHeader { get; init; }

  /// <summary><c>true</c> when this page is a recognised LSM index/leaf page.</summary>
  public bool IsLsmIndexPage =>
    this.PageType is AcronisTibxPageType.LsmLeaf or AcronisTibxPageType.LsmDir;

  /// <summary><c>true</c> when this page is a commit-info / checkpoint page.</summary>
  public bool IsCommitInfo => this.PageType == AcronisTibxPageType.Ci;

  /// <summary><c>true</c> when this page is a DATA payload page.</summary>
  public bool IsData => this.PageType == AcronisTibxPageType.Data;

  /// <summary>Returns the content magic as ASCII when all four bytes are printable, else
  /// the hex form.</summary>
  public string ContentMagicDisplay {
    get {
      var m = this.ContentMagic;
      var allPrintable = m.Length == 4
        && m[0] is >= 0x20 and <= 0x7E
        && m[1] is >= 0x20 and <= 0x7E
        && m[2] is >= 0x20 and <= 0x7E
        && m[3] is >= 0x20 and <= 0x7E;
      return allPrintable
        ? System.Text.Encoding.ASCII.GetString(m)
        : $"{m[0]:X2} {m[1]:X2} {m[2]:X2} {m[3]:X2}";
    }
  }

  /// <summary>
  ///   Tries to parse a single 4 KiB page frame. Returns <c>null</c> when the buffer is shorter
  ///   than one page or the leading 'A' sentinel is missing. Does NOT verify the CRC — callers
  ///   can do that themselves with <see cref="StoredCrc"/>.
  /// </summary>
  /// <param name="page">Full 4 KiB page buffer.</param>
  /// <param name="pageIndex">1-based page index from the start of the container.</param>
  /// <param name="fileOffset">Byte offset of this page within the container.</param>
  public static AcronisTibxPage? Parse(ReadOnlySpan<byte> page, long pageIndex, long fileOffset) {
    if (page.Length < PageSize) return null;
    if (page[0] != 0x41) return null; // 'A' sentinel required on every typed page

    // Page-zero ARCH header path: bytes [0..3] are the 4-byte ASCII "ARCH" magic itself,
    // detected by sniffing all four bytes (rather than the +0x1 type tag, which is 'R' here).
    if (page[1] == 'R' && page[2] == 'C' && page[3] == 'H')
      return new AcronisTibxPage {
        PageIndex = pageIndex,
        FileOffset = fileOffset,
        PageType = AcronisTibxPageType.Hdr,
        StoredCrc = 0,
        ContentMagic = page.Slice(0, 4).ToArray(),
        LsmSubHeader = null,
      };

    // Typed page-frame path: +0x1 = page-type tag, +0x4 = BE32 CRC, +0x8 = content magic.
    var typeTag = page[PageTypeOffset];
    var pageType = typeTag <= (byte)AcronisTibxPageType.Ci
      ? (AcronisTibxPageType)typeTag
      : AcronisTibxPageType.Unknown;
    var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(page.Slice(CrcOffset, 4));
    var contentMagic = page.Slice(ContentMagicOffset, 4).ToArray();

    AcronisTibxLsmPageSubHeader? lsmSub = null;
    if (pageType is AcronisTibxPageType.LsmLeaf or AcronisTibxPageType.LsmDir)
      lsmSub = AcronisTibxLsmPageSubHeader.Parse(page);

    return new AcronisTibxPage {
      PageIndex = pageIndex,
      FileOffset = fileOffset,
      PageType = pageType,
      StoredCrc = storedCrc,
      ContentMagic = contentMagic,
      LsmSubHeader = lsmSub,
    };
  }
}

/// <summary>
///   Decoded LSM-specific sub-header carried by every LEAF / LDIR page: the
///   <c>version / encoding / count / len / zlen / seq / id</c> fields.
/// </summary>
/// <param name="Version">Byte at <c>+0xC</c>.</param>
/// <param name="Encoding">Byte at <c>+0xD</c> (<c>3</c> = standard, <c>4</c> = alternative).</param>
/// <param name="Count">BE16 at <c>+0xE</c> — number of LSM records on this page.</param>
/// <param name="Len">BE32 at <c>+0x10</c> — uncompressed body length.</param>
/// <param name="Zlen">BE32 at <c>+0x14</c> — on-disk compressed body length.</param>
/// <param name="Seq">BE32 at <c>+0x18</c> — LSM sequence ordinal.</param>
/// <param name="Id">Byte at <c>+0x1C</c> — ctree index this page belongs to (0..nr_ctree-1).</param>
public sealed record AcronisTibxLsmPageSubHeader(
  byte Version,
  byte Encoding,
  ushort Count,
  uint Len,
  uint Zlen,
  uint Seq,
  byte Id
) {
  /// <summary>Decode a sub-header from a full page buffer starting at the page frame.</summary>
  public static AcronisTibxLsmPageSubHeader? Parse(ReadOnlySpan<byte> page) {
    if (page.Length < 0x20) return null;
    var version = page[0xC];
    var encoding = page[0xD];
    var count = BinaryPrimitives.ReadUInt16BigEndian(page.Slice(0xE, 2));
    var len = BinaryPrimitives.ReadUInt32BigEndian(page.Slice(0x10, 4));
    var zlen = BinaryPrimitives.ReadUInt32BigEndian(page.Slice(0x14, 4));
    var seq = BinaryPrimitives.ReadUInt32BigEndian(page.Slice(0x18, 4));
    var id = page[0x1C];
    return new AcronisTibxLsmPageSubHeader(version, encoding, count, len, zlen, seq, id);
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lz4;
using FileFormat.Acronis;

namespace FileFormat.AcronisTibx;

/// <summary>
/// Whole-archive writer for Acronis True Image <c>.tibx</c> (2020+ libarchive3 LSM page store)
/// containers.
/// </summary>
/// <remarks>
/// <para>
/// Builds a 4 KiB-page container whose byte layout is the exact inverse of
/// <see cref="AcronisTibxReader"/> / <see cref="AcronisTibxPage"/> /
/// <see cref="AcronisTibxLsmPageSubHeader"/> / <see cref="AcronisTibxLsmRecord"/>. The page set
/// produced is:
/// </para>
/// <list type="number">
///   <item><description><b>Page 0 — HDR.</b> ASCII <c>"ARCH"</c> magic at offset 0, BE16 version
///     code at <c>+0x008</c>, BE32 mode word at <c>+0x174</c>, an 8-word BE32 dump-field cluster
///     at <c>+0x1e0</c>, and a 16-byte archive UUID at <c>+0x233</c>. All fields land where the
///     reader's <c>Parse</c> reads them.</description></item>
///   <item><description><b>One LSM_LEAF page per file.</b> Page frame: sentinel <c>'A'</c> at
///     <c>+0x0</c>, page-type tag <c>2</c> at <c>+0x1</c>, BE32 CRC at <c>+0x4</c>, ASCII
///     <c>"LEAF"</c> content magic at <c>+0x8</c>, then the LSM sub-header (version, encoding 3,
///     BE16 count, BE32 len/zlen/seq, ctree-id) at <c>+0xC..+0x1C</c>. The body at <c>+0x20</c>
///     is an LZ4 chained stream (<c>(BE32 zchunk, BE32 chunk, LZ4-block)</c> triples) carrying an
///     ItemCommon(0x10) attribute stream that names the file — the same InputItem layout the
///     reader's <see cref="AcronisTibxLsmRecord.ScanForItemCommonAttributes"/> recovers.</description></item>
///   <item><description><b>One DATA page per file</b> carrying the raw file content (frame
///     sentinel + tag 5 + BE32 CRC; payload at <c>+0x8</c>). DATA pages are forensic surface —
///     the reader does not yet resolve leaf-to-extent pointers, but the pages are valid frames
///     that <c>ar_page_verify</c> accepts.</description></item>
///   <item><description><b>One CI (commit-info) page</b> tagged <c>"ARCI"</c> closing the
///     container.</description></item>
/// </list>
/// <para>
/// CRCs are computed with the IEEE CRC-32 over the full 4 KiB page with the CRC field zeroed,
/// matching the reader's stored-CRC field semantics (the reader stores but does not verify the
/// CRC; we compute a real one for structural fidelity).
/// </para>
/// <para>
/// <b>Scope / honesty.</b> The full Acronis LSM B+-tree (LDIR directory pages, Golomb-coded
/// item-id index, dedup short index, commit-info segment chain, optional AES wrap, content-
/// defined chunking) is NOT reproduced — those have no published spec. This writer produces a
/// structurally-valid page set that our own reader walks and from which it recovers the file
/// names via the ItemCommon scan; it is the reader-inverting writer pending vendor-restore
/// validation, and the descriptor does NOT advertise <c>CanCreate</c>.
/// </para>
/// </remarks>
public static class AcronisTibxWriter {

  /// <summary>4 KiB page size for all pages.</summary>
  public const int PageSize = AcronisTibxPage.PageSize;

  /// <summary>Default version code emitted at header offset 0x008 (BE16).</summary>
  public const ushort DefaultVersion = 0x0008;

  private static readonly byte[] LeafMagic = "LEAF"u8.ToArray();
  private static readonly byte[] DataMagic = "DATA"u8.ToArray();
  private static readonly byte[] ArciMagic = "ARCI"u8.ToArray();

  /// <summary>One file to place into a fresh container.</summary>
  /// <param name="Name">Full entry name (path included, e.g. <c>"subdir/nested.txt"</c>).</param>
  /// <param name="Content">Raw file bytes (stored verbatim in a DATA page).</param>
  public sealed record FileSpec(string Name, byte[] Content);

  /// <summary>
  /// Builds a complete <c>.tibx</c> container carrying <paramref name="files"/> and returns the
  /// full archive bytes (a whole number of 4 KiB pages).
  /// </summary>
  /// <param name="files">Files to place into the container.</param>
  /// <param name="archiveUuid">Optional 16-byte archive identity (random in real archives).</param>
  public static byte[] Build(IReadOnlyList<FileSpec> files, byte[]? archiveUuid = null) {
    ArgumentNullException.ThrowIfNull(files);

    var pages = new List<byte[]>();

    // Page 0 — HDR.
    pages.Add(BuildHeaderPage(archiveUuid));

    var seq = 1u;
    foreach (var f in files) {
      // ItemCommon carries the leaf name (the format's per-item name attribute); the directory
      // path is a separate SourceItem attribute in real archives and is not stored here. The
      // reader's forensic ItemCommon scan recovers leaf names (it rejects path separators).
      pages.Add(BuildLeafPage(LeafNameOf(f.Name), ctreeId: 0, seq: seq));
      pages.Add(BuildDataPage(f.Content, seq: seq));
      seq++;
    }

    // Closing commit-info page.
    pages.Add(BuildCommitInfoPage());

    var result = new byte[pages.Count * PageSize];
    for (var i = 0; i < pages.Count; i++)
      pages[i].CopyTo(result.AsSpan(i * PageSize, PageSize));
    return result;
  }

  // ----- page builders (inverse of AcronisTibxPage.Parse / AcronisTibxReader.Parse) -----

  private static byte[] BuildHeaderPage(byte[]? archiveUuid) {
    var page = new byte[PageSize];
    // "ARCH" magic at offset 0 (the reader sniffs all four bytes; page-type resolves to HDR).
    "ARCH"u8.CopyTo(page.AsSpan(0, 4));
    // BE16 version at 0x008.
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(AcronisTibxReader.VersionOffset, 2), DefaultVersion);
    // BE32 mode word at 0x174 — "FULL" backup mode (ASCII, also valid as a 4-byte discriminator).
    "FULL"u8.CopyTo(page.AsSpan(AcronisTibxReader.ModeOffset, 4));
    // 8-word BE32 dump-field cluster at 0x1e0 (fsize/offset/aligned_size/size + commit ids).
    // Zeros are valid; the reader surfaces them verbatim as forensic hex.
    // 16-byte archive UUID at 0x233.
    var uuid = archiveUuid is { Length: AcronisTibxReader.UuidLength }
      ? archiveUuid
      : Guid.NewGuid().ToByteArray();
    uuid.AsSpan(0, AcronisTibxReader.UuidLength).CopyTo(page.AsSpan(AcronisTibxReader.UuidOffset, AcronisTibxReader.UuidLength));
    // HDR page-zero carries no page-frame CRC (the reader surfaces it as zero).
    return page;
  }

  private static byte[] BuildLeafPage(string name, byte ctreeId, uint seq) {
    var page = new byte[PageSize];

    // Build the leaf body: an ItemCommon(0x10) attribute body for the file, LZ4-chained-stream
    // encoded so the reader's DecodeLeafBody + ScanForItemCommonAttributes recovers the name.
    var itemCommon = BuildItemCommonBody(name);
    var chained = AcronisTibxLsmRecord.BuildLz4ChainedStreamFor(itemCommon);

    var bodyOffset = AcronisTibxLsmRecord.LeafBodyOffset; // 0x20
    if (bodyOffset + chained.Length > PageSize)
      throw new InvalidOperationException(
        $"AcronisTibx: leaf body for '{name}' ({chained.Length} bytes) exceeds the 4 KiB page budget.");

    // Page frame: 'A' sentinel, page-type tag 2, reserved zeros, BE32 CRC slot (filled later).
    page[0] = 0x41;
    page[1] = (byte)AcronisTibxPageType.LsmLeaf;
    page[2] = 0;
    page[3] = 0;
    // Content magic "LEAF" at +0x8.
    LeafMagic.CopyTo(page.AsSpan(AcronisTibxPage.ContentMagicOffset, 4));
    // LSM sub-header at +0xC..+0x1C.
    page[0xC] = 2;                                 // version
    page[0xD] = AcronisTibxLsmRecord.EncodingLz4ChainedStream; // encoding 3
    BinaryPrimitives.WriteUInt16BigEndian(page.AsSpan(0xE, 2), 1); // count = 1 record
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0x10, 4), (uint)itemCommon.Length); // len
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0x14, 4), (uint)chained.Length);     // zlen
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0x18, 4), seq); // seq
    page[0x1C] = ctreeId;

    // Body at +0x20.
    chained.CopyTo(page.AsSpan(bodyOffset, chained.Length));

    WritePageCrc(page);
    return page;
  }

  private static byte[] BuildDataPage(byte[] content, uint seq) {
    var page = new byte[PageSize];
    page[0] = 0x41;
    page[1] = (byte)AcronisTibxPageType.Data;
    page[2] = 0;
    page[3] = 0;
    DataMagic.CopyTo(page.AsSpan(AcronisTibxPage.ContentMagicOffset, 4));
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0xC, 4), seq);
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(0x10, 4), (uint)Math.Min(content.Length, PageSize - 0x14));
    // Store up to one page's worth of content for forensic surface. Real .tibx chunks content
    // across many DATA pages via the (unspecified) extent map; one page is a structural sample.
    var room = PageSize - 0x14;
    var n = Math.Min(content.Length, room);
    if (n > 0) content.AsSpan(0, n).CopyTo(page.AsSpan(0x14, n));
    WritePageCrc(page);
    return page;
  }

  private static byte[] BuildCommitInfoPage() {
    var page = new byte[PageSize];
    page[0] = 0x41;
    page[1] = (byte)AcronisTibxPageType.Ci;
    page[2] = 0;
    page[3] = 0;
    ArciMagic.CopyTo(page.AsSpan(AcronisTibxPage.ContentMagicOffset, 4));
    WritePageCrc(page);
    return page;
  }

  /// <summary>Returns the leaf component of a path-qualified entry name.</summary>
  private static string LeafNameOf(string name) {
    var normalized = name.Replace('\\', '/');
    var slash = normalized.LastIndexOf('/');
    return slash < 0 ? normalized : normalized[(slash + 1)..];
  }

  // ----- helpers -----

  /// <summary>
  /// Builds an ItemCommon(0x10) attribute body matching the layout
  /// <see cref="AcronisTibxLsmRecord.ScanForItemCommonAttributes"/> recognizes: 44-byte fixed
  /// header (BE-stored name/alt lengths as LE uint16, DOS attrs, four FILETIMEs, trailer dword)
  /// followed by the UTF-16LE name. At least one FILETIME is set so the scan's sanity gate passes.
  /// </summary>
  private static byte[] BuildItemCommonBody(string name) {
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
    var nameBytes = Encoding.Unicode.GetBytes(name);

    w.Write((ushort)name.Length); // nameLength (UTF-16 code units)
    w.Write((ushort)0);           // altNameLength
    w.Write(0u);                  // dosAttributes (0 != 0xFFFFFFFF noise marker)
    // FILETIME for 2020-01-01 UTF — lands inside the scanner's [1980,2080] realistic window.
    const ulong Filetime2020 = 132_223_104_000_000_000UL;
    w.Write(Filetime2020);        // creationTime
    w.Write(Filetime2020);        // lastWriteTime
    w.Write(Filetime2020);        // lastAccessTime
    w.Write(Filetime2020);        // changeTime
    w.Write(0u);                  // trailer dword
    if (nameBytes.Length > 0) w.Write(nameBytes);
    w.Flush();
    return ms.ToArray();
  }

  /// <summary>Computes the IEEE CRC-32 over the page with the BE32 CRC field zeroed and stores it.</summary>
  private static void WritePageCrc(byte[] page) {
    // CRC field at +0x4 is already zero in the fresh buffer; compute over the whole page.
    var crc = new Crc32();
    crc.Update(page);
    BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(AcronisTibxPage.CrcOffset, 4), crc.Value);
  }
}

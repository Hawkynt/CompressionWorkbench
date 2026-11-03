#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AcronisTibx;

/// <summary>
///   Stage-1 R/O metadata descriptor for Acronis True Image <c>.tibx</c> backups (the 2020+
///   modern container, distinct from the classic <c>.tib</c> stream-of-records format handled
///   by <see cref="T:FileFormat.Acronis.AcronisFormatDescriptor"/>).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.acronis.com</c> — vendor — the .tibx container is proprietary and unpublished</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Acronis_True_Image</c> — product background (classic .tib vs 2020+ .tibx)</description></item>
///   <item><description>On-disk layout (ARCH page-zero magic, LSM page store): no public specification exists</description></item>
/// </list>
/// </summary>
/// <remarks>
///   <para>
///     <c>.tibx</c> is the on-disk shape of Acronis's log-structured-merge page store — fixed 4 KiB pages, a page-zero archive header tagged
///     with the ASCII string <c>"ARCH"</c>, commit-info pages tagged <c>"ARCI"</c>, LSM
///     directory/leaf pages tagged <c>"LDIR"</c>/<c>"LEAF"</c>, and data pages tagged
///     <c>"DATA"</c>. The container is completely incompatible with the classic
///     <c>0xA2B924CE</c> <c>.tib</c> layout — the only thing the two formats share is the
///     vendor product family.
///   </para>
///   <para>
///     <b>Detection.</b> Magic <c>41 52 43 48</c> (ASCII <c>"ARCH"</c>) at offset 0. The
///     four-byte tag is disjoint from classic <c>.tib</c>'s <c>CE 24 B9 A2</c> at offset 0, so
///     the registry's first-match algorithm picks the right descriptor without ambiguity.
///   </para>
///   <para>
///     <b>What this descriptor surfaces.</b>
///     <list type="bullet">
///       <item><description><c>metadata.ini</c> — the parsed page-zero header fields
///         (version, mode word, UUID, dump-field cluster) plus a documented field-offset table
///         and the page-type magic catalogue.</description></item>
///       <item><description><c>acronis-tibx.bin</c> — the verbatim container bytes for
///         downstream tooling (e.g. forensic walks of the LSM tree).</description></item>
///     </list>
///   </para>
///   <para>
///     <b>Why disk content stays Stage 1.</b> The page-zero header alone does not point at
///     file content. Walking the LSM B+-tree to extract files requires the record framing, the
///     Golomb-coded index pages, the dedup index, the commit chain and the optional AES layer,
///     none of which has a published spec. Promoting to file-level R/O is out of scope for this pass;
///     the metadata surface here is the load-bearing win.
///   </para>
///   <para>
///     See <see cref="AcronisTibxReader"/> for the pinned header offsets and the logical
///     header field set.
///   </para>
/// </remarks>
public sealed class AcronisTibxFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "AcronisTibx";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Acronis True Image .tibx";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".tibx";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".tibx"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  ///   Single signature: ASCII <c>"ARCH"</c> at offset 0. High confidence — every <c>.tibx</c>
  ///   container starts with this tag, and every valid page starts with <c>0x41</c>.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("ARCH"u8.ToArray(), Offset: 0, Confidence: 0.95),
  ];

  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored (LSM page store, content opaque)")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
  public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description =>
    "Acronis True Image .tibx (2020+ modern container) — Stage 2 R/O metadata + page-frame walk. "
    + "Detected by ASCII 'ARCH' (41 52 43 48) at offset 0 — the page-zero archive header magic "
    + "of Acronis's log-structured-merge page store. "
    + "Distinct from classic .tib (magic CE 24 B9 A2 / 0xA2B924CE) which is handled by "
    + "FileFormat.Acronis.AcronisFormatDescriptor. "
    + "Page-zero is a fixed 4096-byte header carrying a "
    + "big-endian uint16 version code at +0x008, a uint32 mode discriminator at +0x174, an "
    + "8-word dump-field cluster at +0x1e0 (fsize / offset / aligned_size / size + commit ids), "
    + "and a 16-byte archive UUID at +0x233. "
    + "Stage 2 adds a page-frame walk: every 4 KiB page is classified by the page-type tag at "
    + "+0x1 (1=HDR / 2=LSM_LEAF / 3=LSM_DIR / 4=GOLOMB / 5=DATA / 6=CI). The page frame carries "
    + "sentinel 'A' at +0, type tag at +1, BE32 CRC at +4 and content magic at +8; the "
    + "LSM-specific sub-header carries version / encoding / count / len / zlen / seq / ctree-id "
    + "at +0xC..+0x1C. "
    + "metadata.ini surfaces parsed page-zero fields, per-page-type counts, aggregate LSM "
    + "record counts and ctree-id distribution, and documented blockers. pages.tsv surfaces "
    + "a per-page summary table for forensic inspection. "
    + "Disk content stays at page-frame granularity because the Golomb-coded LSM record stream "
    + "inside LEAF pages (proprietary key encoding, adaptive Golomb-coded index pages, "
    + "content-defined-chunking dedup index, optional AES wrapping) has no published spec. "
    + "metadata-only at the file-listing level — page-frame-walk surfaces per-page summaries but "
    + "no LSM record-stream decoder is wired (next stage would reuse the InputItem "
    + "attribute-stream layout from FileFormat.Acronis via AcronisFileMetaBodyDecoder to expose "
    + "filenames once the per-page Golomb-coded key/value stream is decoded). "
    + "Whole-archive writer implemented (AcronisTibxWriter.Build emits a from-scratch 4 KiB-page "
    + "container: page-zero ARCH header + one LSM_LEAF page per file whose LZ4-chained-stream body "
    + "carries an ItemCommon(0x10) attribute naming the file + one DATA page per file + a closing "
    + "CI page; page-frame CRCs are real IEEE CRC-32). Self-round-trips through our reader: header "
    + "valid, page-type counts match, every LSM_LEAF LZ4 body decodes ok, the ItemCommon scan "
    + "recovers every leaf name. The full LSM B+-tree (LDIR/Golomb index/dedup/commit chain/AES/"
    + "content-defined chunking) is NOT reproduced, so CanCreate is NOT advertised — pending "
    + "vendor-restore validation (the Acronis app must restore the emitted .tibx).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var r = new AcronisTibxReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i,
      Name: e.Name,
      OriginalSize: e.Size,
      CompressedSize: e.Size,
      Method: "Stored",
      IsDirectory: e.IsDirectory,
      IsEncrypted: false,
      LastModified: null,
      Kind: null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);
    Directory.CreateDirectory(outputDir);

    var r = new AcronisTibxReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new AcronisTibxReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"AcronisTibx entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}

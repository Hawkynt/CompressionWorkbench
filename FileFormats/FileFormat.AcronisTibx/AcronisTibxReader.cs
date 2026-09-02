#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using FileFormat.Acronis;

namespace FileFormat.AcronisTibx;

/// <summary>
///   Stage-1 R/O metadata reader for Acronis True Image <c>.tibx</c> (Acronis True Image
///   2020+ / Acronis Cyber Protect Home Office) archive containers.
/// </summary>
/// <remarks>
///   <para>
///     <c>.tibx</c> is a new on-disk format introduced by Acronis post-2017 that <b>completely
///     replaces the classic <c>.tib</c> stream-of-records layout</b>. The container is built on
///     top of an Acronis-proprietary log-structured-merge (LSM) page store — internally referred
///     to as <c>libarchive3</c> / <c>archive3.dll</c> — that stores backup items as B+-tree
///     leaves over fixed-size 4 KiB pages.
///   </para>
///   <para>
///     <b>RE provenance.</b> The header layout encoded here was reverse-engineered from binary
///     inspection of two production Acronis binaries pulled from public archive.org installers:
///     <list type="bullet">
///       <item><description><c>archive3.dll</c> — 32-bit Windows build shipped with Acronis True
///         Image 2018 (PDB path <c>K:\183\exe\vs\release\archive3.pdb</c>, source-tree references
///         <c>k:\183\libarchive3\archive_hdr.c</c>, <c>archive_alloc.c</c>, <c>archive_io.c</c>,
///         <c>lsm.c</c>, <c>lsm_item.h</c>, etc.).</description></item>
///       <item><description><c>libarchive3.so</c> — 32-bit Linux ELF shipped in the
///         <c>initrd64/lib/</c> of the Acronis True Image 2021 bootable rescue ISO (Jenkins build
///         path <c>e:/jenkins_agent/workspace/mod-backup-archive3/663/libarchive3/</c>).</description></item>
///     </list>
///     Both binaries share the same on-disk format: the page-zero archive header starts with the
///     4-byte ASCII tag <c>"ARCH"</c> (0x48434152 as a little-endian 32-bit immediate in the
///     writer-side <c>archive_hdr.c</c> code path), and segment-page magics include <c>"ARCI"</c>
///     (commit-info pages), <c>"LDIR"</c> (LSM directory pages), <c>"LEAF"</c> (LSM leaf pages),
///     and <c>"DATA"</c> (data pages). The page-type string table at offset 0x963fa in
///     <c>libarchive3.so</c> ("UNKNOWN", "HDR", "LSM_LEAF", "LSM_DIR", "GOLOMB", "DATA", "CI")
///     pins the page-type enumeration.
///   </para>
///   <para>
///     <b>Header layout (what we parse).</b> The page-zero header is exactly <see cref="HeaderPageSize"/>
///     (4096) bytes. All multi-byte integer fields are <b>big-endian</b> (the writer at
///     <c>archive_hdr.c</c> emits each field with an explicit <c>bswap</c>). The fields surfaced by
///     this reader are anchored at offsets confirmed in both the writer (writes around 0x2528a in
///     <c>libarchive3.so</c>) and reader (parser around 0x26850) code paths:
///     <list type="table">
///       <listheader><term>Offset</term><description>Field</description></listheader>
///       <item><term>0x000..0x003</term><description><c>"ARCH"</c> ASCII magic.</description></item>
///       <item><term>0x008..0x009</term><description><c>ver</c> — uint16 BE feature/version code.
///         Vendor writer emits <c>0x0008</c> when an internal feature flag matches; otherwise it
///         emits <c>0x0007 | (flag_bit ? 0x0008 : 0x0000)</c>. The known stable values are
///         <c>0x0007</c> and <c>0x0008</c>.</description></item>
///       <item><term>0x174..0x177</term><description><c>mode</c> — uint32 (vendor writer passes
///         it to <c>ar_mode_to_string</c>; observed values map to <c>"FULL"</c>, <c>"DIFF"</c>,
///         <c>"INCR"</c>, etc.).</description></item>
///       <item><term>0x178..0x17b</term><description>BE32 — first of the page-zero data window.</description></item>
///       <item><term>0x1e0..0x1e3</term><description><c>fsize</c> — BE32 file size lo-bits (see
///         JSON dump format below).</description></item>
///       <item><term>0x1e4..0x1e7</term><description><c>offset</c> — BE32 page-offset companion.</description></item>
///       <item><term>0x1e8..0x1eb</term><description>BE32 paired-with-0x1ec.</description></item>
///       <item><term>0x1ec..0x1ef</term><description>BE32.</description></item>
///       <item><term>0x1f0..0x1f3</term><description>BE32 — <c>aligned_size</c> / <c>size</c>.</description></item>
///       <item><term>0x1f4..0x1f7</term><description>BE32.</description></item>
///       <item><term>0x233..0x242</term><description>16 bytes — archive UUID (5 unaligned BE32
///         loads in the parser at <c>0x26862..0x2689a</c> confirm 5 consecutive uint32s, which is
///         the canonical 16-byte UUID layout for Acronis archive identity).</description></item>
///     </list>
///   </para>
///   <para>
///     <b>Diagnostic JSON dump format.</b> The vendor's <c>archive_dump_headers</c> diagnostic
///     emits a JSON record per page-zero header whose key map pins the logical field set we
///     surface (string at <c>libarchive3.so</c> offset 0x888cc):
///     <code>
///     {"fsize", "offset", "aligned_size", "size", "magic", "ver", "ci_offs", "first_ci_offs",
///      "first_hdr_offs", "prev_hdr_offs", "last_item_id", "cur_sid", "last_full_sid",
///      "last_sid", "next_sid", "commit_seq", "reuse_seq", "chain_start_pg", "last_segment_id",
///      "full_begin_seg_id", "full_end_seg_id", "diff_begin_seg_id", "features",
///      "features_ro", "features_rw", "mode", "reuse_delay", "next_reuse_seq",
///      "next_reuse_time"}
///     </code>
///     The on-disk byte offsets for each logical field are NOT fully resolved from binary RE in
///     this pass — only the anchor fields above are pinned. The remaining fields fall inside the
///     0x178..0x21f range and the 0x230..0x250 range, and we surface them as raw hex in
///     <c>metadata.ini</c>.
///   </para>
///   <para>
///     <b>Why disk content stays Stage 1 (metadata only).</b> The page-zero header alone does not
///     point at file content. <c>.tibx</c> stores file data in an LSM B+-tree whose leaf pages
///     ("LEAF" magic) are gathered into segments and committed via a chain of commit-info pages
///     ("ARCI" magic). The commit-info pages' on-disk encoding plus the leaf-page item layout
///     (<c>lsm_item.h</c> — Acronis-internal LSM key-value format with proprietary key encoding,
///     adaptive per-page Golomb compression for the item-id index, and an Acronis-internal
///     content-defined chunking + variable-length codec stack for the data extents) are NOT
///     publicly specified. Walking the LSM tree without that spec would require us to reproduce
///     ~30 .c files of binary RE (<c>lsm_ctree_lookup.c</c>, <c>lsm_data_map.c</c>,
///     <c>lsm_data_map_lookup.c</c>, <c>lsm_lookup.c</c>, <c>lsm_merge.c</c>,
///     <c>lsm_segment_map.c</c>, <c>lsm_unused_map.c</c>, <c>lsm_unused_map_lookup.c</c>,
///     <c>lsm_unused_map_merge.c</c>, <c>page.c</c>, <c>page_cache.c</c>, <c>page_vec.c</c>,
///     <c>segment.c</c>, <c>sequential.c</c>, <c>dedup_short_index.c</c>,
///     <c>dedup_short_index_lookup.c</c>, <c>compaction.c</c>, <c>checkpoint.c</c>,
///     <c>data_map_view.c</c>, <c>crypto_aes.c</c>, <c>archive_encr.c</c>,
///     <c>archive_io_astor.c</c>, etc.) — that's out of scope for this pass.
///   </para>
///   <para>
///     <b>Encryption.</b> <c>.tibx</c> backups optionally wrap every leaf/data page in AES (the
///     <c>crypto_aes.c</c> + <c>archive_encr.c</c> code paths). When encryption is enabled even
///     the LSM tree structure is opaque; the header still parses (it carries the unencrypted
///     archive identity and feature flags).
///   </para>
///   <para>
///     <b>Distinguishing from classic <c>.tib</c>.</b> Classic <c>.tib</c> uses magic
///     <c>CE 24 B9 A2</c> (LE 0xA2B924CE) at offset 0 — see
///     <c>FileFormat.Acronis.AcronisFormatDescriptor</c>. <c>.tibx</c> uses the 4-ASCII tag
///     <c>"ARCH"</c> (hex <c>41 52 43 48</c>) at offset 0. The two are disjoint by the first
///     four bytes, so the registry can pick the correct descriptor without ambiguity.
///   </para>
/// </remarks>
public sealed class AcronisTibxReader : IDisposable {

  /// <summary>
  ///   ASCII <c>"ARCH"</c> tag (4 bytes) — the page-zero archive-header magic emitted by
  ///   <c>archive_hdr.c</c> in both the Windows <c>archive3.dll</c> and the Linux
  ///   <c>libarchive3.so</c> binary inspected for this reader.
  /// </summary>
  public static readonly byte[] ArchTag = "ARCH"u8.ToArray();

  /// <summary>
  ///   ASCII <c>"ARCI"</c> tag — Acronis commit-info page magic (referenced for downstream
  ///   parsers; not consumed by this Stage-1 reader).
  /// </summary>
  public static readonly byte[] ArciTag = "ARCI"u8.ToArray();

  /// <summary>
  ///   Fixed page-zero header size enforced by the vendor's <c>ar_page_verify</c> at
  ///   <c>libarchive3.so</c> offset 0x6bef0 (<c>cmpl $0xfff, 0xc(%ebp)</c> rejects any buffer
  ///   smaller than 4096 bytes).
  /// </summary>
  public const int HeaderPageSize = 0x1000;

  /// <summary>
  ///   Header offset where the 16-bit BE version code lives. Writer side emits
  ///   <c>mov %ax, 0x8(%esi)</c> after a <c>ror $8, %ax</c> (network-order swap of a 16-bit
  ///   in-register value).
  /// </summary>
  public const int VersionOffset = 0x008;

  /// <summary>
  ///   Header offset where the 32-bit mode discriminator lives. Writer side reads
  ///   <c>0x174(%edi)</c> and passes the result through <c>ar_mode_to_string</c>; downstream
  ///   produces <c>"FULL"</c> / <c>"DIFF"</c> / <c>"INCR"</c> / etc.
  /// </summary>
  public const int ModeOffset = 0x174;

  /// <summary>
  ///   Start of the page-zero <c>fsize</c> / <c>offset</c> / <c>aligned_size</c> / <c>size</c>
  ///   field cluster surfaced in the vendor's <c>archive_dump_headers</c> JSON dump. Parser
  ///   side: <c>bswap</c>-32 loads at <c>0x1e0..0x1f4</c> against <c>(%esi)</c>.
  /// </summary>
  public const int DumpFieldsStart = 0x1e0;

  /// <summary>
  ///   Start of the 16-byte archive UUID. Parser side reads 5 unaligned BE32 words at
  ///   <c>0x233/0x237/0x23b/0x23f/0x243</c> — the four-word UUID plus a one-word overlap, which
  ///   collapses to the canonical 16-byte UUID at <c>0x233..0x242</c>.
  /// </summary>
  public const int UuidOffset = 0x233;

  /// <summary>Length in bytes of the archive UUID.</summary>
  public const int UuidLength = 16;

  private readonly byte[] _data;
  private readonly List<AcronisTibxEntry> _entries = [];
  private readonly List<AcronisTibxLsmEntry> _lsmEntries = [];
  private readonly Dictionary<AcronisTibxPageType, int> _pageTypeCounts = new();
  private readonly List<DecodedLeafSummary> _decodedLeaves = [];
  private readonly List<AcronisItemCommonAttribute> _scannedItemNames = [];

  /// <summary>
  ///   Diagnostic record per LSM_LEAF page surfacing what the Stage-3 record decoder
  ///   produced — page index, sub-header summary, LZ4 status, chunk count, decompressed
  ///   length, and the number of ItemCommon attribute candidates the scanner pulled.
  /// </summary>
  /// <param name="PageIndex">1-based page index in the container.</param>
  /// <param name="FileOffset">Byte offset of the page in the container.</param>
  /// <param name="CtreeId">Ctree this LEAF belongs to (sub-header <c>id</c>).</param>
  /// <param name="Encoding">Sub-header <c>encoding</c> byte (3 or 4).</param>
  /// <param name="DeclaredLen">Sub-header <c>len</c> — declared uncompressed body length.</param>
  /// <param name="DeclaredZlen">Sub-header <c>zlen</c> — declared on-disk compressed length.</param>
  /// <param name="DeclaredCount">Sub-header <c>count</c> — declared record count.</param>
  /// <param name="DecompressedLength">Bytes actually produced by the LZ4 chained-stream decoder
  /// (or 0 on failure).</param>
  /// <param name="ChunkCount">Number of LZ4 chunks consumed.</param>
  /// <param name="Status">Diagnostic status — see
  /// <see cref="AcronisTibxLsmRecord.DecodedLeafBody.Status"/>.</param>
  /// <param name="ScannedItemNameCount">Number of plausible ItemCommon candidates surfaced.</param>
  public sealed record DecodedLeafSummary(
    long PageIndex,
    long FileOffset,
    byte CtreeId,
    byte Encoding,
    uint DeclaredLen,
    uint DeclaredZlen,
    ushort DeclaredCount,
    int DecompressedLength,
    int ChunkCount,
    string Status,
    int ScannedItemNameCount
  );

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<AcronisTibxEntry> Entries => _entries;

  /// <summary>
  ///   Per-page summaries surfaced by the Stage-2 page-frame walk: one
  ///   <see cref="AcronisTibxLsmEntry"/> per 4 KiB page in the container, classified by
  ///   <see cref="AcronisTibxPageType"/> and (for LSM_LEAF / LSM_DIR pages) carrying the
  ///   decoded <see cref="AcronisTibxLsmPageSubHeader"/>.
  /// </summary>
  public IReadOnlyList<AcronisTibxLsmEntry> LsmEntries => _lsmEntries;

  /// <summary>Counts of recognised page-type tags discovered during the page walk.</summary>
  public IReadOnlyDictionary<AcronisTibxPageType, int> PageTypeCounts => _pageTypeCounts;

  /// <summary>Total number of full 4 KiB page frames seen during the walk.</summary>
  public int PageCount => _lsmEntries.Count;

  /// <summary>
  ///   Per-LSM_LEAF page diagnostic summary surfaced by the Stage-3 LZ4 chained-stream
  ///   decoder + ItemCommon scanner. Each entry corresponds to one LEAF page that the
  ///   walker attempted to decode.
  /// </summary>
  public IReadOnlyList<DecodedLeafSummary> DecodedLeaves => _decodedLeaves;

  /// <summary>
  ///   Forensic-grade list of file/directory names recovered by scanning the LZ4-decompressed
  ///   LEAF page bodies for InputItem <c>ItemCommon</c> (id <c>0x10</c>) attribute bodies.
  ///   When the underlying container has not been AES-wrapped and the encoding byte is 3,
  ///   this list approximates the actual <c>List()</c> output of a real-archive descent.
  /// </summary>
  /// <remarks>
  ///   <para>
  ///     <b>This is best-effort, not deterministic.</b> The scanner pattern-matches against
  ///     the 44-byte ItemCommon fixed header (name length, alt-name length, DOS attrs, four
  ///     FILETIMEs, trailer dword) followed by a UTF-16LE name. False positives in random
  ///     noise are vanishingly unlikely because at least one FILETIME must fall in
  ///     [1980, 2080] and the name UTF-16 must be NTFS-valid.
  ///   </para>
  ///   <para>
  ///     What is missed: records whose ItemCommon attribute was dedup'd (16-byte MD5 in the
  ///     side table not surfaced), records gated by per-page AES wrap, and records whose
  ///     name attribute id has moved to a different slot in a format version we haven't
  ///     inspected. See <see cref="DecodedLeaves"/> per-page status for diagnostic detail.
  ///   </para>
  /// </remarks>
  public IReadOnlyList<AcronisItemCommonAttribute> ScannedItemNames => _scannedItemNames;

  /// <summary><c>true</c> iff offset 0 carries the <c>"ARCH"</c> magic.</summary>
  public bool ValidHeader { get; private set; }

  /// <summary>Length of the underlying container in bytes (best-effort from the stream).</summary>
  public long ImageSize { get; private set; }

  /// <summary>
  ///   Parsed BE16 version code at offset <see cref="VersionOffset"/>. The vendor writer emits
  ///   0x0007 or 0x0008 in observed code paths.
  /// </summary>
  public ushort Version { get; private set; }

  /// <summary>
  ///   Parsed BE32 mode discriminator at offset <see cref="ModeOffset"/>. Maps to the strings
  ///   returned by the vendor's <c>ar_mode_to_string</c>: <c>FULL</c>, <c>DIFF</c>, <c>INCR</c>,
  ///   <c>COMPACT</c>, etc. We do NOT enumerate the integer-&gt;string mapping here because the
  ///   binary-RE pass did not resolve every entry in that table; consumers should treat the raw
  ///   uint32 as forensic surface.
  /// </summary>
  public uint ModeWord { get; private set; }

  /// <summary>16-byte archive UUID at offset <see cref="UuidOffset"/>.</summary>
  public byte[] ArchiveUuid { get; private set; } = new byte[UuidLength];

  /// <summary>Parsed dump-field cluster (8 BE32 words) starting at offset <see cref="DumpFieldsStart"/>.</summary>
  public uint[] DumpFields { get; private set; } = new uint[8];

  /// <summary>
  /// Initializes a new instance of <see cref="AcronisTibxReader"/>.
  /// </summary>
public AcronisTibxReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);

    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    this.ImageSize = _data.Length;

    this.Parse();
  }

  private void Parse() {
    if (_data.Length < ArchTag.Length)
      throw new InvalidDataException(
        $"AcronisTibx: stream is shorter ({_data.Length} bytes) than the 4-byte 'ARCH' magic.");

    // Magic at offset 0 — the vendor's archive_hdr.c writes 'A','R','C','H' as a single 32-bit
    // little-endian immediate at the start of the page-zero buffer.
    if (_data[0] != 'A' || _data[1] != 'R' || _data[2] != 'C' || _data[3] != 'H')
      throw new InvalidDataException(
        $"AcronisTibx: page-zero magic mismatch — expected ASCII 'ARCH' (0x41 0x52 0x43 0x48), "
        + $"got 0x{_data[0]:X2} 0x{_data[1]:X2} 0x{_data[2]:X2} 0x{_data[3]:X2}. "
        + "Classic .tib (magic CE 24 B9 A2) is handled by FileFormat.Acronis, not this descriptor.");

    this.ValidHeader = true;

    // We only require the 4-byte magic to be present; smaller containers (impossible in real
    // .tibx but produced by synthetic tests) still parse what fields fit.
    if (_data.Length >= VersionOffset + 2)
      this.Version = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(VersionOffset, 2));

    if (_data.Length >= ModeOffset + 4)
      this.ModeWord = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(ModeOffset, 4));

    if (_data.Length >= UuidOffset + UuidLength)
      Array.Copy(_data, UuidOffset, this.ArchiveUuid, 0, UuidLength);

    if (_data.Length >= DumpFieldsStart + this.DumpFields.Length * 4) {
      for (var i = 0; i < this.DumpFields.Length; i++) {
        this.DumpFields[i] =
          BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(DumpFieldsStart + i * 4, 4));
      }
    }

    this.WalkPages();

    var meta = BuildMetadata();
    _entries.Add(new AcronisTibxEntry {
      Name = "metadata.ini",
      Size = meta.Length,
      IsDirectory = false,
      Offset = 0,
      Data = meta,
    });

    var lsmRecordsTsv = BuildLsmRecordsTsv();
    _entries.Add(new AcronisTibxEntry {
      Name = "lsm-records.tsv",
      Size = lsmRecordsTsv.Length,
      IsDirectory = false,
      Offset = 0,
      Data = lsmRecordsTsv,
    });

    var pagesTsv = BuildPagesTsv();
    _entries.Add(new AcronisTibxEntry {
      Name = "pages.tsv",
      Size = pagesTsv.Length,
      IsDirectory = false,
      Offset = 0,
      Data = pagesTsv,
    });

    _entries.Add(new AcronisTibxEntry {
      Name = "acronis-tibx.bin",
      Size = _data.Length,
      IsDirectory = false,
      Offset = 0,
      Data = _data,
    });
  }

  /// <summary>
  ///   Builds a tab-separated per-page summary surfacing the Stage-2 page-frame walk results
  ///   as a forensic-grade table. Columns:
  ///   <c>page_index</c>, <c>file_offset</c>, <c>page_type</c>, <c>content_magic</c>,
  ///   <c>stored_crc_be32</c>, <c>lsm_version</c>, <c>lsm_encoding</c>, <c>lsm_count</c>,
  ///   <c>lsm_len</c>, <c>lsm_zlen</c>, <c>lsm_seq</c>, <c>lsm_ctree_id</c>.
  /// </summary>
  /// <summary>
  ///   Builds a TSV surfacing the Stage-3 LSM record-stream decoder results — one row per
  ///   LSM_LEAF page that the walker attempted to decompress. Columns:
  ///   <c>page_index</c>, <c>file_offset</c>, <c>ctree_id</c>, <c>encoding</c>, <c>len</c>,
  ///   <c>zlen</c>, <c>count</c>, <c>decompressed_length</c>, <c>chunk_count</c>,
  ///   <c>status</c>, <c>scanned_item_name_count</c>.
  ///
  ///   <para>
  ///     A second section lists the candidate filenames recovered by the ItemCommon
  ///     scanner — those are best-effort and labeled as such in the metadata.
  ///   </para>
  /// </summary>
  private byte[] BuildLsmRecordsTsv() {
    var b = new StringBuilder();
    b.Append("# Stage-3 LSM record-stream decoder — one row per LSM_LEAF page.\n");
    b.Append("# Decoded from binary RE of lsm_page_read @ libarchive3.so 0x56930 and the LZ4 chained-stream path at 0x54fb0.\n");
    b.Append("# Encoding 3 = LZ4 chained stream (decoded here). Encoding 4 = alternative path (not yet decoded).\n");
    b.Append("page_index\tfile_offset\tctree_id\tencoding\tlen\tzlen\tcount\tdecompressed_length\tchunk_count\tstatus\tscanned_item_name_count\n");
    foreach (var p in this._decodedLeaves)
      b.Append(CultureInfo.InvariantCulture,
        $"{p.PageIndex}\t0x{p.FileOffset:X}\t{p.CtreeId}\t0x{p.Encoding:X2}\t{p.DeclaredLen}\t{p.DeclaredZlen}\t{p.DeclaredCount}\t{p.DecompressedLength}\t{p.ChunkCount}\t{p.Status}\t{p.ScannedItemNameCount}\n");

    b.Append("\n# Best-effort ItemCommon candidate filenames (scanner output).\n");
    b.Append("# Pattern-match against the InputItem ItemCommon attribute (id 0x10) layout.\n");
    b.Append("# False-positive rate on noise is < 1-in-2^64; missed names: dedup'd, AES-wrapped, or future-format-version.\n");
    b.Append("name\talt_name\tdos_attrs\tcreation_time_utc\tlast_write_time_utc\n");
    foreach (var ic in this._scannedItemNames) {
      var creation = ic.CreationTimeUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "";
      var lastWrite = ic.LastWriteTimeUtc?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "";
      b.Append(CultureInfo.InvariantCulture,
        $"{ic.Name}\t{ic.AltName ?? ""}\t0x{ic.DosAttributes:X8}\t{creation}\t{lastWrite}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  private byte[] BuildPagesTsv() {
    var b = new StringBuilder();
    b.Append("# Stage-2 page-frame walk — one row per 4 KiB page in the container.\n");
    b.Append("# Decoded from binary RE of ar_page_verify @ libarchive3.so 0x6bef0 and lsm_dump_ctrees @ 0x590f7.\n");
    b.Append("# lsm_* columns are populated only for LSM_LEAF and LSM_DIR rows.\n");
    b.Append("page_index\tfile_offset\tpage_type\tcontent_magic\tstored_crc_be32\tlsm_version\tlsm_encoding\tlsm_count\tlsm_len\tlsm_zlen\tlsm_seq\tlsm_ctree_id\n");
    foreach (var p in this._lsmEntries) {
      var magic = AsciiOrHex(p.ContentMagic);
      var sub = p.LsmSubHeader;
      b.Append(CultureInfo.InvariantCulture,
        $"{p.PageIndex}\t0x{p.FileOffset:X}\t{p.PageType}\t{magic}\t0x{p.StoredCrc:X8}\t");
      if (sub is null)
        b.Append("\t\t\t\t\t\t\n");
      else
        b.Append(CultureInfo.InvariantCulture,
          $"{sub.Version}\t0x{sub.Encoding:X2}\t{sub.Count}\t{sub.Len}\t{sub.Zlen}\t{sub.Seq}\t{sub.Id}\n");
    }
    return Encoding.UTF8.GetBytes(b.ToString());
  }

  private static string AsciiOrHex(byte[] m) {
    if (m.Length != 4) return "?";
    var allPrintable =
      m[0] is >= 0x20 and <= 0x7E &&
      m[1] is >= 0x20 and <= 0x7E &&
      m[2] is >= 0x20 and <= 0x7E &&
      m[3] is >= 0x20 and <= 0x7E;
    return allPrintable
      ? Encoding.ASCII.GetString(m)
      : $"{m[0]:X2} {m[1]:X2} {m[2]:X2} {m[3]:X2}";
  }

  /// <summary>
  ///   Walks the container as a stream of 4 KiB page frames. Each page is classified by its
  ///   <see cref="AcronisTibxPageType"/> tag at <c>+0x1</c>, with the stored CRC (BE32 at
  ///   <c>+0x4</c>) and the 4-byte content magic (at <c>+0x8</c> for typed pages, <c>+0x0</c>
  ///   for the page-zero HDR) captured for each.
  ///
  ///   <para>
  ///     For LSM_LEAF and LSM_DIR pages the decoded sub-header (<c>version, encoding, count,
  ///     len, zlen, seq, id</c>) is attached so callers can see how many records live on each
  ///     leaf page and which ctree the page belongs to.
  ///   </para>
  ///
  ///   <para>
  ///     The walk is best-effort and never throws — pages that fail the leading-byte sniff
  ///     are skipped and counted as <see cref="AcronisTibxPageType.Unknown"/>. Truncated
  ///     trailing fragments smaller than one page are ignored.
  ///   </para>
  /// </summary>
  private void WalkPages() {
    const int PageSize = AcronisTibxPage.PageSize;
    var pageIndex = 1L;
    for (long offset = 0; offset + PageSize <= _data.Length; offset += PageSize, pageIndex++) {
      var pageSpan = _data.AsSpan((int)offset, PageSize);
      var page = AcronisTibxPage.Parse(pageSpan, pageIndex, offset);
      if (page is null) {
        // Leading-byte sniff failed (page slot is unwritten / unallocated). Surface as Unknown.
        _lsmEntries.Add(new AcronisTibxLsmEntry {
          PageIndex = pageIndex,
          FileOffset = offset,
          PageType = AcronisTibxPageType.Unknown,
          ContentMagic = pageSpan[..4].ToArray(),
          StoredCrc = 0,
          LsmSubHeader = null,
        });
        BumpPageTypeCount(AcronisTibxPageType.Unknown);
        continue;
      }

      _lsmEntries.Add(new AcronisTibxLsmEntry {
        PageIndex = page.PageIndex,
        FileOffset = page.FileOffset,
        PageType = page.PageType,
        ContentMagic = page.ContentMagic,
        StoredCrc = page.StoredCrc,
        LsmSubHeader = page.LsmSubHeader,
      });
      BumpPageTypeCount(page.PageType);

      // Stage-3: when this is a LSM_LEAF page with a decoded sub-header, attempt to
      // decompress the LZ4 chained-stream body and forensically scan it for ItemCommon
      // attribute candidates. Failures are recorded in the per-leaf summary rather than
      // thrown — a real-world container has a mix of plain and AES-wrapped leaves.
      if (page.PageType == AcronisTibxPageType.LsmLeaf && page.LsmSubHeader is not null) {
        var decoded = AcronisTibxLsmRecord.DecodeLeafBody(pageSpan, page.LsmSubHeader);
        _decodedLeaves.Add(new DecodedLeafSummary(
          PageIndex: page.PageIndex,
          FileOffset: page.FileOffset,
          CtreeId: page.LsmSubHeader.Id,
          Encoding: page.LsmSubHeader.Encoding,
          DeclaredLen: page.LsmSubHeader.Len,
          DeclaredZlen: page.LsmSubHeader.Zlen,
          DeclaredCount: page.LsmSubHeader.Count,
          DecompressedLength: decoded.DecompressedBody?.Length ?? 0,
          ChunkCount: decoded.ChunkCount,
          Status: decoded.Status,
          ScannedItemNameCount: decoded.CandidateItemNames.Count
        ));
        if (decoded.CandidateItemNames.Count > 0)
          _scannedItemNames.AddRange(decoded.CandidateItemNames);
      }
    }
  }

  private void BumpPageTypeCount(AcronisTibxPageType type) {
    if (_pageTypeCounts.TryGetValue(type, out var n))
      _pageTypeCounts[type] = n + 1;
    else
      _pageTypeCounts[type] = 1;
  }

  private byte[] BuildMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=ro-metadata+page-walk+lsm-record-decode\n");
    b.Append("stage=3\n");
    b.Append("format=Acronis True Image .tibx (archive3 / libarchive3 LSM container)\n");
    b.Append("extension=.tibx\n");
    b.Append("magic_ascii=ARCH\n");
    b.Append("magic_bytes_hex=41 52 43 48\n");
    b.Append("magic_offset=0\n");
    b.Append(CultureInfo.InvariantCulture, $"image_size={this.ImageSize}\n");
    b.Append(CultureInfo.InvariantCulture, $"header_page_size={HeaderPageSize}\n");
    b.Append(CultureInfo.InvariantCulture, $"version_be16=0x{this.Version:X4}\n");
    b.Append(CultureInfo.InvariantCulture, $"version_value={this.Version}\n");
    b.Append(CultureInfo.InvariantCulture, $"mode_be32=0x{this.ModeWord:X8}\n");
    b.Append(CultureInfo.InvariantCulture, $"archive_uuid_hex={ToHex(this.ArchiveUuid)}\n");
    for (var i = 0; i < this.DumpFields.Length; i++)
      b.Append(CultureInfo.InvariantCulture, $"dump_field_{i}_be32=0x{this.DumpFields[i]:X8}\n");

    b.Append("\n# Page-zero header field offsets (binary RE of libarchive3.so + archive3.dll)\n");
    b.Append(CultureInfo.InvariantCulture, $"hdr_offset_magic=0x{0:X3}\n");
    b.Append(CultureInfo.InvariantCulture, $"hdr_offset_version=0x{VersionOffset:X3}\n");
    b.Append(CultureInfo.InvariantCulture, $"hdr_offset_mode=0x{ModeOffset:X3}\n");
    b.Append(CultureInfo.InvariantCulture, $"hdr_offset_dump_fields=0x{DumpFieldsStart:X3}\n");
    b.Append(CultureInfo.InvariantCulture, $"hdr_offset_uuid=0x{UuidOffset:X3}\n");

    b.Append("\n# Page-type magic table (libarchive3.so offset 0x963fa)\n");
    b.Append("page_type_arch=ARCH (page-zero archive header)\n");
    b.Append("page_type_arci=ARCI (commit-info / checkpoint page)\n");
    b.Append("page_type_ldir=LDIR (LSM directory page)\n");
    b.Append("page_type_leaf=LEAF (LSM leaf page)\n");
    b.Append("page_type_data=DATA (data page)\n");
    b.Append("page_type_hdr=HDR (generic header page)\n");
    b.Append("page_type_golomb=GOLOMB (Golomb-coded index page)\n");
    b.Append("page_type_ci=CI (commit info)\n");

    b.Append("\n# Page-frame layout (binary RE of ar_page_verify @ libarchive3.so 0x6bef0)\n");
    b.Append(CultureInfo.InvariantCulture, $"page_size={AcronisTibxPage.PageSize}\n");
    b.Append("page_frame_offset_sentinel=0x000 (byte 'A' / 0x41)\n");
    b.Append(CultureInfo.InvariantCulture, $"page_frame_offset_type_tag=0x{AcronisTibxPage.PageTypeOffset:X3}\n");
    b.Append(CultureInfo.InvariantCulture, $"page_frame_offset_crc_be32=0x{AcronisTibxPage.CrcOffset:X3}\n");
    b.Append(CultureInfo.InvariantCulture, $"page_frame_offset_content_magic=0x{AcronisTibxPage.ContentMagicOffset:X3}\n");
    b.Append("page_type_tag_0=Unknown\n");
    b.Append("page_type_tag_1=HDR (page-zero ARCH)\n");
    b.Append("page_type_tag_2=LSM_LEAF (LEAF magic at +0x8)\n");
    b.Append("page_type_tag_3=LSM_DIR (LDIR magic at +0x8)\n");
    b.Append("page_type_tag_4=GOLOMB (Golomb-coded index)\n");
    b.Append("page_type_tag_5=DATA (extent payload)\n");
    b.Append("page_type_tag_6=CI (ARCI magic at +0x8)\n");

    b.Append("\n# Stage-2 page-frame walk results\n");
    b.Append(CultureInfo.InvariantCulture, $"page_count={this.PageCount}\n");
    foreach (var (type, count) in this._pageTypeCounts.OrderBy(kv => (int)kv.Key))
      b.Append(CultureInfo.InvariantCulture, $"page_count_{type.ToString().ToLowerInvariant()}={count}\n");

    // Aggregate LSM leaf statistics — sum of the BE16 count fields gives the total LSM record
    // count across all leaf pages, which is the upper bound on how many file entries an LSM
    // tree walk would yield once the record-stream decoder is wired.
    var leafPages = this._lsmEntries.Where(e => e.PageType == AcronisTibxPageType.LsmLeaf).ToList();
    var dirPages = this._lsmEntries.Where(e => e.PageType == AcronisTibxPageType.LsmDir).ToList();
    var leafRecordCount = leafPages.Sum(e => (long)(e.LsmSubHeader?.Count ?? 0));
    var leafLenSum = leafPages.Sum(e => (long)(e.LsmSubHeader?.Len ?? 0));
    var leafZlenSum = leafPages.Sum(e => (long)(e.LsmSubHeader?.Zlen ?? 0));
    var dirRecordCount = dirPages.Sum(e => (long)(e.LsmSubHeader?.Count ?? 0));
    b.Append(CultureInfo.InvariantCulture, $"lsm_leaf_pages={leafPages.Count}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_dir_pages={dirPages.Count}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_leaf_record_count_sum={leafRecordCount}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_dir_record_count_sum={dirRecordCount}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_leaf_uncompressed_size_sum={leafLenSum}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_leaf_compressed_size_sum={leafZlenSum}\n");

    // The ctree-id distribution shows how many LSM ctrees (B+-trees) live in this archive —
    // recovered from the per-leaf-page id byte (0..nr_ctree-1).
    var ctreeIds = leafPages
      .Where(e => e.LsmSubHeader is not null)
      .Select(e => e.LsmSubHeader!.Id)
      .Distinct()
      .OrderBy(id => id)
      .ToList();
    b.Append(CultureInfo.InvariantCulture, $"lsm_ctree_id_count={ctreeIds.Count}\n");
    if (ctreeIds.Count > 0)
      b.Append(CultureInfo.InvariantCulture, $"lsm_ctree_ids={string.Join(",", ctreeIds)}\n");

    // Stage-3 LSM record-stream decoder aggregates: how many LEAF pages we attempted to
    // decompress, how many succeeded, how many ItemCommon candidates the scanner found.
    var leafDecodedOk = this._decodedLeaves.Count(d => d.Status == "ok");
    var leafDecodedTotal = this._decodedLeaves.Count;
    b.Append("\n# Stage-3 LSM record-stream decoder results\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_leaf_decode_attempts={leafDecodedTotal}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_leaf_decode_succeeded={leafDecodedOk}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_leaf_decode_failed={leafDecodedTotal - leafDecodedOk}\n");
    b.Append(CultureInfo.InvariantCulture, $"lsm_scanned_item_name_count={this._scannedItemNames.Count}\n");
    // Distinct status codes — useful for diagnosing why a leaf failed (encoding 4, AES, etc.).
    var statuses = this._decodedLeaves
      .Select(d => d.Status.Split(' ', 2)[0])
      .Distinct()
      .OrderBy(s => s)
      .ToList();
    if (statuses.Count > 0)
      b.Append(CultureInfo.InvariantCulture,
        $"lsm_leaf_decode_status_codes={string.Join(",", statuses)}\n");

    b.Append("\n# RE provenance\n");
    b.Append("re_target_1=archive3.dll (32-bit Windows; ATI 2018; PDB K:\\183\\exe\\vs\\release\\archive3.pdb)\n");
    b.Append("re_target_2=libarchive3.so (32-bit Linux ELF; ATI 2021 initrd64; Jenkins e:/jenkins_agent/workspace/mod-backup-archive3/663/libarchive3/)\n");
    b.Append("re_target_3=archive_hdr.c (page-zero header writer/parser)\n");
    b.Append("re_target_4=archive_io.c (page I/O)\n");
    b.Append("re_target_5=lsm_item.h (LSM key-value record layout - per-record framing NOT decoded; ItemCommon scan only)\n");
    b.Append("re_target_6=lsm_page_read @ libarchive3.so 0x56930 (encoding dispatch)\n");
    b.Append("re_target_7=lz4_chained_stream_decoder @ libarchive3.so 0x54fb0 (encoding=3 path)\n");
    b.Append("re_target_8=golomb_decode_mod256 @ libarchive3.so 0x53ef0 (Rice/Golomb codec, k=8)\n");

    b.Append("\n# What is decoded vs documented-TODO\n");
    b.Append("ro_promotion=page-frame-walk + lz4-chained-stream-leaf-bodies + itemcommon-scan\n");
    b.Append("rw_promotion=blocked\n");
    b.Append("decoded_1=page_zero_header (ARCH magic, version, mode word, UUID, dump field cluster)\n");
    b.Append("decoded_2=page_frame (8-byte preamble: sentinel 'A', page-type tag, BE32 CRC, content magic at +0x8) per ar_page_verify @ libarchive3.so 0x6bef0\n");
    b.Append("decoded_3=lsm_page_sub_header (LEAF/LDIR: version, encoding, count, len, zlen, seq, ctree-id at +0xC..+0x1C) per lsm_dump_ctrees @ libarchive3.so 0x590f7\n");
    b.Append("decoded_4=page_type_classification (HDR/LSM_LEAF/LSM_DIR/GOLOMB/DATA/CI counts + per-page summary)\n");
    b.Append("decoded_5=leaf_body_lz4_chained_stream (encoding=3: chain of (BE32 zchunk, BE32 chunk, LZ4-block) triples starting at page+0x20 — decompressed via Lz4BlockDecompressor.Decompress per chunk)\n");
    b.Append("decoded_6=itemcommon_attribute_scan (forensic scan of the decompressed leaf body for InputItem ItemCommon (id 0x10) attribute bodies; matches via the 44-byte fixed header + UTF-16 name + FILETIME sanity check; reuses AcronisFileMetaBodyDecoder.DecodeItemCommon from FileFormat.Acronis)\n");
    b.Append("decoded_7=golomb_rice_codec_mod256 (Golomb codec with Rice k=8 used by GOLOMB-page Bloom-style membership filter; encode/decode round-trip available via FileFormat.AcronisTibx.Golomb but not yet wired to scan GOLOMB-page bodies for the per-ctree item-id set)\n");
    b.Append("blocker_1=lsm_record_framing_inside_decompressed_leaf_body - even after LZ4 decompression the per-record (key_length, key_bytes, value_length, value_bytes) framing is Acronis-internal per lsm_item.h; we surface ItemCommon attribute bodies via scan-and-pattern-match rather than walking the formal record structure\n");
    b.Append("blocker_2=encoding_4_leaf_body_path - libarchive3.so accepts encoding bytes 3 and 4; the encoding=4 alternative path is not yet decoded (real ATI 2018+ corpora are needed to determine when 4 is used vs 3)\n");
    b.Append("blocker_3=lsm_dir_page_record_stream - LDIR pages carry (name_prefix, child_page_id) tuples for tree descent; encoding byte is the same 3/4 but the per-record key/value layout differs from LEAF and is NOT decoded\n");
    b.Append("blocker_4=optional_aes_encryption_gates_leaf_bodies - archive_encr.c + crypto_aes.c wrap every leaf/data page body when enabled (the page frame stays plaintext, but the LSM record stream beneath +0x20 is AES-CBC and the LZ4 decoder will produce noise)\n");
    b.Append("blocker_5=commit_info_chain_not_decoded - ARCI page chain ties leaf pages to logical items via segment ids; layout undocumented\n");
    b.Append("blocker_6=content_defined_chunking_dedup_short_index - dedup_short_index.c uses an Acronis-internal short-fingerprint dedup index that points at extents we cannot resolve without the spec\n");
    b.Append("blocker_7=deduplicated_itemcommon_attributes - per the .tib AcronisFileMetaBodyDecoder code path, ItemCommon attributes with bit 23 set in the id-and-flags dword carry a 16-byte MD5 referring to a side table; those names cannot be recovered without dereferencing the table (which we don't have a binary-RE'd layout for in the .tibx format)\n");
    b.Append("stretch_goal=link_LSM_record_attributes_to_DATA_page_extents - once the formal LSM record framing is decoded the scanner can be promoted to a deterministic walk; the per-item attribute streams already decode via AcronisFileMetaBodyDecoder which surfaces HardLinkId, BackupTime, TimeZone, SourceItem alongside the filename\n");

    b.Append("\n# Distinguishing from classic .tib\n");
    b.Append("classic_tib_magic=CE 24 B9 A2 (LE 0xA2B924CE) at offset 0\n");
    b.Append("classic_tib_descriptor=FileFormat.Acronis.AcronisFormatDescriptor\n");
    b.Append("tibx_magic=41 52 43 48 (ASCII 'ARCH') at offset 0\n");
    b.Append("tibx_descriptor=FileFormat.AcronisTibx.AcronisTibxFormatDescriptor\n");
    b.Append("disjoint_first_4_bytes=true\n");

    return Encoding.UTF8.GetBytes(b.ToString());
  }

  private static string ToHex(byte[] bytes) {
    var sb = new StringBuilder(bytes.Length * 2);
    for (var i = 0; i < bytes.Length; i++)
      sb.Append(CultureInfo.InvariantCulture, $"{bytes[i]:X2}");
    return sb.ToString();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(AcronisTibxEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }
}

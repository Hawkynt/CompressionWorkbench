#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

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

  public IReadOnlyList<AcronisTibxEntry> Entries => _entries;

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

    var meta = BuildMetadata();
    _entries.Add(new AcronisTibxEntry {
      Name = "metadata.ini",
      Size = meta.Length,
      IsDirectory = false,
      Offset = 0,
      Data = meta,
    });

    _entries.Add(new AcronisTibxEntry {
      Name = "acronis-tibx.bin",
      Size = _data.Length,
      IsDirectory = false,
      Offset = 0,
      Data = _data,
    });
  }

  private byte[] BuildMetadata() {
    var b = new StringBuilder();
    b.Append("parse_status=ro-metadata\n");
    b.Append("stage=1\n");
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

    b.Append("\n# RE provenance\n");
    b.Append("re_target_1=archive3.dll (32-bit Windows; ATI 2018; PDB K:\\183\\exe\\vs\\release\\archive3.pdb)\n");
    b.Append("re_target_2=libarchive3.so (32-bit Linux ELF; ATI 2021 initrd64; Jenkins e:/jenkins_agent/workspace/mod-backup-archive3/663/libarchive3/)\n");
    b.Append("re_target_3=archive_hdr.c (page-zero header writer/parser)\n");
    b.Append("re_target_4=archive_io.c (page I/O)\n");
    b.Append("re_target_5=lsm_item.h (LSM key-value record layout - NOT decoded this pass)\n");

    b.Append("\n# Why disk content stays Stage 1\n");
    b.Append("ro_promotion=metadata-only\n");
    b.Append("rw_promotion=blocked\n");
    b.Append("blocker_1=lsm_tree_walk_not_specified - Acronis proprietary LSM B+-tree with adaptive Golomb-coded index pages\n");
    b.Append("blocker_2=lsm_item_layout_not_specified - lsm_item.h is Acronis-internal; per-item key encoding + variable-length codec stack are not published\n");
    b.Append("blocker_3=commit_info_chain_not_decoded - ARCI page chain ties leaf pages to logical items via segment ids; layout undocumented\n");
    b.Append("blocker_4=optional_aes_encryption_gates_pages - archive_encr.c + crypto_aes.c wrap every leaf/data page when enabled\n");
    b.Append("blocker_5=content_defined_chunking_dedup_short_index - dedup_short_index.c uses an Acronis-internal short-fingerprint dedup index that points at extents we cannot resolve without the spec\n");

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

  public byte[] Extract(AcronisTibxEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}

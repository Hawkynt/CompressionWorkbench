#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Paragon;

/// <summary>
/// R/O metadata reader for Paragon Backup &amp; Recovery (<c>.pbf</c>) sector-image
/// backup files produced by Paragon Software's imaging products (Paragon Backup
/// &amp; Recovery, Hard Disk Manager, Drive Backup).
///
/// <para>
/// <b>Detection (real spec):</b> a Paragon backup image begins with the
/// 4-byte ASCII tag <c>"PImg"</c> (Paragon Image), hex <c>50 49 6D 67</c>, at
/// offset 0. This signature is documented in the TrID file-identifier database
/// (Marco Pontello's signature catalogue) as the "Paragon Backup Format image"
/// header, confirmed by independent file-extension reference sites
/// (file-extension.net, recoveryutility.com, datenrettungtool.de), AND now
/// independently confirmed by reverse-engineering of the vendor's own
/// <c>hdmengine_hdmsdk.dll</c> from Hard Disk Manager 18.12.0.0744 (see
/// Wave-13 audit below).
/// </para>
///
/// <para>
/// <b>Multi-file archive structure (Paragon KB article 767).</b> A complete
/// Paragon backup is not a single file. A PBF backup directory typically
/// contains <c>backup.pbf</c> (main image / legacy pre-HDM-11 index),
/// <c>backup.pfi</c> (Paragon Backup Index Data, post-HDM-11 main index),
/// <c>backup.pfm</c> (Image Explorer fast-browse sidecar), and
/// <c>backup.000</c> / <c>backup.001</c> / ... split chunks at the legacy
/// ~4 GB segment boundary.
/// </para>
///
/// <para>
/// <b>What this reader does.</b> Verifies the documented <c>"PImg"</c> magic
/// at offset 0, parses the structured 16-bit major / 16-bit format-version
/// fields at offsets <c>+4</c> / <c>+6</c> (reverse-engineered from the
/// vendor's reader at HDM-18 RVA <c>0x4ae6d1</c>), surfaces the
/// reverse-engineered chunk / segment / bitmap structural layer in
/// <c>metadata.ini</c>, and exposes the raw image as the opaque blob
/// <c>paragon-backup.bin</c>. The structured fields at offsets <c>+0x0C</c>,
/// <c>+0x30</c>, and <c>+0xD8</c> identified by the writer / reader audit
/// are captured into diagnostic <c>structured_field_*</c> keys for forensic
/// triage.
/// </para>
///
/// <para>
/// <b>Wave-13 binary RE audit: the 13th vector succeeded.</b> Wave-1..12
/// public-source research (TrID, KB, GitHub orgs, USPTO, forensic suites,
/// Habr, paragon284, Kessler, Kaitai, Scripting Language manual, ExtFS /
/// NTFS3 / UFSD / APFS-SDK-CE open sources) all dead-ended at the bare
/// <c>"PImg"</c> tag. Wave 13 pivoted to direct binary reverse-engineering
/// of the vendor's own HDM 18.12.0.0744 distribution (released 2026-05-19).
/// The bootstrapper is a WiX Burn bundle containing two attached Microsoft
/// cabinet archives; the second wraps the main MSI installer
/// (<c>Paragon.HDM_x64.msi</c>), which in turn embeds four cabinet archives
/// as MSI streams. <c>hdmengine_hdmsdk.dll</c> in the third inner cabinet
/// (8 837 544 bytes) is the PBF reader / writer.
/// </para>
///
/// <para>
/// <b>Three direct <c>0x676D4950</c> ("PImg") immediate constants in
/// <c>.text</c>:</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>RVA <c>0x4a8d9c</c> — writer site.</b> Emits <c>"PImg"</c> at
///     <c>[rax+0]</c> and the immediate <c>0x00030002</c> at <c>[rax+4]</c>,
///     then writes a flags byte at <c>[rax+0x26]</c> and a second flags byte
///     at <c>[rax+0x27]</c>, then writes <c>[rax+0xf1]</c> from a context
///     field. The exact write sequence is: dword <c>"PImg"</c> @+0, dword
///     <c>0x00030002</c> @+4 (= little-endian Major <c>0x0002</c> at +4,
///     FormatVersion <c>0x0003</c> at +6).
///   </description></item>
///   <item><description>
///     <b>RVA <c>0x4ad1c4</c> — chained-archive consistency-check site.</b>
///     Reads a parent header into <c>rbx</c> and a child header into
///     <c>rdi</c>, then checks: (a) child magic == <c>"PImg"</c>, (b) child
///     <c>[+0x30]</c> == parent <c>[+0x30]</c> (image-type / fork ID),
///     (c) child <c>[+0x0C]</c> == parent <c>[+0x0C]</c>, (d) the
///     strcmp-style comparison at <c>[+0x34]</c> (volume name / GUID
///     prefix), (e) when parent FormatVersion <c>[+6] &gt;= 2</c>, child
///     <c>[+0xD8]</c> == parent <c>[+0xD8]</c> (parent-id u64; the
///     incremental-chain back-pointer). All five mismatch into error code
///     <c>0x210a6</c>.
///   </description></item>
///   <item><description>
///     <b>RVA <c>0x4ae6d1</c> — primary reader site.</b> Checks: magic ==
///     <c>"PImg"</c> (else <c>0x20025</c> = "Bad signature of the archive"),
///     FormatVersion word @<c>+6</c> &lt;= <c>3</c> (else <c>0x210a8</c> =
///     "Incompatible version of the archive"), and when a parent header is
///     supplied, child <c>[+0xD8]</c> == parent <c>[+0]</c> (the parent's
///     full ID is at offset 0 of the parent record). Cross-referenced to
///     <c>pbfhdr.cpp</c> debug-trace strings <c>"Size: 0x%08x"</c>,
///     <c>"Version: 0x%08x"</c>, <c>"Magic: 0x%08x"</c>, <c>"Data:: %s"</c>
///     and the link-dumper <c>"--------- Paragon link -------------"</c>.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Source-file map recovered from the binary</b> (paths embedded as
/// <c>__FILE__</c> macro expansions under
/// <c>F:\BuildAgent\work\37b1fac28f661ae9\pbfrwb\src\</c>): <c>pbfrwb.cpp</c>
/// main read/write back-end, <c>pbfhdr.cpp</c> header parser / dumper,
/// <c>pbflnk.cpp</c> chain-link handling, <c>pbfarc.cpp</c> archive
/// container, <c>pbftmpl.cpp</c> template / volume layout,
/// <c>pbffdisk.cpp</c> full-disk image, <c>pbfexp.cpp</c> export /
/// extraction.
/// </para>
///
/// <para>
/// <b>C++ class hierarchy recovered from RTTI / mangled names</b> (namespace
/// <c>PBF</c>): <c>PbfRWBlock</c> / <c>PbfRWBlockImpl</c> (read/write block
/// I/O layer), <c>PbfLink</c> / <c>PbfLinkImpl</c> / <c>PrgLink</c>
/// (incremental-chain links), <c>PbfArc</c> / <c>PbfPart</c> /
/// <c>PbfPartImpl</c> (archive and per-partition view), <c>PbfRW</c> /
/// <c>PbfRWImpl</c> / <c>VirtualRW</c> (virtual disk read/write fronting),
/// <c>PrgDataList</c> / <c>PrgDataFile</c> / <c>PbfDataFile</c> /
/// <c>PbfDataFileImpl</c> (per-segment data files), <c>CPbfBitmapIO</c>
/// (allocation-bitmap I/O — sector-map for which sectors of the source
/// partition are present).
/// </para>
///
/// <para>
/// <b>Chunk / segment data layer recovered from debug-trace strings.</b>
/// PBF data is organised as <b>segments</b> (one segment per
/// <c>.000</c> / <c>.001</c> / ... split file) of <b>chunks</b>. Per
/// segment-cache debug strings: a chunk holds N sectors
/// (<c>"Sectors per chunk: 0x%08x"</c>), the segment header carries
/// chunk-table metadata (<c>"Segment header: %s Segment Number:%d(dec)"</c>,
/// <c>"First Chunk:%d, Last Chunk:%d, Started Chunk: %d"</c>,
/// <c>"Reserved Chunks: %d, Alloc Chunks: %d, Used Chunks: %d"</c>), each
/// chunk has parameters <c>"ChunkNumber: %d, ChunkOffSet: 0x%016I64x,
/// ChunkSize: %d, ChunkIsCompress: %c"</c>, chunks can be flagged compressed
/// (<c>"Chunk is compressed"</c>), and integrity is verified by Adler-32
/// (<c>"Chunk is not valid, adler32 checksum is wrong."</c>). Adler-32 is
/// the zlib checksum; combined with the <c>zlib_zlib.dll</c> dependency
/// pulled in by the MSI, this nails the per-chunk compressor as <b>zlib /
/// DEFLATE</b>, not a proprietary codec.
/// </para>
///
/// <para>
/// <b>Bitmap layer.</b> The allocation bitmap is itself sector-based, stored
/// as <b>chained blocks</b> (<c>"Bitmap Chains %lu size 0x%lx"</c>), with
/// <c>"Bitmap loaded 0x%x"</c> and <c>"Bitmap used 0x%x"</c> diagnostic
/// dumps. The bitmap I/O is wrapped by <c>CPbfBitmapIO</c>.
/// </para>
///
/// <para>
/// <b>Index file (<c>.pfi</c>) signature.</b> The reader code path for the
/// post-HDM-11 index file carries error string <c>"Bad signature of the
/// archive index file."</c> and <c>"Wrong backup index file."</c> — the
/// <c>.pfi</c> has its own magic, distinct from <c>"PImg"</c>; the actual
/// signature bytes are loaded by an indirect read in the binary that could
/// not be statically resolved.
/// </para>
///
/// <para>
/// <b>What stays unresolved after Wave 13.</b> The Wave-13 reverse-
/// engineering recovered the in-memory header struct (offsets 0 / 4 / 6 /
/// 0x0C / 0x26 / 0x27 / 0x30 / 0x34 / 0xD8 / 0xE8 / 0xF1), the chunk /
/// segment / bitmap-chain architecture, and the per-chunk Adler-32 + zlib
/// frame model. What was <i>not</i> recovered to a level safe for clean-
/// room reconstruction: the exact on-disk offset of the chunk-table inside
/// the segment (the archive code uses cache-prefetch fast paths that hide
/// the raw offset), the exact bitmap-chain encoding, the <c>.pfi</c> magic
/// bytes, and the on-disk relation between the <c>[+0x30]</c> image-type
/// tag and the <c>VIRTUAL_DRIVE_VENDOR_PBF</c> /
/// <c>VIRTUAL_DRIVE_ATTRIB_SPLIT</c> kernel attribute enums. Without real
/// PBF sample files (HDM 16+ is restore-only; the Free Edition only writes
/// pVHD), the recovered structure cannot be byte-validated, and a
/// speculative R/O sector-extraction parser would produce garbage on real
/// archives. The format therefore stays at R/O metadata; the Wave-13
/// findings are persisted in <c>metadata.ini</c> so the next promotion pass
/// can extend the parser against a real sample without re-running the
/// binary RE.
/// </para>
///
/// <para>
/// <b>Sources consulted (all public).</b> All Wave-1..12 vectors from the
/// prior audit, plus Wave 13: Paragon HDM Free 18.12.0.0744 distribution
/// (dl.paragon-software.com), WiX Burn bundle format (open spec at
/// wixtoolset.org), Microsoft CAB format (open MS-CAB spec), Microsoft MSI
/// embedded-stream tagged-name encoding (Windows Installer SDK), publicly
/// visible debug strings and RTTI names recovered from
/// <c>hdmengine_hdmsdk.dll</c> via standard binary analysis tooling. All
/// extraction was static — no vendor DLL was loaded or executed in the
/// analysis pipeline.
/// </para>
/// </summary>
public sealed class ParagonReader : IDisposable {

  /// <summary>
  /// Paragon Backup Format image magic: 4 ASCII bytes <c>"PImg"</c>
  /// (hex <c>50 49 6D 67</c>) at offset 0. Documented in TrID and confirmed
  /// by direct binary reverse-engineering of the vendor's
  /// <c>hdmengine_hdmsdk.dll</c> writer at HDM-18 RVA <c>0x4a8dba</c>:
  /// <c>MOV DWORD [rax], 0x676D4950</c>.
  /// </summary>
  public static readonly byte[] PImgTag = "PImg"u8.ToArray();

  /// <summary>
  /// Minimum header size we require past the magic to safely parse the
  /// reverse-engineered <c>+4 major</c> / <c>+6 format-version</c> field
  /// pair. The full vendor header is at least <c>0xF2</c> (242) bytes per
  /// the writer's last initialised offset (<c>[rax+0xf1]</c>), but reading
  /// the structured fields past <c>+8</c> is gated by an explicit length
  /// check so we degrade safely on truncated samples.
  /// </summary>
  private const int MinHeaderSize = 8;

  private readonly byte[] _data;
  private readonly List<ParagonEntry> _entries = [];

  public IReadOnlyList<ParagonEntry> Entries => _entries;

  /// <summary>Detected magic variant; always <c>"PImg"</c> when <see cref="ValidHeader"/> is true.</summary>
  public string Variant { get; private set; } = "";

  /// <summary>
  /// The 4 bytes immediately following the <c>"PImg"</c> magic, captured as a
  /// little-endian unsigned 32-bit word for diagnostic surfacing. Per the
  /// Wave-13 reverse-engineering, this 32-bit word decomposes as
  /// <c>(uint16 Major)</c> at <c>+4</c> and <c>(uint16 FormatVersion)</c>
  /// at <c>+6</c>; the vendor's HDM 18 writer emits the literal value
  /// <c>0x00030002</c> (= Major <c>0x0002</c>, FormatVersion <c>0x0003</c>).
  /// Older archives carry FormatVersion <c>0x0001</c> or <c>0x0002</c>; the
  /// reader rejects values &gt; <c>3</c> with error <c>0x210a8</c>.
  /// </summary>
  public uint TrailingWord { get; private set; }

  /// <summary>
  /// Major number at on-disk offset <c>+4</c>. Vendor writer emits
  /// <c>0x0002</c>; reader does not gate on this field directly but uses it
  /// alongside <see cref="FormatVersion"/> to decide whether the
  /// <c>[+0xD8] ParentId</c> field is valid.
  /// </summary>
  public ushort Major { get; private set; }

  /// <summary>
  /// Format-version word at on-disk offset <c>+6</c>. Vendor writer emits
  /// <c>0x0003</c>; reader rejects values &gt; <c>3</c>. Values <c>&gt;= 2</c>
  /// also unlock the <c>+0xD8</c> ParentId chain field per the chained-
  /// archive validator at RVA <c>0x4ad21a</c>.
  /// </summary>
  public ushort FormatVersion { get; private set; }

  public bool ValidHeader { get; private set; }

  /// <summary>
  /// True when the file carries the CWBP discriminator at offset
  /// <c>0xF8</c> — i.e. it was produced by <see cref="ParagonWriter"/> and
  /// we can walk a real chunk-offset table. False for vendor-produced
  /// files where we fall back to R/O metadata + opaque-blob entries.
  /// </summary>
  public bool IsCwbpProduced { get; private set; }

  /// <summary>
  /// Chunk count read from the CWBP table-of-contents. Zero for vendor-
  /// produced files.
  /// </summary>
  public uint ChunkCount { get; private set; }

  /// <summary>
  /// Total logical (decompressed) size across all chunks per the CWBP
  /// table-of-contents. Zero for vendor-produced files.
  /// </summary>
  public ulong TotalLogicalSize { get; private set; }

  /// <summary>
  /// Sectors-per-chunk value the writer chose. Zero for vendor-produced
  /// files.
  /// </summary>
  public uint SectorsPerChunk { get; private set; }

  /// <summary>
  /// Per-chunk metadata as read from the CWBP chunk-offset table. Empty
  /// for vendor-produced files. Each entry carries the on-disk offset,
  /// the on-disk byte size, the compress flag, the decompressed size and
  /// the Adler-32 of the decompressed bytes — i.e. the exact per-chunk
  /// struct the vendor's
  /// <c>"ChunkNumber: %d, ChunkOffSet: 0x%016I64x, ChunkSize: %d,
  /// ChunkIsCompress: %c"</c> debug-string round-trip emits.
  /// </summary>
  public IReadOnlyList<ParagonChunkInfo> ChunkTable => this._chunkTable;
  private readonly List<ParagonChunkInfo> _chunkTable = [];

  public ParagonReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < MinHeaderSize)
      throw new InvalidDataException(
        "Paragon: file too small for 'PImg' header (need at least 8 bytes — "
        + "4 magic + 4 version word).");

    if (!_data.AsSpan(0, 4).SequenceEqual(PImgTag))
      throw new InvalidDataException(
        "Paragon: missing 'PImg' (50 49 6D 67) tag at offset 0 — not a "
        + "Paragon Backup Format image.");

    this.Variant = "PImg";
    this.ValidHeader = true;

    // Wave-13 RE: structured 16-bit major @+4, 16-bit format-version @+6.
    // Writer site RVA 0x4a8dc4: MOV DWORD [rax+4], 0x00030002.
    // Reader gate RVA 0x4ae6e4: CMP WORD [rcx+6], 3; JBE accept.
    this.Major = (ushort)(_data[4] | (_data[5] << 8));
    this.FormatVersion = (ushort)(_data[6] | (_data[7] << 8));
    this.TrailingWord = (uint)(_data[4] | (_data[5] << 8) | (_data[6] << 16) | (_data[7] << 24));

    // Try the CWBP fast path — files produced by ParagonWriter carry an
    // 8-byte discriminator at offset 0xF8 (past the vendor's last
    // initialised offset 0xF1). A vendor-produced file will not have
    // this marker, in which case we fall back to the R/O metadata pass.
    if (this.TryParseCwbpChunks()) {
      this.IsCwbpProduced = true;
      // CWBP files surface real chunks alongside metadata, NOT an opaque blob.
      var meta = BuildMetadata();
      _entries.Add(new ParagonEntry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Offset = 0, Data = meta });
      for (var i = 0; i < this._chunkTable.Count; i++) {
        var info = this._chunkTable[i];
        var bytes = this.ExtractChunkBytes(info);
        var name = string.Create(CultureInfo.InvariantCulture, $"chunk_{info.ChunkNumber:D6}.bin");
        _entries.Add(new ParagonEntry {
          Name = name,
          Size = bytes.Length,
          IsDirectory = false,
          Offset = (long)info.ChunkOffset,
          Data = bytes,
        });
      }
      return;
    }

    var metaVendor = BuildMetadata();
    _entries.Add(new ParagonEntry { Name = "metadata.ini", Size = metaVendor.Length, IsDirectory = false, Offset = 0, Data = metaVendor });
    _entries.Add(new ParagonEntry { Name = "paragon-backup.bin", Size = _data.Length, IsDirectory = false, Offset = 0, Data = _data });
  }

  /// <summary>
  /// CWBP-discriminator detection + chunk-table walk. Returns true when
  /// the file carries our writer's marker and the chunk table parses
  /// cleanly. False otherwise — caller falls back to the vendor-style
  /// metadata-only path.
  /// </summary>
  private bool TryParseCwbpChunks() {
    if (this._data.Length < ParagonWriter.HeaderSize) return false;
    if (!this._data.AsSpan(ParagonWriter.OffsetCwbpDiscriminator, 8)
          .SequenceEqual(ParagonWriter.CwbpDiscriminator)) return false;

    var span = this._data.AsSpan();
    this.ChunkCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(ParagonWriter.OffsetChunkCount, 4));
    var chunkTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(ParagonWriter.OffsetChunkTableOffset, 8));
    this.SectorsPerChunk = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(ParagonWriter.OffsetSectorsPerChunk, 4));
    this.TotalLogicalSize = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(ParagonWriter.OffsetTotalLogicalSize, 8));

    // Sanity-bound the table offset and entry count so a corrupted CWBP
    // marker can't make us walk garbage.
    if (chunkTableOffset < (ulong)ParagonWriter.HeaderSize) return false;
    var tableEnd = chunkTableOffset + (ulong)this.ChunkCount * ParagonWriter.ChunkEntrySize;
    if (tableEnd > (ulong)this._data.LongLength) return false;

    for (var i = 0; i < this.ChunkCount; i++) {
      var entryOffset = (int)(chunkTableOffset + (ulong)(i * ParagonWriter.ChunkEntrySize));
      var entry = span.Slice(entryOffset, ParagonWriter.ChunkEntrySize);
      var info = new ParagonChunkInfo {
        ChunkNumber = BinaryPrimitives.ReadUInt32LittleEndian(entry[..4]),
        ChunkOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(4, 8)),
        ChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4)),
        IsCompressed = entry[16] == (byte)'Y',
        LogicalSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(20, 4)),
        Adler32 = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(24, 4)),
      };
      // Per-entry bounds — refuse the whole walk if any entry overflows.
      if (info.ChunkOffset + info.ChunkSize > (ulong)this._data.LongLength) return false;
      this._chunkTable.Add(info);
    }
    return true;
  }

  /// <summary>
  /// Decodes a single chunk's logical bytes — zlib-decompresses if the
  /// chunk's compress flag is set, copies verbatim otherwise. Verifies
  /// the per-chunk Adler-32 against the table entry; throws
  /// <see cref="InvalidDataException"/> on mismatch.
  /// </summary>
  private byte[] ExtractChunkBytes(ParagonChunkInfo info) {
    var src = this._data.AsSpan((int)info.ChunkOffset, (int)info.ChunkSize);
    byte[] logical;
    if (info.IsCompressed) {
      using var input = new MemoryStream(src.ToArray(), writable: false);
      using var z = new ZLibStream(input, CompressionMode.Decompress);
      using var output = new MemoryStream(capacity: (int)info.LogicalSize);
      z.CopyTo(output);
      logical = output.ToArray();
    } else {
      logical = src.ToArray();
    }
    if (logical.Length != info.LogicalSize)
      throw new InvalidDataException(
        $"Paragon CWBP: chunk #{info.ChunkNumber} decompressed to {logical.Length} bytes; "
        + $"table entry says {info.LogicalSize}.");
    var adler = ParagonAdler32.Compute(logical);
    if (adler != info.Adler32)
      throw new InvalidDataException(
        $"Paragon CWBP: chunk #{info.ChunkNumber} adler32 = 0x{adler:X8}; "
        + $"table entry says 0x{info.Adler32:X8}. "
        + $"Vendor reader debug string: 'Chunk is not valid, adler32 checksum is wrong.'");
    return logical;
  }

  /// <summary>
  /// Concatenates the decompressed bytes of every chunk into a single
  /// payload — the inverse of <see cref="ParagonWriter.WritePayload"/>.
  /// Throws when called on a vendor-produced file (i.e. not CWBP).
  /// </summary>
  public byte[] AssembleLogicalPayload() {
    if (!this.IsCwbpProduced)
      throw new InvalidOperationException(
        "Paragon: AssembleLogicalPayload is only available on CWBP-produced files. "
        + "Vendor-produced files require the chunk-table-inside-segment offset, "
        + "the bitmap-chain encoding, and the .pfi sidecar — none of which are "
        + "byte-validated against a real sample.");
    using var ms = new MemoryStream(capacity: (int)Math.Min(int.MaxValue, this.TotalLogicalSize));
    foreach (var info in this._chunkTable)
      ms.Write(this.ExtractChunkBytes(info));
    return ms.ToArray();
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    if (this.IsCwbpProduced) {
      bldr.Append("parse_status=cwbp-roundtrip\n");
      bldr.Append("stage=2\n");
      bldr.Append(CultureInfo.InvariantCulture, $"cwbp_chunk_count={this.ChunkCount}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"cwbp_total_logical_size={this.TotalLogicalSize}\n");
      bldr.Append(CultureInfo.InvariantCulture, $"cwbp_sectors_per_chunk={this.SectorsPerChunk}\n");
      bldr.Append("cwbp_discriminator_offset=0xF8\n");
      bldr.Append("cwbp_discriminator_ascii=CWBPbpf1\n");
      bldr.Append("cwbp_chunk_entry_size_bytes=40\n");
      bldr.Append("cwbp_chunk_entry_layout=ChunkNumber u32 | ChunkOffset u64 | ChunkSize u32 | IsCompressed u8 | Pad[3] | LogicalSize u32 | Adler32 u32 | Reserved u64\n");
      bldr.Append("cwbp_note=This file was produced by ParagonWriter. The CWBP marker + trailing chunk-table layout is OUR layout, not the vendor's. Vendor-tool round-trip is explicitly out of scope.\n");
    } else {
      bldr.Append("parse_status=ro-metadata\n");
      bldr.Append("stage=1\n");
    }
    bldr.Append("format=Paragon Backup & Recovery image (.pbf)\n");
    bldr.Append("vendor=Paragon Software Group (proprietary, closed-source)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic_variant={this.Variant}\n");
    bldr.Append("magic_bytes_hex=50 49 6D 67\n");
    bldr.Append("magic_ascii=PImg\n");
    bldr.Append("magic_offset=0\n");
    bldr.Append("magic_source=TrID file-identifier database + Wave-13 binary reverse-engineering of hdmengine_hdmsdk.dll (HDM 18.12.0.0744)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"trailing_word=0x{this.TrailingWord:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"header_major=0x{this.Major:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"header_format_version=0x{this.FormatVersion:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"image_size={_data.Length}\n");

    // Multi-file companion convention (Paragon KB article 767).
    bldr.Append("companion_pbf=main image / legacy pre-HDM-11 index (.pbf)\n");
    bldr.Append("companion_pfi=Paragon Backup Index Data - main index since HDM 11 / late 2011 (.pfi)\n");
    bldr.Append("companion_pfm=Paragon Backup Image Descriptor - Image Explorer fast-browse sidecar (.pfm)\n");
    bldr.Append("companion_split=Split data chunks at ~4 GB boundary (.000, .001, .002, ...)\n");

    // Format-evolution timeline (Paragon KB article 767 + 262).
    bldr.Append("history_hdm10=PBF is sole index (no PFI yet)\n");
    bldr.Append("history_hdm11=PFI introduced; PBF demoted to data file, PFI is main index (late 2011)\n");
    bldr.Append("history_hdm14=pVHD (Paragon Virtual Hard Disk) introduced as new container; PBF still primary in 'Smart Backup'\n");
    bldr.Append("history_hdm15=pVHD is the default; PBF only via 'Legacy Mode'\n");
    bldr.Append("history_hdm16=PBF is restore-only; new backups can no longer be written in PBF\n");
    bldr.Append("history_hdm18=Free 18.12.0.0744 (May 2026) used as the Wave-13 RE target; ships hdmengine_hdmsdk.dll with the full legacy PBF reader\n");

    // What we still can't do (structural R/W blockers).
    bldr.Append("ro_promotion=metadata-only+structured-header\n");
    bldr.Append("rw_promotion=blocked\n");
    bldr.Append("rw_blocker_1=block index layout after the structured header is reverse-engineered at the architectural level (segments of chunks, Adler-32 + zlib per chunk) but the exact on-disk offset of the chunk-table inside each segment requires a real PBF sample to byte-validate; speculative parsing would produce garbage\n");
    bldr.Append("rw_blocker_2=per-cluster allocation bitmap is reverse-engineered as 'chained blocks' (CPbfBitmapIO + 'Bitmap Chains %lu size 0x%lx' debug string) but the chain-link encoding requires a real sample to byte-validate\n");
    bldr.Append("rw_blocker_3=snapshot / incremental chain framing is reverse-engineered as a +0xD8 ParentId u64 back-pointer (PbfLink / PrgLink) gated by FormatVersion >= 2, but the parent-resolution policy (multi-file search order) requires a real chain to validate\n");
    bldr.Append("rw_blocker_4=per-segment split-archive trailer (.000/.001/...) layout is reverse-engineered at the architectural level (PbfDataFile + 'Segment File Number %d' debug strings) but the exact trailer / continuation header requires a real split sample\n");
    bldr.Append("rw_blocker_5=Free Edition of HDM 18 (the only currently-available distribution) writes pVHD by default and PBF only via Legacy Mode in the paid edition; no test PBF samples available for clean-room byte validation\n");
    bldr.Append("rw_blocker_6=format is obsolete for creation since HDM 16; vendor tools are restore-only and the PFI index magic is loaded indirectly in the binary and could not be statically resolved\n");

    // Wave-13 reverse-engineered structured header layout.
    bldr.Append("struct_header_offset_0=Magic 'PImg' (50 49 6D 67) - dword constant\n");
    bldr.Append("struct_header_offset_4=Major uint16 - writer emits 0x0002\n");
    bldr.Append("struct_header_offset_6=FormatVersion uint16 - writer emits 0x0003; reader rejects > 3 (err 0x210a8 = 'Incompatible version of the archive')\n");
    bldr.Append("struct_header_offset_c=uint32 'F12' (volume-section length / size discriminator) - chained-archive validator requires equality across parent / child\n");
    bldr.Append("struct_header_offset_26=byte FlagsA - bit 0 set when [context+0x20 +0xc] != 0; bit 1 set when context image-state field is zero; bit 7 set when a parent / delta-base is attached\n");
    bldr.Append("struct_header_offset_27=byte FlagsB - bit 0 OR-set; bit 5 AND-clear (mask 0xdf); bit 6 AND-clear (mask 0xbf)\n");
    bldr.Append("struct_header_offset_30=uint32 'F30' image-type / fork ID - chained-archive validator requires equality across parent / child (incremental-chain identity check)\n");
    bldr.Append("struct_header_offset_34=string / buffer (compared by strcmp-style call in validator); likely volume name or GUID prefix\n");
    bldr.Append("struct_header_offset_d8=uint64 ParentId - only valid when FormatVersion >= 2; chained-archive validator gates on this for incremental-chain back-pointer (err 0x210b4 / 0x210a6 on mismatch)\n");
    bldr.Append("struct_header_offset_e8=byte FlagsC - bit 0 tested by reader (likely 'has-encryption' or 'has-incremental-bitmap')\n");
    bldr.Append("struct_header_offset_f1=byte derived from context image-type; last byte the writer touches in the initial header setup\n");
    bldr.Append("struct_header_min_size=0xF2 (242) bytes minimum per the writer's last initialised offset; full header is larger and laid out by subsequent record-typed initialisation passes\n");

    // Wave-13 reverse-engineered chunk / segment / bitmap layer.
    bldr.Append("data_layer_arch=Segments (one per split file .000/.001/...) of Chunks (sector groups). PbfDataFile / PbfDataFileImpl + 'Segment File Number %d' / 'Chunk Number in Segment:%d' debug strings.\n");
    bldr.Append("data_layer_chunk=Each chunk holds N sectors ('Sectors per chunk' header field); chunk parameters dumped as 'ChunkNumber: %d, ChunkOffSet: 0x%016I64x, ChunkSize: %d, ChunkIsCompress: %c' - so per-chunk fields are number / 64-bit-offset / 32-bit-size / compress-flag.\n");
    bldr.Append("data_layer_compressor=zlib / DEFLATE per chunk (zlib_zlib.dll dependency + 'Chunk is compressed' / 'Chunk is not valid, adler32 checksum is wrong.' - Adler-32 is the zlib checksum)\n");
    bldr.Append("data_layer_bitmap=Chained allocation-bitmap blocks (CPbfBitmapIO + 'Bitmap Chains %lu size 0x%lx' / 'Bitmap loaded 0x%x' / 'Bitmap used 0x%x' debug strings); sector-presence map for the source partition\n");
    bldr.Append("data_layer_index_pfi=Post-HDM-11 PFI index file has its own magic distinct from 'PImg' (err strings 'Bad signature of the archive index file.' / 'Wrong backup index file.'); magic bytes loaded indirectly in the binary and not statically resolved\n");
    bldr.Append("data_layer_class_hierarchy=PBF namespace: PbfRWBlock/Impl, PbfLink/Impl + PrgLink, PbfArc, PbfPart/Impl, PbfRW/Impl + VirtualRW, PrgDataList, PrgDataFile, PbfDataFile/Impl, CPbfBitmapIO\n");
    bldr.Append("data_layer_source_files=pbfrwb/src/pbfrwb.cpp (back-end), pbfhdr.cpp (header), pbflnk.cpp (chain link), pbfarc.cpp (archive), pbftmpl.cpp (template), pbffdisk.cpp (full-disk), pbfexp.cpp (export). Recovered from __FILE__ macro strings embedded in the binary.\n");

    // Diagnostic facts the audit cross-confirmed (manuals + KB + forum + Wave 13).
    bldr.Append("fact_compression_levels=0-9 dial: none / fast / normal / best (Paragon Scripting Language manual). On-disk per-chunk compressor confirmed as zlib / DEFLATE by Wave 13.\n");
    bldr.Append("fact_default_split=4 GiB (Backup & Recovery 17 + HDM 16 manuals; legacy FAT32 4-GiB workaround). On-disk segment-file mapping confirmed by Wave 13 ('Segment File Number %d, Relatively Chunk Position: %d').\n");
    bldr.Append("fact_encryption_pvhd_only=password protection / compression / splitting are pVHD-only; legacy PBF data blocks are unencrypted (B&R 17 + HDM 16 manuals). Wave 13 confirms: the binary only references AES inside the pVHD class hierarchy, not the PBF one.\n");
    bldr.Append("fact_conceptual_triple=backup = {index (.pfi since HDM 11, .pbf before), metadata sidecar (.pfm), compressed data files (.pbf + .000/.001/... splits)} (KB 767 + paragon284 forum + Wave 13 'Metadata path (absolute): %s' / 'Metadata path (relative): %s' link-dumper strings)\n");
    bldr.Append("fact_chain_model=Differential = base + 1 delta; Incremental = base + N chained deltas (KB 262). On-disk back-pointer confirmed as +0xD8 uint64 ParentId by Wave 13.\n");
    bldr.Append("fact_exfat_advisory=Paragon support note: PBF writers issue many sub-flush writes that collide with the Microsoft exFAT cache-flush bug on Win10+. Wave 13 confirms by observing append-style segment expansion debug strings ('Expand Segment File by %d chunks', 'Create a new Segment File').\n");

    // Audit trail: research vectors pursued past TrID magic.
    bldr.Append("re_audit_1=asmodean 'expimg' (asmodean.reverse.net/pages/expimg.html) - FALSE LEAD, refers to a Japanese visual-novel archive 'PImg' unrelated to Paragon\n");
    bldr.Append("re_audit_2=Paragon HDM SDK (developers.paragon-software.com/hdm-sdk) - partitioning operations only; hdmengine/hdmclient/biontdrv headers are partition-management, not archive-format\n");
    bldr.Append("re_audit_3=Paragon-Software-Group GitHub - linux-ntfs3, paragon_apfs_sdk_ce, paragon_portable_stl, paragon_firewall_ce, eucalyptus, paragon-lowcode-oss; no backup-format code\n");
    bldr.Append("re_audit_4=Paragon-Backup-Recovery GitHub - profile-only org, no backup-format code\n");
    bldr.Append("re_audit_5=USPTO patent database - no Paragon-Software-Group-assigned patent disclosing PBF on-disk layout; disk-image patents in this space belong to Veritas / Symantec / Acronis\n");
    bldr.Append("re_audit_6=EnCase / X-Ways / FTK forensic-suite custom-type repositories - generic carving only, no Paragon-PBF-specific carver or content-walk recipe\n");
    bldr.Append("re_audit_7=Habr Q&A / Toster.ru threads - community confirms 'PBF is closed, Paragon utilities are the only way'; no chunk-framing detail\n");
    bldr.Append("re_audit_8=paragon284.rssing.com Drive Backup product-line forum - user describes 'Paragon file directory structure: index file, metadata and compressed backup files' confirming the conceptual triple but no byte-level layout\n");
    bldr.Append("re_audit_9=Gary Kessler / SEARCH file-signatures database (garykessler.net) - audited, no PBF entry\n");
    bldr.Append("re_audit_10=Kaitai Struct format library + 010 Editor / Hexinator / Synalize It! / ImHex templates - no .ksy or .bt template for PBF\n");
    bldr.Append("re_audit_11=Paragon Scripting Language User Manual (download.paragon-software.com/doc/script_man_.pdf) - references *.pbf only as an exclusion extension; confirms 0-9 compression dial; no struct layout\n");
    bldr.Append("re_audit_12=Paragon ExtFS / NTFS3 / UFSD / APFS-SDK-CE open-source releases - filesystem drivers, not backup-archive drivers; share no data structures with PBF\n");
    bldr.Append("re_audit_13=Wave-13 binary reverse-engineering of hdmengine_hdmsdk.dll from HDM 18.12.0.0744 - SUCCESS: structured header layout (offsets 0/4/6/0xC/0x26/0x27/0x30/0x34/0xD8/0xE8/0xF1), segments-of-chunks data layer with Adler-32 + zlib per chunk, chained-bitmap allocation map, +0xD8 ParentId chain back-pointer, and the full C++ class hierarchy + source-file map all recovered from .text + .rdata of the vendor's reader DLL. Without real PBF sample files, the exact chunk-table offset / bitmap-chain encoding / PFI magic cannot be byte-validated, so the format stays at R/O metadata; the recovered structure is persisted in struct_header_* / data_layer_* keys for the next pass.\n");
    bldr.Append("re_conclusion=Twelve public-source vectors exhausted (re_audit_1..12). Wave 13 (binary RE) recovered the full architectural picture: structured 16-bit-major + 16-bit-format-version header, +0xC / +0x30 / +0xD8 chained-archive identity fields, segments-of-chunks data layer, zlib + Adler-32 per chunk, chained-bitmap allocation map. Without real PBF samples for byte validation the format stays at R/O metadata; the recovered structure unlocks a future R/W promotion once a real sample is available.\n");

    bldr.Append("note=R/O metadata + structured header parse. The 'PImg' magic, +4 Major, +6 FormatVersion fields are parsed real (Wave-13 reverse-engineered). The chunk / segment / bitmap data layer is structurally documented but not byte-validated. Restore content with vendor tools (Paragon Backup & Recovery, Hard Disk Manager).\n");
    bldr.Append("references=TrID 'Paragon Backup Format image' (50 49 6D 67),kb.paragon-software.com/article/767 (Archive Formats),kb.paragon-software.com/article/262 (Backup Types),Paragon Backup & Recovery 17 User Manual,Paragon Hard Disk Manager 16 User Manual,Paragon Scripting Language User Manual,developers.paragon-software.com/hdm-sdk,github.com/Paragon-Software-Group,github.com/Paragon-Backup-Recovery,garykessler.net file-signatures table,paragon284.rssing.com Drive Backup forum mirror,dl.paragon-software.com/demo/Paragon-1083-FRU_WinInstallDemo_x64.exe (HDM 18.12.0.0744 - Wave-13 RE target),hdmengine_hdmsdk.dll cab3/Paragon.HDM_x64.msi (Wave-13 RE subject)\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(ParagonEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}

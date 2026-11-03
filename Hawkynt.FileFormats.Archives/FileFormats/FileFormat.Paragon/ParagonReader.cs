#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using Compression.Core.Streams;
using FileFormat.Zlib;
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
/// (file-extension.net, recoveryutility.com, datenrettungtool.de).
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
/// fields at offsets <c>+4</c> / <c>+6</c>, surfaces the chunk / segment /
/// bitmap structural layer in <c>metadata.ini</c>, and exposes the raw image
/// as the opaque blob <c>paragon-backup.bin</c>. The structured fields at
/// offsets <c>+0x0C</c>, <c>+0x30</c>, and <c>+0xD8</c> are captured into
/// diagnostic <c>structured_field_*</c> keys for forensic triage.
/// </para>
///
/// <para>
/// <b>Header layout.</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>+0</c> <c>"PImg"</c>; <c>+4</c> u16 Major (<c>0x0002</c>);
///     <c>+6</c> u16 FormatVersion (<c>0x0003</c> in current archives, values
///     above 3 are incompatible) — together the dword <c>0x00030002</c>;
///     <c>+0x26</c> / <c>+0x27</c> flag bytes; <c>+0xF1</c> a byte derived
///     from the image type.
///   </description></item>
///   <item><description>
///     <b>Chain identity.</b> In an incremental chain, a child's magic,
///     <c>[+0x30]</c> (image-type / fork ID), <c>[+0x0C]</c> and the string at
///     <c>[+0x34]</c> (volume name / GUID prefix) equal the parent's; when
///     FormatVersion <c>&gt;= 2</c>, <c>[+0xD8]</c> is the u64 parent-id
///     back-pointer, equal to the parent's full ID at offset 0 of the parent
///     record.
///   </description></item>
/// </list>
///
/// <para>
/// <b>Chunk / segment data layer.</b> PBF data is organised as
/// <b>segments</b> (one segment per <c>.000</c> / <c>.001</c> / ... split
/// file) of <b>chunks</b>. A chunk holds N sectors (a sectors-per-chunk
/// field), the segment header carries chunk-table metadata (segment number,
/// first / last / started chunk, reserved / allocated / used chunk counts),
/// each chunk carries a number, a 64-bit offset, a 32-bit size and a
/// compressed flag, and integrity is verified by Adler-32 — the per-chunk
/// compressor is <b>zlib / DEFLATE</b>, not a proprietary codec.
/// </para>
///
/// <para>
/// <b>Bitmap layer.</b> The allocation bitmap is itself sector-based, stored
/// as <b>chained blocks</b>.
/// </para>
///
/// <para>
/// <b>Index file (<c>.pfi</c>) signature.</b> The post-HDM-11 index file has
/// its own magic, distinct from <c>"PImg"</c>; the signature bytes are not
/// yet known.
/// </para>
///
/// <para>
/// <b>What stays unresolved.</b> Known: the header struct (offsets 0 / 4 / 6 /
/// 0x0C / 0x26 / 0x27 / 0x30 / 0x34 / 0xD8 / 0xE8 / 0xF1), the chunk /
/// segment / bitmap-chain architecture, and the per-chunk Adler-32 + zlib
/// frame model. Not known to a level safe for reconstruction: the exact
/// on-disk offset of the chunk-table inside the segment, the exact
/// bitmap-chain encoding, the <c>.pfi</c> magic bytes, and the on-disk
/// relation between the <c>[+0x30]</c> image-type tag and the
/// <c>VIRTUAL_DRIVE_VENDOR_PBF</c> / <c>VIRTUAL_DRIVE_ATTRIB_SPLIT</c>
/// attribute enums. Without real PBF sample files (HDM 16+ is restore-only;
/// the Free Edition only writes pVHD), the structure cannot be
/// byte-validated, and a speculative R/O sector-extraction parser would
/// produce garbage on real archives. The format therefore stays at R/O
/// metadata; the known structure is persisted in <c>metadata.ini</c> so the
/// next promotion pass can extend the parser against a real sample.
/// </para>
/// </summary>
public sealed class ParagonReader : IDisposable {

  /// <summary>
  /// Paragon Backup Format image magic: 4 ASCII bytes <c>"PImg"</c>
  /// (hex <c>50 49 6D 67</c>) at offset 0. Documented in TrID.
  /// </summary>
  public static readonly byte[] PImgTag = "PImg"u8.ToArray();

  /// <summary>
  /// Minimum header size we require past the magic to safely parse the
  /// <c>+4 major</c> / <c>+6 format-version</c> field pair. The full vendor
  /// header is at least <c>0xF2</c> (242) bytes, but reading
  /// the structured fields past <c>+8</c> is gated by an explicit length
  /// check so we degrade safely on truncated samples.
  /// </summary>
  private const int MinHeaderSize = 8;

  private readonly byte[] _data;
  private readonly List<ParagonEntry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<ParagonEntry> Entries => _entries;

  /// <summary>Detected magic variant; always <c>"PImg"</c> when <see cref="ValidHeader"/> is true.</summary>
  public string Variant { get; private set; } = "";

  /// <summary>
  /// The 4 bytes immediately following the <c>"PImg"</c> magic, captured as a
  /// little-endian unsigned 32-bit word for diagnostic surfacing. This
  /// 32-bit word decomposes as <c>(uint16 Major)</c> at <c>+4</c> and
  /// <c>(uint16 FormatVersion)</c> at <c>+6</c>; HDM 18 archives carry
  /// <c>0x00030002</c> (= Major <c>0x0002</c>, FormatVersion <c>0x0003</c>).
  /// Older archives carry FormatVersion <c>0x0001</c> or <c>0x0002</c>;
  /// values &gt; <c>3</c> are incompatible.
  /// </summary>
  public uint TrailingWord { get; private set; }

  /// <summary>
  /// Major number at on-disk offset <c>+4</c> (<c>0x0002</c> in current
  /// archives); considered alongside <see cref="FormatVersion"/> when deciding
  /// whether the <c>[+0xD8] ParentId</c> field is valid.
  /// </summary>
  public ushort Major { get; private set; }

  /// <summary>
  /// Format-version word at on-disk offset <c>+6</c> (<c>0x0003</c> in
  /// current archives; values &gt; <c>3</c> are incompatible). Values
  /// <c>&gt;= 2</c> also make the <c>+0xD8</c> ParentId chain field valid.
  /// </summary>
  public ushort FormatVersion { get; private set; }

  /// <summary>
  /// Gets a value indicating whether valid header.
  /// </summary>
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
  /// the Adler-32 of the decompressed bytes — i.e. the vendor's per-chunk
  /// field set (number, 64-bit offset, 32-bit size, compressed flag).
  /// </summary>
  public IReadOnlyList<ParagonChunkInfo> ChunkTable => this._chunkTable;
  private readonly List<ParagonChunkInfo> _chunkTable = [];

  /// <summary>
  /// Initializes a new instance of <see cref="ParagonReader"/>.
  /// </summary>
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

    // Structured 16-bit major @+4, 16-bit format-version @+6 (0x00030002 in
    // current archives; format versions above 3 are incompatible).
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

      // Latest-entry-wins per ChunkNumber: the in-place modifier appends
      // fresh entries on Replace/Remove keyed by the original ChunkNumber.
      // The reader walks the table in order, so the LAST entry per
      // ChunkNumber is the live state. Tombstones (IsTombstone=true) wipe
      // the chunk from the live view entirely.
      var latestByChunkNumber = new Dictionary<uint, ParagonChunkInfo>();
      var ordering = new List<uint>();
      foreach (var info in this._chunkTable) {
        if (!latestByChunkNumber.ContainsKey(info.ChunkNumber))
          ordering.Add(info.ChunkNumber);
        latestByChunkNumber[info.ChunkNumber] = info;
      }
      foreach (var chunkNumber in ordering) {
        var info = latestByChunkNumber[chunkNumber];
        if (info.IsTombstone) continue;
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
      var flagByte = entry[16];
      var isTombstone = flagByte == ParagonWriter.TombstoneFlag;
      var info = new ParagonChunkInfo {
        ChunkNumber = BinaryPrimitives.ReadUInt32LittleEndian(entry[..4]),
        ChunkOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(4, 8)),
        ChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4)),
        IsCompressed = flagByte == (byte)'Y',
        IsTombstone = isTombstone,
        LogicalSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(20, 4)),
        Adler32 = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(24, 4)),
      };
      // Per-entry bounds — refuse the whole walk if any entry overflows.
      // Tombstones legitimately carry ChunkSize=0 + ChunkOffset=0 so they
      // always pass this check.
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
      using var z = new ZlibStream(input, CompressionStreamMode.Decompress);
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
        + $"table entry says 0x{info.Adler32:X8}.");
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
    // Latest-wins per ChunkNumber + skip tombstones — matches the live
    // entry view materialised by Parse() so in-place-modified images
    // round-trip the post-mutation payload.
    var latestByChunkNumber = new Dictionary<uint, ParagonChunkInfo>();
    var ordering = new List<uint>();
    foreach (var info in this._chunkTable) {
      if (!latestByChunkNumber.ContainsKey(info.ChunkNumber))
        ordering.Add(info.ChunkNumber);
      latestByChunkNumber[info.ChunkNumber] = info;
    }
    using var ms = new MemoryStream(capacity: (int)Math.Min(int.MaxValue, this.TotalLogicalSize));
    foreach (var chunkNumber in ordering) {
      var info = latestByChunkNumber[chunkNumber];
      if (info.IsTombstone) continue;
      ms.Write(this.ExtractChunkBytes(info));
    }
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
      bldr.Append("cwbp_rw_scope=Add / Replace / Remove via true in-place chunk-table append (ParagonInPlaceModifier). Existing chunk bodies in [0, oldChunkTableOffset) stay byte-identical. Replace appends fresh entry with same ChunkNumber (latest-wins). Remove appends tombstone (IsCompressed=0xFF + ChunkSize=0); original bytes survive, reader hides the entry.\n");
      bldr.Append("cwbp_tombstone_flag=0xFF\n");
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
    bldr.Append("magic_source=TrID file-identifier database\n");
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
    bldr.Append("history_hdm18=Free 18.12.0.0744 (May 2026) still restores legacy PBF\n");

    // What we still can't do (structural R/W blockers).
    bldr.Append("ro_promotion=metadata-only+structured-header\n");
    bldr.Append("rw_promotion=blocked\n");
    bldr.Append("rw_blocker_1=block index layout after the structured header is known at the architectural level (segments of chunks, Adler-32 + zlib per chunk) but the exact on-disk offset of the chunk-table inside each segment requires a real PBF sample to byte-validate; speculative parsing would produce garbage\n");
    bldr.Append("rw_blocker_2=per-cluster allocation bitmap is stored as chained blocks but the chain-link encoding requires a real sample to byte-validate\n");
    bldr.Append("rw_blocker_3=snapshot / incremental chain framing uses a +0xD8 ParentId u64 back-pointer gated by FormatVersion >= 2, but the parent-resolution policy (multi-file search order) requires a real chain to validate\n");
    bldr.Append("rw_blocker_4=per-segment split-archive trailer (.000/.001/...) layout is known at the architectural level but the exact trailer / continuation header requires a real split sample\n");
    bldr.Append("rw_blocker_5=Free Edition of HDM 18 (the only currently-available distribution) writes pVHD by default and PBF only via Legacy Mode in the paid edition; no test PBF samples available for byte validation\n");
    bldr.Append("rw_blocker_6=format is obsolete for creation since HDM 16; vendor tools are restore-only and the PFI index magic is not known\n");

    // Structured header layout.
    bldr.Append("struct_header_offset_0=Magic 'PImg' (50 49 6D 67) - dword constant\n");
    bldr.Append("struct_header_offset_4=Major uint16 - 0x0002 in current archives\n");
    bldr.Append("struct_header_offset_6=FormatVersion uint16 - 0x0003 in current archives; values > 3 are incompatible\n");
    bldr.Append("struct_header_offset_c=uint32 'F12' (volume-section length / size discriminator) - equal across parent / child in a chain\n");
    bldr.Append("struct_header_offset_26=byte FlagsA - bit 7 set when a parent / delta-base is attached; bits 0 / 1 carry image-state flags\n");
    bldr.Append("struct_header_offset_27=byte FlagsB - bit 0 set; bits 5 and 6 clear\n");
    bldr.Append("struct_header_offset_30=uint32 'F30' image-type / fork ID - equal across parent / child (incremental-chain identity)\n");
    bldr.Append("struct_header_offset_34=string / buffer equal across parent / child; likely volume name or GUID prefix\n");
    bldr.Append("struct_header_offset_d8=uint64 ParentId - only valid when FormatVersion >= 2; incremental-chain back-pointer\n");
    bldr.Append("struct_header_offset_e8=byte FlagsC - bit 0 meaningful (likely 'has-encryption' or 'has-incremental-bitmap')\n");
    bldr.Append("struct_header_offset_f1=byte derived from the image type; last byte of the initial header setup\n");
    bldr.Append("struct_header_min_size=0xF2 (242) bytes minimum; the full header is larger and laid out by subsequent record-typed sections\n");

    // Chunk / segment / bitmap layer.
    bldr.Append("data_layer_arch=Segments (one per split file .000/.001/...) of Chunks (sector groups).\n");
    bldr.Append("data_layer_chunk=Each chunk holds N sectors (a sectors-per-chunk header field); per-chunk fields are number / 64-bit-offset / 32-bit-size / compress-flag.\n");
    bldr.Append("data_layer_compressor=zlib / DEFLATE per chunk, integrity-checked by Adler-32 (the zlib checksum)\n");
    bldr.Append("data_layer_bitmap=Chained allocation-bitmap blocks; sector-presence map for the source partition\n");
    bldr.Append("data_layer_index_pfi=Post-HDM-11 PFI index file has its own magic distinct from 'PImg'; magic bytes not known\n");

    // Diagnostic facts (manuals + KB + forum).
    bldr.Append("fact_compression_levels=0-9 dial: none / fast / normal / best (Paragon Scripting Language manual). On-disk per-chunk compressor is zlib / DEFLATE.\n");
    bldr.Append("fact_default_split=4 GiB (Backup & Recovery 17 + HDM 16 manuals; legacy FAT32 4-GiB workaround). Each split file is one segment of chunks.\n");
    bldr.Append("fact_encryption_pvhd_only=password protection / compression / splitting are pVHD-only; legacy PBF data blocks are unencrypted (B&R 17 + HDM 16 manuals).\n");
    bldr.Append("fact_conceptual_triple=backup = {index (.pfi since HDM 11, .pbf before), metadata sidecar (.pfm), compressed data files (.pbf + .000/.001/... splits)} (KB 767 + paragon284 forum)\n");
    bldr.Append("fact_chain_model=Differential = base + 1 delta; Incremental = base + N chained deltas (KB 262). On-disk back-pointer is the +0xD8 uint64 ParentId.\n");
    bldr.Append("fact_exfat_advisory=Paragon support note: PBF writers issue many sub-flush writes that collide with the Microsoft exFAT cache-flush bug on Win10+; segment files grow by appending chunks.\n");

    // Audit trail: public research vectors pursued past TrID magic.
    bldr.Append("research_audit_1=asmodean 'expimg' (asmodean.reverse.net/pages/expimg.html) - FALSE LEAD, refers to a Japanese visual-novel archive 'PImg' unrelated to Paragon\n");
    bldr.Append("research_audit_2=Paragon HDM SDK (developers.paragon-software.com/hdm-sdk) - partitioning operations only; hdmengine/hdmclient/biontdrv headers are partition-management, not archive-format\n");
    bldr.Append("research_audit_3=Paragon-Software-Group GitHub - linux-ntfs3, paragon_apfs_sdk_ce, paragon_portable_stl, paragon_firewall_ce, eucalyptus, paragon-lowcode-oss; no backup-format code\n");
    bldr.Append("research_audit_4=Paragon-Backup-Recovery GitHub - profile-only org, no backup-format code\n");
    bldr.Append("research_audit_5=USPTO patent database - no Paragon-Software-Group-assigned patent disclosing PBF on-disk layout; disk-image patents in this space belong to Veritas / Symantec / Acronis\n");
    bldr.Append("research_audit_6=EnCase / X-Ways / FTK forensic-suite custom-type repositories - generic carving only, no Paragon-PBF-specific carver or content-walk recipe\n");
    bldr.Append("research_audit_7=Habr Q&A / Toster.ru threads - community confirms 'PBF is closed, Paragon utilities are the only way'; no chunk-framing detail\n");
    bldr.Append("research_audit_8=paragon284.rssing.com Drive Backup product-line forum - user describes 'Paragon file directory structure: index file, metadata and compressed backup files' confirming the conceptual triple but no byte-level layout\n");
    bldr.Append("research_audit_9=Gary Kessler / SEARCH file-signatures database (garykessler.net) - audited, no PBF entry\n");
    bldr.Append("research_audit_10=Kaitai Struct format library + 010 Editor / Hexinator / Synalize It! / ImHex templates - no .ksy or .bt template for PBF\n");
    bldr.Append("research_audit_11=Paragon Scripting Language User Manual (download.paragon-software.com/doc/script_man_.pdf) - references *.pbf only as an exclusion extension; confirms 0-9 compression dial; no struct layout\n");
    bldr.Append("research_audit_12=Paragon ExtFS / NTFS3 / UFSD / APFS-SDK-CE open-source releases - filesystem drivers, not backup-archive drivers; share no data structures with PBF\n");
    bldr.Append("research_conclusion=Twelve public-source vectors exhausted (research_audit_1..12) at the bare 'PImg' tag. The structured header (16-bit major + 16-bit format version, +0xC / +0x30 / +0xD8 chain identity fields), the segments-of-chunks data layer, zlib + Adler-32 per chunk and the chained-bitmap allocation map are known; without real PBF samples for byte validation the format stays at R/O metadata.\n");

    bldr.Append("note=R/O metadata + structured header parse. The 'PImg' magic, +4 Major, +6 FormatVersion fields are parsed real. The chunk / segment / bitmap data layer is structurally documented but not byte-validated. Restore content with vendor tools (Paragon Backup & Recovery, Hard Disk Manager).\n");
    bldr.Append("references=TrID 'Paragon Backup Format image' (50 49 6D 67),kb.paragon-software.com/article/767 (Archive Formats),kb.paragon-software.com/article/262 (Backup Types),Paragon Backup & Recovery 17 User Manual,Paragon Hard Disk Manager 16 User Manual,Paragon Scripting Language User Manual,developers.paragon-software.com/hdm-sdk,github.com/Paragon-Software-Group,github.com/Paragon-Backup-Recovery,garykessler.net file-signatures table,paragon284.rssing.com Drive Backup forum mirror\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(ParagonEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}

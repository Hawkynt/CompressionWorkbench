#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;

namespace FileFormat.Paragon;

/// <summary>
/// Round-trippable writer for the Paragon Backup Format image
/// (<c>.pbf</c>) container.
///
/// <para>
/// <b>Scope: WORM (write-once round-trippable) self-interop.</b> The output
/// file is byte-identical when read back by <see cref="ParagonReader"/>
/// running in CWBP mode. <b>Byte-compat with the vendor's reader (Paragon
/// Backup &amp; Recovery / Hard Disk Manager) is explicitly out of scope</b>
/// — vendor on-disk semantics past offset 8 (the <c>+0xC F12</c>
/// discriminator, <c>+0x30</c> image-type / fork ID, <c>+0x34</c> volume
/// name buffer, the segment-internal chunk-table offset, the bitmap chain
/// encoding, the per-chunk Adler-32 verification site) are reverse-
/// engineered at the architectural level but have never been byte-validated
/// against a real Paragon-produced sample (HDM 16+ is restore-only; the
/// Free Edition only writes pVHD). Vendor offsets <c>+0xC</c>, <c>+0x30</c>,
/// <c>+0x34</c>, <c>+0xD8</c>, <c>+0xE8</c>, <c>+0xF1</c> are left zero by
/// this writer rather than forged.
/// </para>
///
/// <para>
/// <b>On-disk layout this writer produces.</b>
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Bytes <c>+0x00..+0x07</c> — vendor-real header prefix.</b>
///     <c>"PImg"</c> magic at <c>+0x00</c>, <c>Major = 0x0002</c> at
///     <c>+0x04</c>, <c>FormatVersion = 0x0003</c> at <c>+0x06</c>. These
///     are the same literal values the vendor writer emits at HDM-18
///     RVA <c>0x4a8dc4</c> (<c>MOV DWORD [rax+4], 0x00030002</c>) so a
///     vendor reader at least passes the magic + version gate at
///     RVA <c>0x4ae6e4</c>; everything past <c>+0x07</c> is our own layout.
///   </description></item>
///   <item><description>
///     <b>Bytes <c>+0x08..+0xF7</c> — zero-fill.</b> The reverse-engineered
///     vendor fields at <c>+0x0C</c> (F12 discriminator), <c>+0x26</c>
///     (FlagsA), <c>+0x27</c> (FlagsB), <c>+0x30</c> (image-type / fork
///     ID), <c>+0x34</c> (volume name / GUID), <c>+0xD8</c> (ParentId u64),
///     <c>+0xE8</c> (FlagsC), <c>+0xF1</c> (derived byte) are deliberately
///     left zero. Forging them without a real sample to validate against
///     would produce an image the vendor either rejects or misreads
///     silently. Leaving them zero is honest about the scope.
///   </description></item>
///   <item><description>
///     <b>Bytes <c>+0xF8..+0x100</c> — CWBP discriminator (8 bytes).</b>
///     ASCII <c>"CWBPbpf1"</c> at <c>+0xF8</c>. This is past the vendor's
///     last initialised offset (<c>+0xF1</c>) and tells our own reader
///     "this file was produced by our writer; the trailing chunk-table
///     index is at the offset stored below". A vendor-produced file will
///     not have this marker, in which case our reader falls back to the
///     R/O metadata pass.
///   </description></item>
///   <item><description>
///     <b>Bytes <c>+0x100..+0x140</c> — CWBP table-of-contents (64 bytes).</b>
///     The fields are all little-endian. <c>ChunkCount u32</c> at
///     <c>+0x100</c>, <c>ChunkTableOffset u64</c> at <c>+0x104</c>,
///     <c>SectorsPerChunk u32</c> at <c>+0x10C</c> (default 256, so
///     128 KiB per chunk at 512 B sectors — matches the
///     "Sectors per chunk" debug field), <c>SegmentCount u32</c> at
///     <c>+0x110</c> (we emit a single segment containing all chunks),
///     <c>TotalLogicalSize u64</c> at <c>+0x114</c>,
///     <c>HeaderSize u32</c> at <c>+0x11C</c> (the offset of the first
///     chunk body), and zero-padding to <c>+0x140</c>. After
///     <c>+0x140</c> the chunks start.
///   </description></item>
///   <item><description>
///     <b>Chunk body region — <c>+0x140..ChunkTableOffset</c>.</b> Each
///     chunk's body is written at the offset recorded in its chunk-table
///     entry. When the chunk is compressed (<c>ChunkIsCompress = 'Y'</c>)
///     the body is a raw zlib stream
///     (<see cref="System.IO.Compression.ZLibStream"/>); when not
///     compressed it is the raw bytes verbatim. Per-chunk integrity is
///     verified by Adler-32 (see <see cref="ParagonAdler32"/>) stored in the table entry — the
///     vendor uses the same checksum ("Chunk is not valid, adler32
///     checksum is wrong.") because the per-chunk codec is zlib/DEFLATE.
///   </description></item>
///   <item><description>
///     <b>Chunk-offset table — at <c>ChunkTableOffset</c>.</b> An array of
///     <c>ChunkCount</c> entries, 40 bytes per entry (little-endian):
///     <c>ChunkNumber u32</c>, <c>ChunkOffSet u64</c>, <c>ChunkSize u32</c>
///     (the on-disk byte size of the chunk body — raw bytes or zlib
///     stream bytes), <c>ChunkIsCompress u8</c> (<c>'Y'</c> /
///     <c>'N'</c>), <c>3 bytes padding</c>, <c>LogicalSize u32</c> (the
///     decompressed byte size), <c>Adler32 u32</c> (zlib Adler-32 of the
///     decompressed bytes), <c>Reserved u64</c>. This is exactly the
///     architectural per-chunk struct the vendor's
///     <c>"ChunkNumber: %d, ChunkOffSet: 0x%016I64x, ChunkSize: %d,
///     ChunkIsCompress: %c"</c> debug-string round-trip emits.
///   </description></item>
/// </list>
///
/// <para>
/// <b>What is intentionally NOT emitted.</b> No allocation bitmap
/// (<c>CPbfBitmapIO</c> chained blocks — encoding undocumented), no
/// incremental-chain parent back-pointer (<c>+0xD8 ParentId u64</c> — we
/// always write a base image), no per-segment split-archive trailer
/// (<c>.000</c> / <c>.001</c> / ... — we always write a single segment),
/// no PFI sidecar (<c>.pfi</c> magic loaded indirectly in the vendor
/// binary, could not be statically resolved). These stay open for the
/// next promotion pass once a real Paragon-produced sample is available
/// to byte-validate against.
/// </para>
/// </summary>
public sealed class ParagonWriter : IDisposable {

  /// <summary>"PImg" tag at <c>+0x00</c> — vendor-documented magic.</summary>
  public static readonly byte[] PImgTag = "PImg"u8.ToArray();

  /// <summary>CWBP discriminator at <c>+0xF8</c> — past the vendor's last
  /// initialised offset (<c>+0xF1</c>). 8 bytes <c>"CWBPbpf1"</c>. Used by
  /// our reader to detect a file produced by our writer vs. a vendor one.</summary>
  public static readonly byte[] CwbpDiscriminator = "CWBPbpf1"u8.ToArray();

  /// <summary>Vendor-literal Major value at <c>+0x04</c>: <c>0x0002</c>.</summary>
  public const ushort Major = 0x0002;

  /// <summary>Vendor-literal FormatVersion value at <c>+0x06</c>: <c>0x0003</c>.</summary>
  public const ushort FormatVersion = 0x0003;

  /// <summary>Header size up to and including the CWBP table-of-contents.</summary>
  public const int HeaderSize = 0x140;

  /// <summary>On-disk size of a single chunk-table entry.</summary>
  public const int ChunkEntrySize = 40;

  /// <summary>
  /// Sentinel value the in-place modifier writes into the chunk-table
  /// entry's <c>IsCompressed</c> byte (offset +16 within the entry) to
  /// flag the entry as a Remove tombstone. Combined with
  /// <c>ChunkSize = 0</c> the tombstone suppresses the chunk identified
  /// by <c>ChunkNumber</c> from the live view. Picked as a non-ASCII
  /// value so it can never collide with the vendor-style <c>'Y'</c> /
  /// <c>'N'</c> compress flag.
  /// </summary>
  public const byte TombstoneFlag = 0xFF;

  /// <summary>Default sectors per chunk — 256 sectors × 512 B = 128 KiB per chunk.</summary>
  public const int DefaultSectorsPerChunk = 256;

  /// <summary>Default sector size in bytes.</summary>
  public const int SectorSize = 512;

  /// <summary>Default chunk size in bytes (128 KiB).</summary>
  public const int DefaultChunkSize = DefaultSectorsPerChunk * SectorSize;

  /// <summary>CWBP table-of-contents field offsets — exposed so the
  /// in-place modifier and round-trip tests can patch / verify the same
  /// canonical layout.</summary>
  public const int OffsetCwbpDiscriminator = 0xF8;
  /// <summary>CWBP TOC offset of the <c>ChunkCount u32</c> field.</summary>
  public const int OffsetChunkCount = 0x100;
  /// <summary>CWBP TOC offset of the <c>ChunkTableOffset u64</c> field.</summary>
  public const int OffsetChunkTableOffset = 0x104;
  /// <summary>CWBP TOC offset of the <c>SectorsPerChunk u32</c> field.</summary>
  public const int OffsetSectorsPerChunk = 0x10C;
  /// <summary>CWBP TOC offset of the <c>SegmentCount u32</c> field.</summary>
  public const int OffsetSegmentCount = 0x110;
  /// <summary>CWBP TOC offset of the <c>TotalLogicalSize u64</c> field.</summary>
  public const int OffsetTotalLogicalSize = 0x114;
  /// <summary>CWBP TOC offset of the <c>HeaderSize u32</c> field.</summary>
  public const int OffsetHeaderSize = 0x11C;

  private readonly Stream _output;
  private readonly bool _leaveOpen;
  private readonly bool _compressChunks;
  private readonly int _chunkSize;
  private readonly List<ChunkEntry> _chunkTable = [];
  private long _bodyEndOffset;
  private bool _headerWritten;
  private bool _finalised;
  private bool _disposed;

  /// <summary>
  /// Creates a writer over <paramref name="output"/>. The stream is left
  /// open when <paramref name="leaveOpen"/> is true.
  /// </summary>
  /// <param name="output">Target stream — must be writable and seekable.</param>
  /// <param name="compressChunks">Whether each chunk is zlib-compressed
  /// (<c>true</c>) or stored verbatim (<c>false</c>).</param>
  /// <param name="chunkSize">Bytes per chunk. The vendor's debug strings
  /// frame chunks as "N sectors per chunk"; <see cref="DefaultChunkSize"/>
  /// (128 KiB) matches the typical sector-times-256 value. The writer
  /// splits the payload at this granularity, except for the last chunk
  /// which may be shorter.</param>
  /// <param name="leaveOpen">When true, the underlying stream stays open
  /// after this writer is disposed.</param>
  public ParagonWriter(Stream output, bool compressChunks = true, int chunkSize = DefaultChunkSize, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite)
      throw new ArgumentException("Paragon writer requires a writable stream.", nameof(output));
    if (!output.CanSeek)
      throw new ArgumentException("Paragon writer requires a seekable stream (chunk-table offset is patched at finalise).", nameof(output));
    if (chunkSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be positive.");
    this._output = output;
    this._leaveOpen = leaveOpen;
    this._compressChunks = compressChunks;
    this._chunkSize = chunkSize;
    this._bodyEndOffset = HeaderSize;
  }

  /// <summary>
  /// Writes a payload as a sequence of chunks. Splits at the configured
  /// chunk size; each chunk is either stored verbatim or zlib-compressed
  /// per the constructor's <c>compressChunks</c> argument.
  /// </summary>
  /// <param name="payload">The decompressed bytes to write.</param>
  public void WritePayload(ReadOnlySpan<byte> payload) {
    this.EnsureHeader();
    if (this._finalised)
      throw new InvalidOperationException("Paragon writer already finalised.");

    var offset = 0;
    while (offset < payload.Length) {
      var size = Math.Min(this._chunkSize, payload.Length - offset);
      this.WriteChunk(payload.Slice(offset, size));
      offset += size;
    }
  }

  /// <summary>
  /// Writes a single chunk verbatim — no splitting. Use when the caller
  /// already chose the per-chunk granularity (e.g. partition-image
  /// emitters that want one chunk per partition).
  /// </summary>
  /// <param name="chunkData">The decompressed bytes for this chunk.</param>
  public void WriteChunk(ReadOnlySpan<byte> chunkData) {
    this.EnsureHeader();
    if (this._finalised)
      throw new InvalidOperationException("Paragon writer already finalised.");

    var logicalSize = chunkData.Length;
    var adler = ParagonAdler32.Compute(chunkData);
    byte[] bodyBytes;
    bool isCompressed;

    if (this._compressChunks && logicalSize > 0) {
      using var ms = new MemoryStream();
      using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        z.Write(chunkData);
      bodyBytes = ms.ToArray();
      // If compressed payload is larger than the source, fall back to stored.
      if (bodyBytes.Length >= logicalSize) {
        bodyBytes = chunkData.ToArray();
        isCompressed = false;
      } else {
        isCompressed = true;
      }
    } else {
      bodyBytes = chunkData.ToArray();
      isCompressed = false;
    }

    this._output.Seek(this._bodyEndOffset, SeekOrigin.Begin);
    this._output.Write(bodyBytes);

    var entry = new ChunkEntry {
      ChunkNumber = (uint)this._chunkTable.Count,
      ChunkOffset = (ulong)this._bodyEndOffset,
      ChunkSize = (uint)bodyBytes.Length,
      IsCompressed = isCompressed,
      LogicalSize = (uint)logicalSize,
      Adler32 = adler,
    };
    this._chunkTable.Add(entry);
    this._bodyEndOffset += bodyBytes.Length;
  }

  /// <summary>
  /// Finalises the file: writes the chunk-offset table at the current
  /// body end, patches the header's <c>ChunkCount</c> /
  /// <c>ChunkTableOffset</c> / <c>TotalLogicalSize</c> fields, and flushes.
  /// Always call this before disposing the writer.
  /// </summary>
  public void Finalise() {
    this.EnsureHeader();
    if (this._finalised) return;
    this._finalised = true;

    var chunkTableOffset = this._bodyEndOffset;
    this._output.Seek(chunkTableOffset, SeekOrigin.Begin);

    Span<byte> entryBuf = stackalloc byte[ChunkEntrySize];
    foreach (var e in this._chunkTable) {
      entryBuf.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[..4], e.ChunkNumber);
      BinaryPrimitives.WriteUInt64LittleEndian(entryBuf.Slice(4, 8), e.ChunkOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(12, 4), e.ChunkSize);
      entryBuf[16] = e.IsCompressed ? (byte)'Y' : (byte)'N';
      // bytes 17..19 = padding (cleared above)
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(20, 4), e.LogicalSize);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(24, 4), e.Adler32);
      // bytes 28..39 = reserved (cleared above)
      this._output.Write(entryBuf);
    }

    var totalLogicalSize = 0UL;
    foreach (var e in this._chunkTable)
      totalLogicalSize += e.LogicalSize;

    // Patch header fields.
    Span<byte> patch = stackalloc byte[8];

    this._output.Seek(OffsetChunkCount, SeekOrigin.Begin);
    BinaryPrimitives.WriteUInt32LittleEndian(patch[..4], (uint)this._chunkTable.Count);
    this._output.Write(patch[..4]);

    this._output.Seek(OffsetChunkTableOffset, SeekOrigin.Begin);
    BinaryPrimitives.WriteUInt64LittleEndian(patch, (ulong)chunkTableOffset);
    this._output.Write(patch);

    this._output.Seek(OffsetTotalLogicalSize, SeekOrigin.Begin);
    BinaryPrimitives.WriteUInt64LittleEndian(patch, totalLogicalSize);
    this._output.Write(patch);

    this._output.Flush();
  }

  private void EnsureHeader() {
    if (this._headerWritten) return;
    this._headerWritten = true;

    Span<byte> hdr = stackalloc byte[HeaderSize];
    hdr.Clear();

    // +0x00 magic.
    PImgTag.AsSpan().CopyTo(hdr[..4]);

    // +0x04 Major.
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(4, 2), Major);

    // +0x06 FormatVersion.
    BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(6, 2), FormatVersion);

    // +0x08..+0xF7 left zero — the reverse-engineered vendor fields at
    // +0xC, +0x26, +0x27, +0x30, +0x34, +0xD8, +0xE8, +0xF1 are not
    // forged. See class XML doc.

    // +0xF8..+0x100 CWBP discriminator (8 bytes).
    CwbpDiscriminator.AsSpan().CopyTo(hdr.Slice(OffsetCwbpDiscriminator, 8));

    // +0x100..+0x108 ChunkCount placeholder (patched in Finalise).
    // +0x108..+0x10C ChunkTableOffset placeholder (patched in Finalise).

    // +0x10C SectorsPerChunk.
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(OffsetSectorsPerChunk, 4), (uint)(this._chunkSize / SectorSize));

    // +0x110 SegmentCount = 1 (we always emit a single segment).
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(OffsetSegmentCount, 4), 1);

    // +0x114..+0x11C TotalLogicalSize placeholder (patched in Finalise).

    // +0x11C HeaderSize.
    BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(OffsetHeaderSize, 4), HeaderSize);

    this._output.Seek(0, SeekOrigin.Begin);
    this._output.Write(hdr);
    this._bodyEndOffset = HeaderSize;
  }

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finalised && this._headerWritten) this.Finalise();
    if (!this._leaveOpen) this._output.Dispose();
  }

  /// <summary>In-memory chunk-table entry kept until <see cref="Finalise"/>.</summary>
  private struct ChunkEntry {
    public uint ChunkNumber;
    public ulong ChunkOffset;
    public uint ChunkSize;
    public bool IsCompressed;
    public uint LogicalSize;
    public uint Adler32;
  }
}

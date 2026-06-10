#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using Compression.Registry;

namespace FileFormat.Paragon;

/// <summary>
/// True in-place modifier for CWBP-discriminated Paragon PBF images
/// emitted by <see cref="ParagonWriter"/>. Performs Add / Replace / Remove
/// by appending fresh chunk bodies at the OLD chunk-table position and
/// re-laying the chunk-offset table at the new tail. Existing chunk-body
/// bytes in <c>[0, oldChunkTableOffset)</c> stay byte-identical at their
/// original offsets after every mutation — the only header fields the
/// modifier patches are <c>ChunkCount</c> at <c>+0x100</c>,
/// <c>ChunkTableOffset</c> at <c>+0x104</c>, and <c>TotalLogicalSize</c>
/// at <c>+0x114</c>.
/// </summary>
/// <remarks>
/// <para><b>CWBP append semantic.</b> The on-disk layout the writer
/// produces is <c>[ Header ][ Chunk bodies ][ Chunk-offset table ]</c>.
/// The chunk-offset table is the last region of the file, so any
/// append-style mutation can simply (a) overwrite the OLD chunk-offset
/// table with the new chunk body / bodies, (b) re-lay the table
/// (existing entries byte-identical + new entries) at the new tail, and
/// (c) patch the TOC's <c>ChunkCount</c> + <c>ChunkTableOffset</c> +
/// <c>TotalLogicalSize</c> fields. Every byte in
/// <c>[0, oldChunkTableOffset)</c> stays byte-identical, which means
/// every existing chunk body's per-chunk Adler-32 stays valid.</para>
///
/// <para><b>Add</b> (<see cref="AddChunks"/>): each new input becomes a
/// fresh chunk with a brand-new <see cref="ParagonChunkInfo.ChunkNumber"/>
/// (max-seen + 1). Bodies are written at the OLD chunk-table offset and
/// the table re-laid at the new tail with the existing entries first,
/// the new entries appended.</para>
///
/// <para><b>Replace</b> (<see cref="ReplaceChunk"/>): a fresh chunk body
/// is written at the OLD chunk-table offset and a fresh chunk-table
/// entry is appended carrying the SAME <see cref="ParagonChunkInfo.ChunkNumber"/>
/// as the target. The reader walks the table in order and the LAST entry
/// per chunk number wins, so the new body becomes the live entry while
/// the old body's bytes (still pointed to by the older entry) stay
/// byte-identical at their original offset. Replace is a strict superset
/// of Add semantically — the old bytes survive at their offset and only
/// the table tail grows.</para>
///
/// <para><b>Remove</b> (<see cref="RemoveChunk"/>): a tombstone entry is
/// appended sharing the target's <see cref="ParagonChunkInfo.ChunkNumber"/>.
/// Tombstones encode <c>IsCompressed = 0xFF</c> + <c>ChunkSize = 0</c>
/// on the wire — see <see cref="ParagonWriter.TombstoneFlag"/>. The
/// reader suppresses chunks whose latest entry is a tombstone, but the
/// original body bytes stay byte-identical at their offset (the operation
/// is byte-preserving on payload, not forensic-wipe).</para>
///
/// <para><b>By design.</b> The modifier only operates on
/// CWBP-discriminated images (the writer's own output). Vendor-style
/// PBFs (no marker at <c>+0xF8</c>) cause an
/// <see cref="InvalidOperationException"/> — the vendor's
/// chunk-table-inside-segment offset is undocumented past the
/// architectural level, so an in-place modify on a real vendor sample
/// would either corrupt the image or produce something the vendor reader
/// rejects silently. See <see cref="ParagonFormatDescriptor"/> for the
/// honest-scope note.</para>
/// </remarks>
public static class ParagonInPlaceModifier {

  /// <summary>
  /// Appends a chunk per input. Each input becomes one new chunk with a
  /// brand-new <see cref="ParagonChunkInfo.ChunkNumber"/> (max-seen + 1).
  /// Existing chunk body bytes in <c>[0, oldChunkTableOffset)</c> stay
  /// byte-identical.
  /// </summary>
  /// <param name="image">A CWBP-discriminated PBF image. Must be
  /// readable, writable, and seekable.</param>
  /// <param name="inputs">The bodies to append. Directory entries are
  /// skipped. Non-directory entry bytes are taken via
  /// <see cref="ArchiveInputInfo.ReadContent"/>.</param>
  /// <param name="compressChunks">Whether each new chunk's body is zlib-
  /// compressed (true, default) or stored verbatim (false). Matches the
  /// writer's per-chunk policy. If zlib output is larger than the source
  /// the writer-style fallback to stored applies.</param>
  public static void AddChunks(
    Stream image,
    IReadOnlyList<ArchiveInputInfo> inputs,
    bool compressChunks = true
  ) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    RequireRwSeekable(image);

    var state = ScanState(image);
    var fileEntries = inputs.Where(i => !i.IsDirectory).ToList();
    if (fileEntries.Count == 0) return;

    var nextChunkNumber = state.NextChunkNumber;
    var newEntries = new List<WireEntry>(state.WireEntries);

    // Bodies are written at the OLD chunk-table offset; each subsequent
    // body lands at the previous body's end.
    image.Seek(state.OldChunkTableOffset, SeekOrigin.Begin);
    var bodyOffset = state.OldChunkTableOffset;

    foreach (var input in fileEntries) {
      var payload = input.ReadContent();
      var (bodyBytes, isCompressed) = EncodeBody(payload, compressChunks);
      image.Write(bodyBytes, 0, bodyBytes.Length);
      newEntries.Add(new WireEntry {
        ChunkNumber = nextChunkNumber++,
        ChunkOffset = (ulong)bodyOffset,
        ChunkSize = (uint)bodyBytes.Length,
        FlagByte = isCompressed ? (byte)'Y' : (byte)'N',
        LogicalSize = (uint)payload.Length,
        Adler32 = ParagonAdler32.Compute(payload),
      });
      bodyOffset += bodyBytes.Length;
    }

    WriteTableAndPatchToc(image, newEntries, bodyOffset);
  }

  /// <summary>
  /// Replaces the chunk identified by <paramref name="entryName"/>
  /// (e.g. <c>chunk_000003.bin</c>) with <paramref name="newPayload"/>.
  /// Appends a fresh chunk body at the OLD chunk-table offset and a
  /// fresh chunk-table entry sharing the target's
  /// <see cref="ParagonChunkInfo.ChunkNumber"/>; the reader's
  /// latest-wins-per-chunk-number semantic surfaces the new body as the
  /// live entry. The old body's bytes stay byte-identical at their
  /// original offset.
  /// </summary>
  /// <param name="image">A CWBP-discriminated PBF image. Must be
  /// readable, writable, and seekable.</param>
  /// <param name="entryName">The current live entry name — must match
  /// <c>chunk_NNNNNN.bin</c>. <see cref="FileNotFoundException"/> is
  /// thrown if no live chunk carries the parsed number.</param>
  /// <param name="newPayload">The replacement body bytes.</param>
  /// <param name="compressChunks">Whether the replacement body is zlib-
  /// compressed (true, default) or stored verbatim (false).</param>
  public static void ReplaceChunk(
    Stream image,
    string entryName,
    byte[] newPayload,
    bool compressChunks = true
  ) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryName);
    ArgumentNullException.ThrowIfNull(newPayload);
    RequireRwSeekable(image);

    var chunkNumber = ParseChunkNumber(entryName);
    var state = ScanState(image);
    if (!state.LiveChunkNumbers.Contains(chunkNumber))
      throw new FileNotFoundException(
        $"Paragon in-place Replace: no live chunk #{chunkNumber} in image (entry '{entryName}').");

    var (bodyBytes, isCompressed) = EncodeBody(newPayload, compressChunks);
    image.Seek(state.OldChunkTableOffset, SeekOrigin.Begin);
    image.Write(bodyBytes, 0, bodyBytes.Length);

    var newEntries = new List<WireEntry>(state.WireEntries) {
      new() {
        ChunkNumber = chunkNumber,
        ChunkOffset = (ulong)state.OldChunkTableOffset,
        ChunkSize = (uint)bodyBytes.Length,
        FlagByte = isCompressed ? (byte)'Y' : (byte)'N',
        LogicalSize = (uint)newPayload.Length,
        Adler32 = ParagonAdler32.Compute(newPayload),
      },
    };
    WriteTableAndPatchToc(image, newEntries, state.OldChunkTableOffset + bodyBytes.Length);
  }

  /// <summary>
  /// Appends a tombstone entry for the chunk identified by
  /// <paramref name="entryName"/>. The original body bytes stay
  /// byte-identical at their offset; the chunk disappears from the
  /// live entry view. Tombstones encode
  /// <c>IsCompressed = <see cref="ParagonWriter.TombstoneFlag"/> = 0xFF</c>
  /// + <c>ChunkSize = 0</c> + <c>ChunkOffset = 0</c> + <c>LogicalSize = 0</c>
  /// on the wire.
  /// </summary>
  public static void RemoveChunk(Stream image, string entryName) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryName);
    RequireRwSeekable(image);

    var chunkNumber = ParseChunkNumber(entryName);
    var state = ScanState(image);
    if (!state.LiveChunkNumbers.Contains(chunkNumber))
      throw new FileNotFoundException(
        $"Paragon in-place Remove: no live chunk #{chunkNumber} in image (entry '{entryName}').");

    var newEntries = new List<WireEntry>(state.WireEntries) {
      new() {
        ChunkNumber = chunkNumber,
        ChunkOffset = 0,
        ChunkSize = 0,
        FlagByte = ParagonWriter.TombstoneFlag,
        LogicalSize = 0,
        Adler32 = 0,
      },
    };
    // No body bytes for a tombstone — the new chunk-table sits exactly at
    // the OLD chunk-table offset.
    WriteTableAndPatchToc(image, newEntries, state.OldChunkTableOffset);
  }

  // ── Helpers ─────────────────────────────────────────────────────────

  private static uint ParseChunkNumber(string entryName) {
    var leaf = Path.GetFileName(entryName);
    const string prefix = "chunk_";
    const string suffix = ".bin";
    if (!leaf.StartsWith(prefix, StringComparison.Ordinal) || !leaf.EndsWith(suffix, StringComparison.Ordinal))
      throw new InvalidOperationException(
        $"Paragon in-place modify: entry name '{entryName}' is not a 'chunk_NNNNNN.bin' chunk name.");
    var digits = leaf.AsSpan(prefix.Length, leaf.Length - prefix.Length - suffix.Length);
    if (!uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var chunkNumber))
      throw new InvalidOperationException(
        $"Paragon in-place modify: entry name '{entryName}' chunk-number segment '{digits}' is not a non-negative integer.");
    return chunkNumber;
  }

  private static void RequireRwSeekable(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException(
        "Paragon in-place modify requires a read/write/seek stream.", nameof(image));
  }

  /// <summary>
  /// Snapshot of the existing image: where the OLD chunk-table starts,
  /// the wire-level chunk-table entries the modifier preserves
  /// byte-identically when re-laying the table, and which chunk numbers
  /// are live (not tombstoned) so Replace / Remove can validate their
  /// targets.
  /// </summary>
  private sealed class ImageState {
    public long OldChunkTableOffset;
    public List<WireEntry> WireEntries = [];
    public HashSet<uint> LiveChunkNumbers = [];
    public uint NextChunkNumber;
  }

  private static ImageState ScanState(Stream image) {
    // Snapshot the image bytes by handing the stream to ParagonReader and
    // re-reading the chunk-table at byte level so we keep the wire-form
    // representation (including the FlagByte we need to preserve for
    // existing entries — 'Y' / 'N' / TombstoneFlag).
    image.Seek(0, SeekOrigin.Begin);
    var snapshot = new byte[image.Length];
    var read = 0;
    while (read < snapshot.Length) {
      var n = image.Read(snapshot, read, snapshot.Length - read);
      if (n <= 0) break;
      read += n;
    }

    if (snapshot.Length < ParagonWriter.HeaderSize ||
        !snapshot.AsSpan(0, 4).SequenceEqual(ParagonWriter.PImgTag) ||
        !snapshot.AsSpan(ParagonWriter.OffsetCwbpDiscriminator, 8)
                 .SequenceEqual(ParagonWriter.CwbpDiscriminator))
      throw new InvalidOperationException(
        "Paragon in-place modify: image lacks the CWBP discriminator at +0xF8. "
        + "Vendor-style PBFs are not supported — vendor chunk-table offsets stay "
        + "out of scope; see ParagonFormatDescriptor for the honest-scope note.");

    var span = snapshot.AsSpan();
    var chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(ParagonWriter.OffsetChunkCount, 4));
    var chunkTableOffset = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(ParagonWriter.OffsetChunkTableOffset, 8));
    if (chunkTableOffset < (ulong)ParagonWriter.HeaderSize ||
        chunkTableOffset + (ulong)chunkCount * ParagonWriter.ChunkEntrySize > (ulong)snapshot.LongLength)
      throw new InvalidDataException(
        "Paragon in-place modify: chunk-table offset / count out of bounds for image size.");

    var state = new ImageState { OldChunkTableOffset = (long)chunkTableOffset };
    var latestByChunkNumber = new Dictionary<uint, bool>(); // value = isTombstone for latest
    var hasAnyEntry = false;
    uint maxChunkNumber = 0;

    for (var i = 0; i < chunkCount; i++) {
      var entryOffset = (int)(chunkTableOffset + (ulong)(i * ParagonWriter.ChunkEntrySize));
      var entry = span.Slice(entryOffset, ParagonWriter.ChunkEntrySize);
      var w = new WireEntry {
        ChunkNumber = BinaryPrimitives.ReadUInt32LittleEndian(entry[..4]),
        ChunkOffset = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(4, 8)),
        ChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4)),
        FlagByte = entry[16],
        LogicalSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(20, 4)),
        Adler32 = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(24, 4)),
      };
      state.WireEntries.Add(w);
      latestByChunkNumber[w.ChunkNumber] = w.FlagByte == ParagonWriter.TombstoneFlag;
      if (!hasAnyEntry || w.ChunkNumber > maxChunkNumber) {
        maxChunkNumber = w.ChunkNumber;
        hasAnyEntry = true;
      }
    }

    foreach (var kv in latestByChunkNumber)
      if (!kv.Value) state.LiveChunkNumbers.Add(kv.Key);

    state.NextChunkNumber = hasAnyEntry ? maxChunkNumber + 1 : 0;
    return state;
  }

  /// <summary>
  /// Encodes a chunk body per the writer's policy: zlib-compress when
  /// requested, fall back to stored if compression doesn't pay off.
  /// Empty payloads always go through the stored path.
  /// </summary>
  private static (byte[] BodyBytes, bool IsCompressed) EncodeBody(byte[] payload, bool compressChunks) {
    if (!compressChunks || payload.Length == 0)
      return (payload, false);
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
      z.Write(payload, 0, payload.Length);
    var compressed = ms.ToArray();
    return compressed.Length >= payload.Length ? (payload, false) : (compressed, true);
  }

  private static void WriteTableAndPatchToc(Stream image, List<WireEntry> entries, long newChunkTableOffset) {
    image.Seek(newChunkTableOffset, SeekOrigin.Begin);

    Span<byte> entryBuf = stackalloc byte[ParagonWriter.ChunkEntrySize];
    foreach (var w in entries) {
      entryBuf.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[..4], w.ChunkNumber);
      BinaryPrimitives.WriteUInt64LittleEndian(entryBuf.Slice(4, 8), w.ChunkOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(12, 4), w.ChunkSize);
      entryBuf[16] = w.FlagByte;
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(20, 4), w.LogicalSize);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf.Slice(24, 4), w.Adler32);
      image.Write(entryBuf);
    }

    var newEof = newChunkTableOffset + (long)entries.Count * ParagonWriter.ChunkEntrySize;
    image.SetLength(newEof);

    // Patch the TOC: ChunkCount, ChunkTableOffset, TotalLogicalSize.
    // TotalLogicalSize reflects the live (latest-wins, non-tombstoned) view
    // so it matches the post-mutation payload size.
    var totalLogical = 0UL;
    var latest = new Dictionary<uint, WireEntry>();
    var ordering = new List<uint>();
    foreach (var w in entries) {
      if (!latest.ContainsKey(w.ChunkNumber)) ordering.Add(w.ChunkNumber);
      latest[w.ChunkNumber] = w;
    }
    foreach (var num in ordering) {
      var w = latest[num];
      if (w.FlagByte == ParagonWriter.TombstoneFlag) continue;
      totalLogical += w.LogicalSize;
    }

    Span<byte> patch = stackalloc byte[8];

    image.Seek(ParagonWriter.OffsetChunkCount, SeekOrigin.Begin);
    BinaryPrimitives.WriteUInt32LittleEndian(patch[..4], (uint)entries.Count);
    image.Write(patch[..4]);

    image.Seek(ParagonWriter.OffsetChunkTableOffset, SeekOrigin.Begin);
    BinaryPrimitives.WriteUInt64LittleEndian(patch, (ulong)newChunkTableOffset);
    image.Write(patch);

    image.Seek(ParagonWriter.OffsetTotalLogicalSize, SeekOrigin.Begin);
    BinaryPrimitives.WriteUInt64LittleEndian(patch, totalLogical);
    image.Write(patch);

    image.Flush();
  }

  /// <summary>
  /// Wire-form chunk-offset table entry — kept as a 1:1 mirror of the
  /// 40-byte on-disk record so the modifier can re-lay the table
  /// byte-identically (existing entries verbatim + new entries appended).
  /// </summary>
  private struct WireEntry {
    public uint ChunkNumber;
    public ulong ChunkOffset;
    public uint ChunkSize;
    /// <summary>Raw byte at offset +16 of the wire entry: <c>'Y'</c> for
    /// zlib-compressed, <c>'N'</c> for stored, <c>0xFF</c>
    /// (<see cref="ParagonWriter.TombstoneFlag"/>) for a Remove
    /// tombstone.</summary>
    public byte FlagByte;
    public uint LogicalSize;
    public uint Adler32;
  }
}

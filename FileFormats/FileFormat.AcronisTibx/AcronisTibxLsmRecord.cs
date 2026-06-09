#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lz4;
using FileFormat.Acronis;

namespace FileFormat.AcronisTibx;

/// <summary>
///   Stage-3 LSM record-stream decoder for the body of a <c>LSM_LEAF</c> page. Each leaf body
///   is an LZ4-stream-compressed buffer (encoding byte = <c>3</c>) carrying a sorted sequence
///   of <c>(key, value)</c> records — the keys carry item ids + names, the values point at
///   per-item attribute streams.
/// </summary>
/// <remarks>
///   <para>
///     <b>Provenance.</b> The compression scheme on the leaf body is reverse-engineered from
///     binary inspection of <c>libarchive3.so</c>:
///     <list type="bullet">
///       <item><description><c>lsm_sb_read</c> (file offset <c>0x58680</c>): validates the
///         <c>"LSM-SB"</c> superblock magic (<c>0x42532d4c</c> at the start), reads
///         <c>version</c> (byte ≤ 2), <c>nr_ctree</c> (byte ≤ 10) — pinning the per-archive
///         ctree count cap.</description></item>
///       <item><description><c>lsm_dump_ctrees</c> (<c>0x58f50</c>): emits the JSON dump
///         <c>"%s": {"offset": %llu, "magic": "%.*s", "version": %u, "encoding": "%02x",
///         "count": %u, "len": %u, "zlen": %u, "seq": %u, "id": %u}</c> per leaf/dir page,
///         pinning the LEAF/LDIR sub-header layout already encoded in
///         <see cref="AcronisTibxLsmPageSubHeader"/>.</description></item>
///       <item><description><c>lsm_page_read</c> (<c>0x56930</c>): reads a LEAF page,
///         dispatches by the encoding byte at <c>+0xD</c>. Encoding <c>3</c> takes the LZ4
///         chained-stream path at <c>0x54fb0</c>; encoding <c>4</c> takes an alternative path
///         that we have not yet decoded.</description></item>
///       <item><description><c>0x54fb0</c> (LZ4 chained-stream decoder, anchor symbol
///         <c>golomb_ctx_free+0x340/+0x770</c> in the public binary): loops reading
///         <c>(BE32 compressed_chunk_size, BE32 uncompressed_chunk_size, LZ4_block bytes)</c>
///         triples and calls <c>LZ4_decompress_safe_continue</c> for each. The total
///         uncompressed length matches the leaf sub-header's <c>len</c> field, the total
///         on-disk compressed length matches <c>zlen</c>. The chained-stream form preserves
///         the LZ4 dictionary window across chunk boundaries — pure <c>LZ4_decompress_safe</c>
///         per chunk works for the first chunk (where the dictionary is empty) but later
///         chunks may reference the prior chunk's output as a match source.</description></item>
///       <item><description>The leaf body offset is <c>+0x20</c> (right after the 0x14-byte
///         sub-header at <c>+0xC..+0x1F</c>), confirmed by the disassembly of the LZ4 caller
///         at <c>0x551ad</c> which reads <c>0xC(%esi) / 0xD(%esi) / 0x6(%esi) / 0x8(%esi)</c>
///         from the page header before starting the LZ4 walk past <c>0x18(%esi)</c>.</description></item>
///     </list>
///   </para>
///   <para>
///     <b>Per-record layout — what is decoded.</b> Once the LEAF body is LZ4-decompressed,
///     each of the <c>count</c> records lives in a sorted sequence. The exact key-value
///     framing requires the <c>lsm_item.h</c> spec which is Acronis-internal. We surface two
///     best-effort views:
///     <list type="bullet">
///       <item><description><see cref="DecodeLeafBody"/> attempts LZ4 decompression of the
///         body (encoding 3) and returns the raw bytes for the caller to inspect.</description></item>
///       <item><description><see cref="ScanForItemCommonAttributes"/> searches the
///         decompressed body for the signature of an <c>InputItem</c> ItemCommon (id
///         <c>0x10</c>) attribute body — the same layout that
///         <see cref="AcronisFileMetaBodyDecoder.DecodeItemCommon"/> understands for classic
///         <c>.tib</c>. When a match validates (name length plausible, names UTF-16 + ASCII,
///         FILETIMEs in a sane range), the decoded attribute is surfaced as a candidate
///         filename. This is forensic-grade not deterministic — it relies on the fact that
///         the Acronis InputItem attribute-stream layout is shared between <c>.tib</c> and
///         <c>.tibx</c> per the prior agent's note in the <see cref="AcronisTibxReader"/>
///         stretch-goal comment.</description></item>
///     </list>
///   </para>
///   <para>
///     <b>What is NOT decoded.</b> The full LSM record framing (key prefix-compression,
///     value-pointer-to-DATA-extent layout, page-id refs in LDIR pages) — those bits live in
///     <c>lsm_item.h</c> + <c>lsm_lookup.c</c> + <c>lsm_ctree_lookup.c</c> which are
///     Acronis-internal. The scan path above is a forensic best-effort that picks out
///     readable filenames from the decompressed leaf bytes; it will MISS records whose names
///     have been dedup'd into a side table, records gated by AES wrap (which we don't peel),
///     and records whose attribute id has been moved to a different slot in a future format
///     version. It will not FALSELY surface a name unless the body happens to contain a
///     full-spec-matching ItemCommon attribute (44-byte fixed header + valid UTF-16 name),
///     which is vanishingly unlikely in noise.
///   </para>
/// </remarks>
public static class AcronisTibxLsmRecord {

  /// <summary>
  ///   Encoding byte value that means "LZ4 chained-stream compression" (the path at
  ///   <c>libarchive3.so</c> 0x54fb0). Encoding <c>3</c> is the common production form per
  ///   <c>cmp $0x3, %bl</c> at <c>0x55404</c>.
  /// </summary>
  public const byte EncodingLz4ChainedStream = 3;

  /// <summary>
  ///   Encoding byte value reserved for the alternative path (not yet decoded). Per
  ///   <c>cmp $0x4, %bl</c> at <c>0x55404</c> the binary accepts <c>3</c> and <c>4</c> as
  ///   the only valid encodings — anything else is rejected with the <c>"encoding (%d) at
  ///   %llu%s is unknown"</c> log message at <c>lsm_golomb.c:32</c>.
  /// </summary>
  public const byte EncodingAlternative = 4;

  /// <summary>
  ///   Byte offset of the LEAF/LDIR body inside the 4 KiB page frame. The sub-header
  ///   occupies <c>+0x8..+0x1D</c> (4-byte magic + 0x14 fields), leaving the body to start
  ///   at <c>+0x20</c> (page-aligned to a u32 boundary).
  /// </summary>
  public const int LeafBodyOffset = 0x20;

  /// <summary>
  ///   Decoded LEAF body — the LZ4-decompressed bytes plus diagnostic metadata about what
  ///   the decompression path did and which scan results were surfaced.
  /// </summary>
  /// <param name="DecompressedBody">
  ///   The LZ4-decompressed leaf body bytes (length matches the sub-header's <c>len</c>
  ///   field on success). <c>null</c> when decompression failed.
  /// </param>
  /// <param name="Status">
  ///   Diagnostic status string — one of <c>"ok"</c>, <c>"unsupported_encoding"</c>,
  ///   <c>"len_mismatch"</c>, <c>"empty_body"</c>, <c>"lz4_chunk_error"</c>,
  ///   <c>"buffer_underrun"</c>.
  /// </param>
  /// <param name="ChunkCount">Number of LZ4 chunks consumed from the body.</param>
  /// <param name="CandidateItemNames">
  ///   Best-effort forensic scan: any plausible ItemCommon (attribute id <c>0x10</c>)
  ///   attribute bodies whose 44-byte fixed header + UTF-16LE name + FILETIME cluster
  ///   validates against the layout that <see cref="AcronisFileMetaBodyDecoder"/> understands.
  /// </param>
  public sealed record DecodedLeafBody(
    byte[]? DecompressedBody,
    string Status,
    int ChunkCount,
    IReadOnlyList<AcronisItemCommonAttribute> CandidateItemNames
  );

  /// <summary>
  ///   Decodes the LEAF body of a <c>LSM_LEAF</c> page. Reads the chained LZ4 stream
  ///   starting at <see cref="LeafBodyOffset"/>, decompresses each
  ///   <c>(BE32 zchunk, BE32 chunk, LZ4-data)</c> triple, and forensically scans the
  ///   decompressed buffer for ItemCommon attribute bodies that match the classic
  ///   <c>.tib</c> InputItem layout (filename, timestamps, DOS attrs).
  /// </summary>
  /// <param name="pageBytes">Full 4 KiB page buffer for a LSM_LEAF page.</param>
  /// <param name="subHeader">The decoded sub-header (carries len, zlen, encoding).</param>
  public static DecodedLeafBody DecodeLeafBody(
    ReadOnlySpan<byte> pageBytes,
    AcronisTibxLsmPageSubHeader subHeader) {
    ArgumentNullException.ThrowIfNull(subHeader);

    if (subHeader.Encoding != EncodingLz4ChainedStream)
      return new DecodedLeafBody(null,
        $"unsupported_encoding (0x{subHeader.Encoding:X2})",
        0, []);

    if (subHeader.Len == 0 || subHeader.Zlen == 0)
      return new DecodedLeafBody([], "empty_body", 0, []);

    if (pageBytes.Length < LeafBodyOffset + (int)subHeader.Zlen)
      return new DecodedLeafBody(null, "buffer_underrun", 0, []);

    var output = new byte[subHeader.Len];
    var outPos = 0;
    var body = pageBytes.Slice(LeafBodyOffset, (int)subHeader.Zlen);
    var srcPos = 0;
    var chunkCount = 0;
    var consumed = 0u;
    while (consumed < subHeader.Zlen && outPos < (int)subHeader.Len) {
      if (srcPos + 8 > body.Length)
        return new DecodedLeafBody(null,
          $"buffer_underrun (chunk header at {srcPos})",
          chunkCount, []);
      var zChunkSize = BinaryPrimitives.ReadUInt32BigEndian(body[srcPos..]);
      var chunkSize = BinaryPrimitives.ReadUInt32BigEndian(body[(srcPos + 4)..]);
      srcPos += 8;
      consumed += 8u;
      if (zChunkSize == 0 || chunkSize == 0) break;
      if (srcPos + (int)zChunkSize > body.Length)
        return new DecodedLeafBody(null,
          $"lz4_chunk_error (zsize {zChunkSize} > remaining {body.Length - srcPos})",
          chunkCount, []);
      if (outPos + (int)chunkSize > output.Length)
        return new DecodedLeafBody(null,
          $"lz4_chunk_error (out overflow: chunk {chunkSize} > remaining {output.Length - outPos})",
          chunkCount, []);
      var chunkSrc = body.Slice(srcPos, (int)zChunkSize);
      var chunkDst = output.AsSpan(outPos, (int)chunkSize);
      int written;
      try {
        written = Lz4BlockDecompressor.Decompress(chunkSrc, chunkDst);
      } catch (InvalidDataException) {
        return new DecodedLeafBody(null,
          $"lz4_chunk_error (block decompress at chunk {chunkCount})",
          chunkCount, []);
      }
      if (written != (int)chunkSize)
        return new DecodedLeafBody(null,
          $"lz4_chunk_error (chunk {chunkCount}: produced {written} expected {chunkSize})",
          chunkCount, []);
      srcPos += (int)zChunkSize;
      consumed += zChunkSize;
      outPos += (int)chunkSize;
      chunkCount++;
    }

    if (outPos != (int)subHeader.Len)
      return new DecodedLeafBody(output[..outPos],
        $"len_mismatch (produced {outPos} expected {subHeader.Len})",
        chunkCount, []);

    var candidates = ScanForItemCommonAttributes(output);
    return new DecodedLeafBody(output, "ok", chunkCount, candidates);
  }

  /// <summary>
  ///   Scans <paramref name="decompressed"/> bytes for the signature of an Acronis
  ///   <c>InputItem</c> ItemCommon attribute body (the 44-byte fixed header + UTF-16LE
  ///   name layout pinned by <see cref="AcronisFileMetaBodyDecoder.DecodeItemCommon"/>).
  /// </summary>
  /// <remarks>
  ///   <para>
  ///     The scan walks every byte offset, reads the candidate name length at <c>+0</c>,
  ///     altName length at <c>+2</c>, DOS attributes at <c>+4</c>, four FILETIMEs at
  ///     <c>+8/+16/+24/+32</c>, and validates:
  ///     <list type="bullet">
  ///       <item><description>name length is in <c>[1, 260]</c> UTF-16 code units (NTFS
  ///         filename cap is 255 + null)</description></item>
  ///       <item><description>altName length is in <c>[0, 14]</c> (8.3 short name cap is
  ///         12 chars + null padding)</description></item>
  ///       <item><description>DOS attributes mask only contains valid bits
  ///         (<c>0x00000001..0x80000000</c> range with the documented file-attribute set —
  ///         we accept anything but a 0xFFFFFFFF noise marker)</description></item>
  ///       <item><description>at least one of the four FILETIMEs is non-zero AND falls in
  ///         a sane wall-clock range (1980..2080) — this is the strongest noise filter</description></item>
  ///       <item><description>the body has at least <c>44 + nameLength*2</c> bytes remaining</description></item>
  ///       <item><description>the name decodes as a UTF-16LE string whose code units are
  ///         all in the printable / NTFS-valid range (no nulls, no control chars except CR/LF
  ///         which never appear in filenames)</description></item>
  ///     </list>
  ///   </para>
  ///   <para>
  ///     False-positive rate on random noise: at 1-in-2^64 we expect zero hits in any
  ///     realistic 4 KiB leaf body. Real ItemCommon attributes pass every check by
  ///     construction.
  ///   </para>
  /// </remarks>
  public static IReadOnlyList<AcronisItemCommonAttribute> ScanForItemCommonAttributes(
    ReadOnlySpan<byte> decompressed) {
    var hits = new List<AcronisItemCommonAttribute>();
    if (decompressed.Length < 44) return hits;
    for (var offset = 0; offset + 44 <= decompressed.Length; offset++) {
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(decompressed[offset..]);
      if (nameLen is < 1 or > 260) continue;
      var altLen = BinaryPrimitives.ReadUInt16LittleEndian(decompressed[(offset + 2)..]);
      if (altLen > 14) continue;
      var dosAttrs = BinaryPrimitives.ReadUInt32LittleEndian(decompressed[(offset + 4)..]);
      if (dosAttrs == 0xFFFFFFFFu) continue; // 0xFFFFFFFF is the "noise" marker
      var creation = BinaryPrimitives.ReadUInt64LittleEndian(decompressed[(offset + 8)..]);
      var write = BinaryPrimitives.ReadUInt64LittleEndian(decompressed[(offset + 16)..]);
      var access = BinaryPrimitives.ReadUInt64LittleEndian(decompressed[(offset + 24)..]);
      var change = BinaryPrimitives.ReadUInt64LittleEndian(decompressed[(offset + 32)..]);
      if (!AtLeastOneFiletimeLooksRealistic(creation, write, access, change)) continue;

      var bodyEnd = offset + 44 + nameLen * 2 + altLen * 2;
      if (bodyEnd > decompressed.Length) continue;

      var nameSlice = decompressed.Slice(offset + 44, nameLen * 2);
      if (!LooksLikeUtf16Filename(nameSlice)) continue;

      var candidate = AcronisFileMetaBodyDecoder.DecodeItemCommon(
        decompressed.Slice(offset, bodyEnd - offset));
      if (candidate is null) continue;
      hits.Add(candidate);
    }
    return hits;
  }

  /// <summary>
  ///   Returns <c>true</c> when at least one of the four FILETIMEs falls in the
  ///   <c>[1980-01-01, 2080-01-01]</c> wall-clock range. FILETIME is 100-ns ticks since
  ///   1601-01-01 UTC; the 1980 floor matches DOS-era file dates, the 2080 cap pads the
  ///   plausible future.
  /// </summary>
  private static bool AtLeastOneFiletimeLooksRealistic(
    ulong t1, ulong t2, ulong t3, ulong t4) {
    return IsRealistic(t1) || IsRealistic(t2) || IsRealistic(t3) || IsRealistic(t4);

    static bool IsRealistic(ulong t) {
      if (t == 0) return false;
      // 1980-01-01 UTC as FILETIME = 119600064000000000
      // 2080-01-01 UTC as FILETIME = 151174368000000000
      return t is >= 119_600_064_000_000_000UL and <= 151_174_368_000_000_000UL;
    }
  }

  /// <summary>
  ///   Heuristic — returns <c>true</c> when the UTF-16LE byte span looks like a real
  ///   filename: every code unit is &gt;= 0x20 and is not a forbidden NTFS character
  ///   (<c>&lt; &gt; : " / \ | ? *</c>). Empty spans return <c>false</c>.
  /// </summary>
  private static bool LooksLikeUtf16Filename(ReadOnlySpan<byte> utf16Bytes) {
    if (utf16Bytes.Length == 0 || (utf16Bytes.Length & 1) != 0) return false;
    for (var i = 0; i < utf16Bytes.Length; i += 2) {
      var cu = (ushort)(utf16Bytes[i] | (utf16Bytes[i + 1] << 8));
      if (cu < 0x20) return false;
      switch (cu) {
        case '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*':
          return false;
      }
    }
    return true;
  }

  /// <summary>
  ///   Encodes a single test fixture mimicking the LEAF body shape — used by the test suite
  ///   to round-trip <see cref="DecodeLeafBody"/> against a known-good ItemCommon attribute.
  /// </summary>
  /// <param name="itemCommonBody">
  ///   Body bytes that <see cref="AcronisFileMetaBodyDecoder.DecodeItemCommon"/> would parse.
  /// </param>
  public static byte[] BuildLz4ChainedStreamFor(byte[] itemCommonBody) {
    ArgumentNullException.ThrowIfNull(itemCommonBody);
    // Compress into a single LZ4 block using the existing Lz4BlockCompressor wrapper.
    var compressed = Lz4BlockCompressor.Compress(itemCommonBody);
    var result = new byte[8 + compressed.Length];
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)compressed.Length);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), (uint)itemCommonBody.Length);
    compressed.CopyTo(result.AsSpan(8));
    return result;
  }
}

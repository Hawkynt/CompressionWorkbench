#pragma warning disable CS1591
namespace FileFormat.EaseUs;

/// <summary>
/// One zlib-deflate substream located inside an EaseUS Todo Backup
/// <c>.pbd</c> container. The PBD body is a sequence of zlib streams (the
/// 0x78 header + Adler-32 trailer envelope around RFC-1951 DEFLATE);
/// binwalk routinely lists 100+ such streams per file, with stable header
/// positions for the first two metadata streams (offsets <c>0x98</c> and
/// <c>0x10F</c> across observed v1 / v2 backup pairs per the Rune-Server
/// reverse-engineering thread 694189) and shifting positions for the
/// payload streams (offsets <c>0xB28+</c>).
///
/// <para>
/// EaseUS never published the chunk-table framing that wraps each zlib
/// stream — so we cannot, offline, identify which logical sector a chunk
/// represents nor whether the inflated payload is plain volume data,
/// AES-256 ciphertext (for password-protected backups), or a parent-chain
/// pointer. What we CAN do, deterministically and reproducibly, is locate
/// each zlib header by linear scan, attempt a trial inflate, and record
/// the compressed + decompressed sizes. Forensic users get a verifiable
/// chunk inventory; everything beyond that requires the vendor engine.
/// </para>
/// </summary>
public sealed record EaseUsZlibChunk {

  /// <summary>Byte offset of the zlib header (the 0x78 byte) within the .pbd file.</summary>
  public long Offset { get; init; }

  /// <summary>
  /// The FCHECK / preset-dict byte that follows the 0x78 — one of
  /// 0x01 (no/low compression), 0x9C (default compression), 0xDA (best
  /// compression). Other bytes are skipped during the scan because the
  /// 0x78-byte alone is too prone to false positives.
  /// </summary>
  public byte FchByte { get; init; }

  /// <summary>
  /// Compressed substream length in bytes (header + DEFLATE + Adler-32),
  /// determined by the trial inflate. <c>0</c> when inflation failed.
  /// </summary>
  public long CompressedLength { get; init; }

  /// <summary>
  /// Decompressed payload length in bytes. <c>0</c> when inflation failed.
  /// </summary>
  public long DecompressedLength { get; init; }

  /// <summary>
  /// Outcome of the trial inflate. See <see cref="EaseUsChunkInflateStatus"/>.
  /// </summary>
  public EaseUsChunkInflateStatus InflateStatus { get; init; }

  /// <summary>
  /// True if the trial inflate succeeded and the decompressed payload was
  /// retained for forensic surfacing. Large payloads (above the reader's
  /// retention cap) are inflated but not retained to bound memory.
  /// </summary>
  public bool PayloadRetained { get; init; }

  /// <summary>
  /// Decompressed payload bytes (only populated when <see cref="PayloadRetained"/>
  /// is true; empty array otherwise — callers should consult
  /// <see cref="DecompressedLength"/> for the actual size).
  /// </summary>
  public byte[] Payload { get; init; } = [];
}

/// <summary>
/// Result of the trial inflate run against a candidate zlib substream
/// header located by linear scan.
/// </summary>
public enum EaseUsChunkInflateStatus {
  /// <summary>Trial inflate has not been attempted (placeholder).</summary>
  NotAttempted = 0,
  /// <summary>Inflated successfully end-to-end; Adler-32 trailer validated.</summary>
  Inflated = 1,
  /// <summary>
  /// Decoder rejected the bitstream — the 0x78-byte was a false positive,
  /// or the surrounding chunk-table framing offset the start by a few bytes.
  /// </summary>
  FailedHeaderInvalid = 2,
  /// <summary>
  /// Decoder consumed bytes but the stream ended before zlib's terminal
  /// block — either the chunk is segmented across vendor-private framing
  /// boundaries we don't understand, or this was a partial match.
  /// </summary>
  FailedTruncated = 3,
  /// <summary>
  /// Decoder threw a generic <see cref="InvalidDataException"/> at some
  /// point past the header — typical when the FCHECK byte matched by
  /// coincidence inside a different (non-zlib) data structure.
  /// </summary>
  FailedCorrupt = 4,
  /// <summary>
  /// Inflated payload exceeded the per-chunk decompressed cap; bytes were
  /// counted but not retained. This is rare for header / metadata chunks
  /// (typically &lt; 1 KiB) but expected for payload chunks once we hit
  /// the body region past offset <c>0xB28</c>.
  /// </summary>
  InflatedOverCap = 5,
}

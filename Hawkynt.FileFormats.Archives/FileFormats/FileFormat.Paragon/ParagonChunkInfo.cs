#pragma warning disable CS1591
namespace FileFormat.Paragon;

/// <summary>
/// A single entry in the CWBP chunk-offset table — surfaces the exact
/// per-chunk fields the vendor's
/// <c>"ChunkNumber: %d, ChunkOffSet: 0x%016I64x, ChunkSize: %d,
/// ChunkIsCompress: %c"</c> debug-string round-trip emits, plus the
/// additional <see cref="LogicalSize"/> and <see cref="Adler32"/> fields
/// our writer stores so the reader can verify and decompress without
/// guessing.
/// </summary>
public sealed class ParagonChunkInfo {

  /// <summary>The chunk's zero-based ordinal within its segment.</summary>
  public uint ChunkNumber { get; init; }

  /// <summary>Byte offset of the chunk's on-disk body within the file.</summary>
  public ulong ChunkOffset { get; init; }

  /// <summary>On-disk byte size of the chunk's body — either the
  /// uncompressed bytes or the zlib-stream bytes, depending on
  /// <see cref="IsCompressed"/>.</summary>
  public uint ChunkSize { get; init; }

  /// <summary>Per-chunk compress flag — true when the body is a zlib
  /// stream, false when stored verbatim.</summary>
  public bool IsCompressed { get; init; }

  /// <summary>
  /// True when this entry is a tombstone marker emitted by
  /// <c>ParagonInPlaceModifier.Remove</c>. Tombstones encode
  /// <c>IsCompressed = 0xFF</c> + <c>ChunkSize = 0</c> on the wire and
  /// suppress the chunk identified by <see cref="ChunkNumber"/> from the
  /// live-entry view. The original chunk body bytes stay byte-identical
  /// at their on-disk offset; only the chunk-table tail grows.
  /// </summary>
  public bool IsTombstone { get; init; }

  /// <summary>Decompressed (logical) byte size of the chunk — equal to
  /// <see cref="ChunkSize"/> when the chunk is stored verbatim.</summary>
  public uint LogicalSize { get; init; }

  /// <summary>Adler-32 of the decompressed bytes — the vendor's
  /// "Chunk is not valid, adler32 checksum is wrong." gate.</summary>
  public uint Adler32 { get; init; }
}

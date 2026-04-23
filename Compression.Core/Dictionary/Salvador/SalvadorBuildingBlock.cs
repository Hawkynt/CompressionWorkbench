using System.Buffers.Binary;
using Compression.Core.Dictionary.Zx0;
using Compression.Registry;

namespace Compression.Core.Dictionary.Salvador;

/// <summary>
/// Salvador — Emmanuel Marty's ZX0-based compressor with inverted Elias-gamma
/// offset encoding, used by Amiga 4K/64K demoscene productions.
/// </summary>
/// <remarks>
/// <para>
/// Reference implementation:
/// <c>https://raw.githubusercontent.com/emmanuel-marty/salvador/master/src/expand.c</c>
/// and <c>shrink.c</c> (zlib license). Salvador's default "V2 / inverted" mode
/// implements the same bit-stream layout as <see cref="Zx0BuildingBlock"/>
/// with one difference: the <b>offset MSB</b> Elias-gamma data bits are XOR'd
/// with 1 (inverted). Rep-match length, literal count, and new-offset length
/// Elias-gammas remain non-inverted.
/// </para>
/// <para>
/// Constants from Salvador's <c>format.h</c>:
/// <c>MIN_OFFSET=1</c>, <c>MAX_OFFSET=0x7F80</c> (32640), <c>MIN_MATCH_SIZE=1</c>,
/// <c>BLOCK_SIZE=0x10000</c>, <c>MAX_VARLEN=0xFFFF</c>.
/// </para>
/// <para>
/// Per the Salvador source header: "Implements the ZX0 encoding designed by
/// Einar Saukas." — this is not an independent range-coding compressor but
/// rather a ZX0-compatible LZ77 encoder retargeted for Amiga memory layouts.
/// Our port is a greedy LZ77 with the spec-compliant Salvador bit-stream
/// writer; the decoder accepts any valid FLG_IS_INVERTED forward stream.
/// </para>
/// </remarks>
public sealed class SalvadorBuildingBlock : IBuildingBlock {

  /// <inheritdoc/>
  public string Id => "BB_Salvador";

  /// <inheritdoc/>
  public string DisplayName => "Salvador";

  /// <inheritdoc/>
  public string Description => "Emmanuel Marty's Salvador — ZX0-compatible LZ77 with inverted Elias-gamma offsets, for Amiga 4K/64K productions";

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  // Salvador's "FLG_IS_INVERTED" (default forward mode) inverts the offset-MSB Elias-gamma bits.
  private const bool InvertMode = true;

  /// <summary>Compress <paramref name="data"/> with a 4-byte little-endian original-size prefix followed by a bare Salvador stream.</summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var ms = new MemoryStream();
    Span<byte> header = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
    ms.Write(header);

    if (data.Length == 0) return ms.ToArray();

    var body = Zx0BuildingBlock.CompressBare(data, InvertMode);
    ms.Write(body);
    return ms.ToArray();
  }

  /// <summary>Decompress a 4-byte-prefixed Salvador payload.</summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    if (data.Length < 4) throw new InvalidDataException("Salvador: input smaller than 4-byte header.");
    var targetSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    if (targetSize < 0) throw new InvalidDataException("Salvador: negative decompressed size.");
    if (targetSize == 0) return [];
    return Zx0BuildingBlock.DecompressCore(data[4..], targetSize, InvertMode);
  }

  /// <summary>
  /// Decompresses a bare Salvador / ZX0-inverted stream (no 4-byte size prefix)
  /// into a freshly-allocated buffer of exactly <paramref name="exactOutputSize"/>
  /// bytes. Exposed for callers parsing embedded Salvador streams (Amiga
  /// intros/cruncher stubs).
  /// </summary>
  public static byte[] DecompressRaw(ReadOnlySpan<byte> compressed, int exactOutputSize) =>
    Zx0BuildingBlock.DecompressCore(compressed, exactOutputSize, InvertMode);
}

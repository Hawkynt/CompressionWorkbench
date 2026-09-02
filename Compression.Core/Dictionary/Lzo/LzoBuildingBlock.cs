using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzo;

/// <summary>
/// Exposes the LZO1X algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class LzoBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzo";
  /// <inheritdoc/>
  public string DisplayName => "LZO1X";
  /// <inheritdoc/>
  public string Description => "Extremely fast dictionary compression, optimized for decompression speed";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = Lzo1xCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return Lzo1xDecompressor.Decompress(data[4..], originalSize);
  }
}

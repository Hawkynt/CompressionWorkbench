using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Brotli;

/// <summary>
/// Exposes the Brotli algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class BrotliBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Brotli";
  /// <inheritdoc/>
  public string DisplayName => "Brotli";
  /// <inheritdoc/>
  public string Description => "Modern LZ77+Huffman compression with static dictionary, designed by Google";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = BrotliCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    _ = originalSize; // Brotli is self-terminating, but we store size for validation
    return BrotliDecompressor.Decompress(data[4..]);
  }
}

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
  public string Description => "Modern LZ77 and Huffman compression designed by Google (RFC 7932), with literal context modelling, distance ring-buffer reuse and cost-driven meta-block splitting. The static dictionary and block-switch commands are not emitted, so output is smaller than a plain LZ77 and Huffman pass but larger than the reference encoder at its highest quality.";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    // CompressLz77 already falls back to an uncompressed meta-block per
    // meta-block whenever entropy coding would not pay, so no second whole-stream
    // comparison is needed. Empty input produces an empty payload, matching the
    // empty-in/empty-out contract the Cipher port has to honour.
    var compressed = data.Length == 0 ? [] : BrotliCompressor.CompressLz77(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    _ = originalSize; // Brotli is self-terminating, but we store size for validation
    return data.Length <= 4 ? [] : BrotliDecompressor.Decompress(data[4..]);
  }
}

using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Arj;

/// <summary>
/// Exposes the ARJ compression algorithm as a benchmarkable building block.
/// Uses method 1 (LZ77+Huffman with 26 KB window).
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class ArjBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Arj";
  /// <inheritdoc/>
  public string DisplayName => "ARJ";
  /// <inheritdoc/>
  public string Description => "Modified LZ77+Huffman used in ARJ archives";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var encoder = new ArjEncoder();
    var compressed = encoder.Encode(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    using var ms = new MemoryStream(data[4..].ToArray());
    var decoder = new ArjDecoder(ms);
    return decoder.Decode(originalSize);
  }
}

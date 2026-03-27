using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Exposes the LZH (Lempel-Ziv-Huffman) algorithm as a benchmarkable building block.
/// Uses the LH5 method (13-bit position, standard for LHA/LZH archives).
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class LzhBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzh";
  /// <inheritdoc/>
  public string DisplayName => "LZH (LH5)";
  /// <inheritdoc/>
  public string Description => "Lempel-Ziv with adaptive Huffman coding, used in LHA archives";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var encoder = new LzhEncoder();
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
    var decoder = new LzhDecoder(ms);
    return decoder.Decode(originalSize);
  }
}

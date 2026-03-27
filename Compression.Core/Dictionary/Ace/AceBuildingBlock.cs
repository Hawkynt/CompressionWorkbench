using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// Exposes the ACE compression algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class AceBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Ace";
  /// <inheritdoc/>
  public string DisplayName => "ACE";
  /// <inheritdoc/>
  public string Description => "LZ77+Huffman compression from the ACE archive format";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var encoder = new AceEncoder();
    var compressed = encoder.Encode(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    var decoder = new AceDecoder();
    return decoder.Decode(data[4..].ToArray(), originalSize);
  }
}

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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Lzh";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZH (LH5)";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Lempel-Ziv with adaptive Huffman coding, used in LHA archives";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var encoder = new LzhEncoder();
    var compressed = encoder.Encode(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    using var ms = new MemoryStream(data[4..].ToArray());
    var decoder = new LzhDecoder(ms);
    return decoder.Decode(originalSize);
  }
}

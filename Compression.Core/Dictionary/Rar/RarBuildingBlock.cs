using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Rar;

/// <summary>
/// Exposes the RAR5 compression algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class RarBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Rar";
  /// <inheritdoc/>
  public string DisplayName => "RAR5";
  /// <inheritdoc/>
  public string Description => "LZ+Huffman+PPM compression from the RAR5 archive format";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var encoder = new Rar5Encoder();
    var compressed = encoder.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    var decoder = new Rar5Decoder(Rar5Constants.MinDictionarySize);
    return decoder.Decompress(data[4..], originalSize);
  }
}

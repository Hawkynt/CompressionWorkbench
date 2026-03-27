using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// Exposes the SQX LZH compression algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class SqxBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Sqx";
  /// <inheritdoc/>
  public string DisplayName => "SQX";
  /// <inheritdoc/>
  public string Description => "LZ+Huffman compression from the SQX archive format";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  private const int DictSize = 1 << 15; // 32 KB

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = SqxEncoder.Encode(data, DictSize);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return SqxDecoder.Decode(data[4..].ToArray(), originalSize, DictSize);
  }
}

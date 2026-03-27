using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lz4;

/// <summary>
/// Exposes the LZ4 block compression algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class Lz4BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lz4";
  /// <inheritdoc/>
  public string DisplayName => "LZ4";
  /// <inheritdoc/>
  public string Description => "Extremely fast LZ77-family block compression optimized for speed";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressed = Lz4BlockCompressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return Lz4BlockDecompressor.Decompress(data[4..], originalSize);
  }
}

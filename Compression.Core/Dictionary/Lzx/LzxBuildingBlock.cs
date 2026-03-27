using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzx;

/// <summary>
/// Exposes the LZX algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class LzxBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzx";
  /// <inheritdoc/>
  public string DisplayName => "LZX";
  /// <inheritdoc/>
  public string Description => "LZ77+Huffman compression used in CAB and CHM archives (Microsoft)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressor = new LzxCompressor();
    var compressed = compressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    using var ms = new MemoryStream(data[4..].ToArray());
    var decompressor = new LzxDecompressor(ms);
    return decompressor.Decompress(originalSize);
  }
}

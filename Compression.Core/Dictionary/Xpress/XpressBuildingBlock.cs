using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Exposes the XPRESS Huffman algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class XpressBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Xpress";
  /// <inheritdoc/>
  public string DisplayName => "XPRESS Huffman";
  /// <inheritdoc/>
  public string Description => "LZ77+Huffman compression used in Windows (NTFS, WIM, Hyper-V)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    return XpressHuffmanDecompressor.Decompress(data[4..], originalSize);
  }
}

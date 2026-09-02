using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Xpress;

/// <summary>
/// Exposes the XPRESS Huffman algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class XpressBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Xpress";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "XPRESS Huffman";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "LZ77+Huffman compression used in Windows (NTFS, WIM, Hyper-V)";
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
    var compressor = new XpressHuffmanCompressor();
    var compressed = compressor.Compress(data);
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
    return XpressHuffmanDecompressor.Decompress(data[4..], originalSize);
  }
}

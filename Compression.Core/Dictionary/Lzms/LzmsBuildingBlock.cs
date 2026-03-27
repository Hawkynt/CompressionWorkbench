using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// Exposes the LZMS algorithm as a benchmarkable building block.
/// Prepends a 4-byte LE uncompressed size header for round-trip support.
/// </summary>
public sealed class LzmsBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzms";
  /// <inheritdoc/>
  public string DisplayName => "LZMS";
  /// <inheritdoc/>
  public string Description => "LZ+Markov+Shannon compression with delta matching, used in Windows WIM";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var compressor = new LzmsCompressor();
    var compressed = compressor.Compress(data);
    var result = new byte[4 + compressed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, data.Length);
    compressed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalSize = BinaryPrimitives.ReadInt32LittleEndian(data);
    var decompressor = new LzmsDecompressor();
    return decompressor.Decompress(data[4..], originalSize);
  }
}

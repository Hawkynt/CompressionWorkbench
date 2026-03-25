using Compression.Registry;

namespace Compression.Core.Deflate;

/// <summary>
/// Exposes the raw DEFLATE algorithm (RFC 1951) as a benchmarkable building block.
/// </summary>
public sealed class DeflateBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Deflate";
  /// <inheritdoc/>
  public string DisplayName => "DEFLATE";
  /// <inheritdoc/>
  public string Description => "LZ77 + Huffman, the algorithm inside gzip/zip/png";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => DeflateCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DeflateDecompressor.Decompress(data);
}

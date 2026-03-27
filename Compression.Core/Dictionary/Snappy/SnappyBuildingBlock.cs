using Compression.Registry;

namespace Compression.Core.Dictionary.Snappy;

/// <summary>
/// Exposes the Snappy algorithm as a benchmarkable building block.
/// Snappy's format is self-describing (varint size header), so no extra framing needed.
/// </summary>
public sealed class SnappyBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Snappy";
  /// <inheritdoc/>
  public string DisplayName => "Snappy";
  /// <inheritdoc/>
  public string Description => "Fast LZ77-family compression designed by Google for speed over ratio";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) =>
    SnappyCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) =>
    SnappyDecompressor.Decompress(data);
}

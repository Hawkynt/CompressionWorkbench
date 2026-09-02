using Compression.Registry;

namespace Compression.Core.Deflate;

/// <summary>
/// Exposes the Deflate64 (Enhanced Deflate) algorithm as a benchmarkable building block.
/// </summary>
public sealed class Deflate64BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Deflate64";
  /// <inheritdoc/>
  public string DisplayName => "Deflate64";
  /// <inheritdoc/>
  public string Description => "Enhanced DEFLATE with 64KB window and extended codes";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => Deflate64Compressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => Deflate64Decompressor.Decompress(data);
}

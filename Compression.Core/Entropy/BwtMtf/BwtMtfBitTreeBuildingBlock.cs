using Compression.Registry;

namespace Compression.Core.Entropy.BwtMtf;

/// <summary>
/// Exposes <see cref="BwtMtfBitTreeCompressor"/> (cyclic BWT, Move-to-Front, adaptive bit-tree
/// range coder) as a benchmarkable building block.
/// </summary>
public sealed class BwtMtfBitTreeBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BwtMtfBitTree";
  /// <inheritdoc/>
  public string DisplayName => "BWT + MTF + bit-tree range coder";
  /// <inheritdoc/>
  public string Description => "Cyclic Burrows-Wheeler Transform, Move-to-Front, and an adaptive bit-tree range coder with two contexts (after a zero rank, otherwise); this repository's own layout";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BwtMtfBitTreeCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BwtMtfBitTreeCompressor.Decompress(data);
}

using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Ctw;

/// <summary>
/// Exposes genuine Context Tree Weighting (CTW) as a benchmarkable building block:
/// a bounded-depth binary context tree with a Krichevsky-Trofimov estimator at every
/// node, recursively weighted between each node's own estimate and the product of its
/// children, driving the repository's binary arithmetic coder. See
/// <see cref="ContextTreeWeightingCompressor"/> for the full model description and citation.
/// </summary>
public sealed class ContextTreeWeightingBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ContextTreeWeighting";
  /// <inheritdoc/>
  public string DisplayName => "Context Tree Weighting";
  /// <inheritdoc/>
  public string Description => $"Context Tree Weighting (Willems/Shtarkov/Tjalkens): depth-{ContextTreeWeightingCompressor.ContextDepthBits} binary context tree with a Krichevsky-Trofimov estimator per node, recursively weighted and arithmetic-coded";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ContextTreeWeightingCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ContextTreeWeightingCompressor.Decompress(data);
}

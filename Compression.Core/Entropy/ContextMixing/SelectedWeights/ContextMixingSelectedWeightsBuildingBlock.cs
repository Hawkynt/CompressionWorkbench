using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.SelectedWeights;

/// <summary>
/// Exposes <see cref="ContextMixingSelectedWeightsCompressor"/> (orders 0-6 and a match model,
/// mixer weight sets selected by the previous byte, one SSE stage) as a benchmarkable building block.
/// </summary>
public sealed class ContextMixingSelectedWeightsBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ContextMixingSelectedWeights";
  /// <inheritdoc/>
  public string DisplayName => "Context mixing with context-selected weights";
  /// <inheritdoc/>
  public string Description => "Hashed order-0..6 contexts and a match model mixed by 16 weight sets chosen by the previous byte's high nibble, with one SSE stage; this repository's own layout";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ContextMixingSelectedWeightsCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ContextMixingSelectedWeightsCompressor.Decompress(data);
}

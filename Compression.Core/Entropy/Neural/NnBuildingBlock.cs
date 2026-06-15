using Compression.Registry;

namespace Compression.Core.Entropy.Neural;

/// <summary>
/// Exposes the online neural predictor as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Wraps <see cref="NnCompressor"/>: a two-layer perceptron with a nonlinear
/// hidden layer and backpropagation, trained online over order-0..3 and sparse
/// bit-context features, driving a binary arithmetic coder. This is the
/// clean-room NNCP-style neural sequence-prediction primitive.
/// </remarks>
public sealed class NnBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Neural";
  /// <inheritdoc/>
  public string DisplayName => "Neural";
  /// <inheritdoc/>
  public string Description => "Online-trained two-layer neural predictor (backprop) driving a binary arithmetic coder (NNCP-style)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => NnCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => NnCompressor.Decompress(data);
}

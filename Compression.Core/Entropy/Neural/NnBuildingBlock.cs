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
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Neural";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Neural";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Online-trained two-layer neural predictor (backprop) driving a binary arithmetic coder (NNCP-style)";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) => NnCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) => NnCompressor.Decompress(data);
}

using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// Exposes the logistic-domain context-mixing compressor as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Wraps <see cref="CmCompressor"/>: hashed order-0..6 bit models mixed in the
/// stretch domain, an adaptive probability map (SSE) refinement stage, and a
/// binary arithmetic coder. This is the clean-room PAQ/lpaq-style primitive
/// underlying formats such as ZPAQ, PAQ8 and cmix.
/// </remarks>
public sealed class CmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ContextMixing";
  /// <inheritdoc/>
  public string DisplayName => "Context Mixing";
  /// <inheritdoc/>
  public string Description => "Logistic-domain context mixing with SSE and a binary arithmetic coder (PAQ/lpaq-style)";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => CmCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CmCompressor.Decompress(data);
}

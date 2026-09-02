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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_ContextMixing";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Context Mixing";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Logistic-domain context mixing with SSE and a binary arithmetic coder (PAQ/lpaq-style)";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) => CmCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CmCompressor.Decompress(data);
}

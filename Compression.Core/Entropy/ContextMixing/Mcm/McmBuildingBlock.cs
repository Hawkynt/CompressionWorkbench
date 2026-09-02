using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>
/// Exposes the MCM-style two-level context-mixing network as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Reduced, clean-room reimplementation of Mathieu Chartier's MCM architecture:
/// three grouped context-mixers (local orders 0-2, medium orders 3-4, and a
/// wide group of order-6 plus a sparse skip-1 context) combined by a top-level
/// mixing stage and refined by a two-stage SSE chain. See
/// <see cref="McmCompressor"/> for the full model description and citations.
/// </remarks>
public sealed class McmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Mcm";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "MCM (reduced)";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Two-level context-mixing network: three grouped mixers combined by a top-level mixer, MCM-style";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data) => McmCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) => McmCompressor.Decompress(data);
}

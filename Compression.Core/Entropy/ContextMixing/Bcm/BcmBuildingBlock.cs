using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Bcm;

/// <summary>
/// Exposes the BCM-style BWT + context-mixing compressor as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Reduced, clean-room reimplementation of Ilya Muravyov's BCM architecture: a
/// Burrows-Wheeler Transform (<see cref="Transforms.BurrowsWheelerTransform"/>)
/// feeding a compact context-mixing back end (orders 0-2 over the sorted
/// string, one mixer, one SSE stage). See <see cref="BcmCompressor"/> for the
/// full model description and citations.
/// </remarks>
public sealed class BcmBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Bcm";
  /// <inheritdoc/>
  public string DisplayName => "BCM (reduced)";
  /// <inheritdoc/>
  public string Description => "Burrows-Wheeler Transform with a compact order-0..2 context-mixing back end, BCM-style";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BcmCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BcmCompressor.Decompress(data);
}

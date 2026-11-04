using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.BwtCm;

/// <summary>
/// Exposes <see cref="BwtContextMixingCompressor"/> (cyclic BWT, order-0..2 context mixing with
/// one SSE stage) as a benchmarkable building block.
/// </summary>
public sealed class BwtContextMixingBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_BwtContextMixing";
  /// <inheritdoc/>
  public string DisplayName => "BWT + context mixing (orders 0-2)";
  /// <inheritdoc/>
  public string Description => "Cyclic Burrows-Wheeler Transform with an order-0..2 context-mixing back end and one SSE stage; this repository's own layout";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BwtContextMixingCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BwtContextMixingCompressor.Decompress(data);
}

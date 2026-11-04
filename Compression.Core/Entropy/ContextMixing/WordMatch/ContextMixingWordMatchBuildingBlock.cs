using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.WordMatch;

/// <summary>
/// Exposes <see cref="ContextMixingWordMatchCompressor"/> (orders 0-6, a word model and a match
/// model, one mixer, two SSE stages) as a benchmarkable building block.
/// </summary>
public sealed class ContextMixingWordMatchBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ContextMixingWordMatch";
  /// <inheritdoc/>
  public string DisplayName => "Context mixing with word and match models";
  /// <inheritdoc/>
  public string Description => "Hashed order-0..6 contexts, a word context and a match model mixed by one logistic mixer with a two-stage SSE chain; this repository's own layout";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ContextMixingWordMatchCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ContextMixingWordMatchCompressor.Decompress(data);
}

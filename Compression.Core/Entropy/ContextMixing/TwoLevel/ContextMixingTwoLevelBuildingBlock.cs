using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.TwoLevel;

/// <summary>
/// Exposes <see cref="ContextMixingTwoLevelCompressor"/> (three grouped mixers combined by a
/// top-level mixer, two SSE stages) as a benchmarkable building block.
/// </summary>
public sealed class ContextMixingTwoLevelBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_ContextMixingTwoLevel";
  /// <inheritdoc/>
  public string DisplayName => "Two-level context-mixing network";
  /// <inheritdoc/>
  public string Description => "Local, medium and wide/sparse context groups, each with its own mixer, combined by a top-level mixer and two SSE stages; this repository's own layout";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ContextMixingTwoLevelCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ContextMixingTwoLevelCompressor.Decompress(data);
}

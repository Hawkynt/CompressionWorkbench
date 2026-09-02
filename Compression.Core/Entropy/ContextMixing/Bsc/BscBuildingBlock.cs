using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Bsc;

/// <summary>
/// Exposes the BSC-style BWT + Move-to-Front + adaptive coder as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Reduced, clean-room reimplementation of Ilya Grebnov's libbsc architecture:
/// a Burrows-Wheeler Transform (<see cref="Transforms.BurrowsWheelerTransform"/>),
/// a Move-to-Front recoding (<see cref="Transforms.MoveToFrontTransform"/>), and
/// a two-context adaptive bit-tree coder — deliberately lighter than a full
/// context-mixing back end. See <see cref="BscCompressor"/> for the model
/// description and citations.
/// </remarks>
public sealed class BscBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Bsc";
  /// <inheritdoc/>
  public string DisplayName => "BSC (reduced)";
  /// <inheritdoc/>
  public string Description => "Burrows-Wheeler Transform, Move-to-Front, and a two-context adaptive bit-tree coder, libbsc-style";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => BscCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => BscCompressor.Decompress(data);
}

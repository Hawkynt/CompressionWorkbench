using Compression.Registry;

namespace Compression.Core.Entropy.ContextMixing.Csc;

/// <summary>
/// Exposes the CSC-style LZ77 + context-mixing compressor as a benchmarkable building block.
/// </summary>
/// <remarks>
/// Reduced, clean-room reimplementation of Fu Siyuan's CSC architecture: an
/// LZ77 parse (<see cref="Dictionary.Lz77.Lz77Compressor"/>) whose flag and
/// literal streams are coded with logistic-domain context mixing, while match
/// length/distance use simple order-0 adaptive bit-trees. See
/// <see cref="CscCompressor"/> for the full model description and citations.
/// </remarks>
public sealed class CscBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Csc";
  /// <inheritdoc/>
  public string DisplayName => "CSC (reduced)";
  /// <inheritdoc/>
  public string Description => "LZ77 parsing with context-mixed literal/flag coding and order-0 length/distance channels, CSC-style";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => CscCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CscCompressor.Decompress(data);
}

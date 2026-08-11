using Compression.Registry;

namespace Compression.Core.Dictionary.Sequitur;

/// <summary>
/// Exposes Sequitur as a benchmarkable building block: an online algorithm that
/// infers a straight-line context-free grammar from the input by enforcing digram
/// uniqueness and rule utility as each symbol is appended, so repeated phrases
/// collapse into rules and repeated sequences of rules collapse in turn. See
/// <see cref="SequiturCompressor"/> for the full algorithm description and citation.
/// </summary>
public sealed class SequiturBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Sequitur";
  /// <inheritdoc/>
  public string DisplayName => "Sequitur";
  /// <inheritdoc/>
  public string Description => "Online grammar inference (Nevill-Manning & Witten): enforces digram uniqueness and rule utility as symbols are appended, collapsing repeated phrases into a straight-line grammar";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => SequiturCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => SequiturCompressor.Decompress(data);
}

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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Csc";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "CSC (reduced)";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "LZ77 parsing with context-mixed literal/flag coding and order-0 length/distance channels, CSC-style";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.ContextMixing;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) => CscCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) => CscCompressor.Decompress(data);
}

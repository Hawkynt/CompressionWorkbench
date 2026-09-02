using Compression.Registry;

namespace Compression.Core.Dictionary.Lzp;

/// <summary>
/// Exposes the LZP (Lempel-Ziv Prediction) algorithm as a benchmarkable building block.
/// LZP's format is self-describing (5-byte header with order and original size).
/// </summary>
public sealed class LzpBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Lzp";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZP";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Lempel-Ziv Prediction using context-based match prediction";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) =>
    LzpCompressor.Compress(data.ToArray());

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) =>
    LzpDecompressor.Decompress(data.ToArray());
}

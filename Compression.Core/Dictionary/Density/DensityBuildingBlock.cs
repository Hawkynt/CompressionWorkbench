using Compression.Registry;

namespace Compression.Core.Dictionary.Density;

/// <summary>
/// Exposes the Density "Chameleon" algorithm as a benchmarkable building block.
/// </summary>
/// <remarks>
/// See <see cref="DensityConstants"/> for the format layout and provenance notes.
/// </remarks>
public sealed class DensityBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Density";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Density (Chameleon)";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Predictive 4-byte-chunk dictionary coder: a hash of the previous chunk predicts the next one at zero bit cost when correct";
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
    DensityChameleonCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) =>
    DensityChameleonDecompressor.Decompress(data);
}

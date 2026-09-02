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
  public string Id => "BB_Density";
  /// <inheritdoc/>
  public string DisplayName => "Density (Chameleon)";
  /// <inheritdoc/>
  public string Description => "Predictive 4-byte-chunk dictionary coder: a hash of the previous chunk predicts the next one at zero bit cost when correct";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) =>
    DensityChameleonCompressor.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) =>
    DensityChameleonDecompressor.Decompress(data);
}

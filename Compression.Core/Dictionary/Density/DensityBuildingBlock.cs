using Compression.Registry;

namespace Compression.Core.Dictionary.Density;

/// <summary>
/// density's Chameleon algorithm as a building block. The block's bytes are exactly what
/// density-rs 0.16.6's <c>Chameleon::encode</c> writes (no header; the stream ends with its input);
/// see <see cref="DensityCodec"/>.
/// </summary>
public sealed class DensityBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Density";
  /// <inheritdoc/>
  public string DisplayName => "Density (Chameleon)";
  /// <inheritdoc/>
  public string Description => "density-rs Chameleon: each 4-byte quad is either its 16-bit hash in a dictionary or the quad itself";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => DensityCodec.Encode(data, DensityAlgorithm.Chameleon);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => DensityCodec.Decode(data, DensityAlgorithm.Chameleon);
}

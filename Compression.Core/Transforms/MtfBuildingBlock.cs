using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes the Move-to-Front Transform as a benchmarkable building block.
/// </summary>
public sealed class MtfBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Mtf";
  /// <inheritdoc/>
  public string DisplayName => "MTF";
  /// <inheritdoc/>
  public string Description => "Move-to-Front Transform, converts repeated patterns to small values";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => MoveToFrontTransform.Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => MoveToFrontTransform.Decode(data);
}

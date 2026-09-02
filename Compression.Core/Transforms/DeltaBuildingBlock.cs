using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes the Delta filter as a benchmarkable building block.
/// </summary>
public sealed class DeltaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Delta";
  /// <inheritdoc/>
  public string DisplayName => "Delta";
  /// <inheritdoc/>
  public string Description => "Delta filter, stores differences between consecutive bytes";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => DeltaFilter.Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DeltaFilter.Decode(data);
}

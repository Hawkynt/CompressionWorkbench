using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes the Delta filter as a benchmarkable building block.
/// </summary>
public sealed class DeltaBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Delta";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Delta";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Delta filter, stores differences between consecutive bytes";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data)
    => DeltaFilter.Encode(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data)
    => DeltaFilter.Decode(data);
}

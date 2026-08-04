using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes Delta + RLE (see <see cref="DeltaRleEncoding"/>) as a benchmarkable building
/// block: the Delta filter followed by run-length encoding of the delta stream. Distinct
/// from the pure filter exposed by <see cref="DeltaBuildingBlock"/> (id <c>BB_Delta</c>),
/// which never changes the data length — this variant actually compresses repetitive data.
/// </summary>
public sealed class DeltaRleBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_DeltaRle";
  /// <inheritdoc/>
  public string DisplayName => "Delta + RLE";
  /// <inheritdoc/>
  public string Description => "Delta filter followed by run-length encoding of the delta stream; unlike the pure Delta filter (BB_Delta), this compresses repetitive data.";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => DeltaRleEncoding.Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DeltaRleEncoding.Decode(data);
}

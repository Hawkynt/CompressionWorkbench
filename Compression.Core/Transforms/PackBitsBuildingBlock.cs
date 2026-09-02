using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes Apple PackBits as a benchmarkable building block.
/// </summary>
public sealed class PackBitsBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_PackBits";
  /// <inheritdoc/>
  public string DisplayName => "PackBits";
  /// <inheritdoc/>
  public string Description => "Apple PackBits run-length encoding used by TIFF and PostScript";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => PackBitsEncoding.Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => PackBitsEncoding.Decode(data);
}

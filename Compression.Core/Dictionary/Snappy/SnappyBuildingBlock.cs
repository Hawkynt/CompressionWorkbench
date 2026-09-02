using Compression.Registry;

namespace Compression.Core.Dictionary.Snappy;

/// <summary>
/// Exposes the Snappy algorithm as a benchmarkable building block.
/// Snappy's format is self-describing (varint size header), so no extra framing needed.
/// </summary>
public sealed class SnappyBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Snappy";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Snappy";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Fast LZ77-family compression designed by Google for speed over ratio";
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
    SnappyCompressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data) =>
    SnappyDecompressor.Decompress(data);
}

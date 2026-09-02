using Compression.Registry;

namespace Compression.Core.Deflate;

/// <summary>
/// Exposes the Deflate64 (Enhanced Deflate) algorithm as a benchmarkable building block.
/// </summary>
public sealed class Deflate64BuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Deflate64";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Deflate64";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Enhanced DEFLATE with 64KB window and extended codes";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
public byte[] Compress(ReadOnlySpan<byte> data)
    => Deflate64Compressor.Compress(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data)
    => Deflate64Decompressor.Decompress(data);
}

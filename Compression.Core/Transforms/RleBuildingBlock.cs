using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes Run-Length Encoding as a benchmarkable building block.
/// </summary>
public sealed class RleBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BB_Rle";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "RLE";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Run-Length Encoding, replaces repeated bytes with count+value pairs";
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
    => RunLengthEncoding.Encode(data);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Decompress(ReadOnlySpan<byte> data)
    => RunLengthEncoding.Decode(data);
}

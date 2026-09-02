using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes the Burrows-Wheeler Transform as a benchmarkable building block.
/// Prepends a 4-byte LE original index to the transformed data.
/// </summary>
public sealed class BwtBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Bwt";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "BWT";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Burrows-Wheeler Transform, reorders bytes for better compression";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var (transformed, originalIndex) = BurrowsWheelerTransform.Forward(data);
    var result = new byte[4 + transformed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, originalIndex);
    transformed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalIndex = BinaryPrimitives.ReadInt32LittleEndian(data);
    return BurrowsWheelerTransform.Inverse(data[4..], originalIndex);
  }
}

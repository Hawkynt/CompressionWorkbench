using System.Buffers.Binary;
using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes the Burrows-Wheeler Transform as a benchmarkable building block.
/// Prepends a 4-byte LE original index to the transformed data.
/// </summary>
public sealed class BwtBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Bwt";
  /// <inheritdoc/>
  public string DisplayName => "BWT";
  /// <inheritdoc/>
  public string Description => "Burrows-Wheeler Transform, reorders bytes for better compression";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    var (transformed, originalIndex) = BurrowsWheelerTransform.Forward(data);
    var result = new byte[4 + transformed.Length];
    BinaryPrimitives.WriteInt32LittleEndian(result, originalIndex);
    transformed.CopyTo(result.AsSpan(4));
    return result;
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    var originalIndex = BinaryPrimitives.ReadInt32LittleEndian(data);
    return BurrowsWheelerTransform.Inverse(data[4..], originalIndex);
  }
}

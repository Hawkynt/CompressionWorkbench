using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes ZFS's zero-length encoding (see <see cref="ZleEncoding"/>) as a benchmarkable
/// building block. The stream is self-delimiting, so no size header is added.
/// </summary>
public sealed class ZleBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Zle";
  /// <inheritdoc/>
  public string DisplayName => "ZLE";
  /// <inheritdoc/>
  public string Description => "ZFS zero-length encoding: zero runs collapse to one byte, other bytes pass as short literal runs";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ZleEncoding.Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ZleEncoding.Decode(data);
}

using Compression.Registry;

namespace Compression.Core.Transforms;

/// <summary>
/// Exposes Run-Length Encoding as a benchmarkable building block.
/// </summary>
public sealed class RleBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Rle";
  /// <inheritdoc/>
  public string DisplayName => "RLE";
  /// <inheritdoc/>
  public string Description => "Run-Length Encoding, replaces repeated bytes with count+value pairs";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Transform;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => RunLengthEncoding.Encode(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => RunLengthEncoding.Decode(data);
}

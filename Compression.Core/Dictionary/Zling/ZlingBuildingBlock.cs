using Compression.Registry;

namespace Compression.Core.Dictionary.Zling;

/// <summary>
/// zling — Zhang Li's ROLZ + Huffman compressor. The block's bytes are a zling stream exactly as
/// the reference demo's <c>zling e</c> (level 0) writes it; see <see cref="ZlingCodec"/>.
/// </summary>
public sealed class ZlingBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Zling";
  /// <inheritdoc/>
  public string DisplayName => "Zling";
  /// <inheritdoc/>
  public string Description => "Zhang Li's zling: ROLZ over 4096 positions per byte context, MTF literals and two Huffman tables per block";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) => ZlingCodec.Compress(data);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) => ZlingCodec.Decompress(data);
}

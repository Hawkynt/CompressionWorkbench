using Compression.Registry;

namespace Compression.Core.Deflate;

/// <summary>
/// Exposes Zopfli-style compression as a benchmarkable building block. Drives
/// <see cref="DeflateCompressor"/> at <see cref="DeflateCompressionLevel.Maximum"/>,
/// which runs the iterative optimal-parsing/block-splitting search implemented in
/// <see cref="ZopfliDeflate"/>. The output is standard RFC 1951 DEFLATE — smaller
/// than the regular greedy/lazy DEFLATE building block on typical inputs, but
/// decodable by any conforming DEFLATE reader, including <see cref="DeflateDecompressor"/>.
/// </summary>
public sealed class ZopfliBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Zopfli";
  /// <inheritdoc/>
  public string DisplayName => "Zopfli";
  /// <inheritdoc/>
  public string Description => "Exhaustive iterative-optimal DEFLATE encoder producing smaller, fully RFC 1951-compatible output";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data)
    => DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DeflateDecompressor.Decompress(data);
}

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
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Zopfli";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Zopfli";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Exhaustive iterative-optimal DEFLATE encoder producing smaller, fully RFC 1951-compatible output";
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
    => DeflateCompressor.Compress(data, DeflateCompressionLevel.Maximum);

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data)
    => DeflateDecompressor.Decompress(data);
}

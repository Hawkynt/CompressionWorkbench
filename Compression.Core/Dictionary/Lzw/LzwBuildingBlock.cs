using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzw;

/// <summary>
/// Exposes the LZW algorithm as a benchmarkable building block.
/// </summary>
public sealed class LzwBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "BB_Lzw";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "LZW";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the description.
  /// </summary>
  public string Description => "Lempel-Ziv-Welch dictionary coding, used in GIF and Unix compress";
  /// <inheritdoc/>
  /// <summary>
  /// Gets the family.
  /// </summary>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  /// <summary>
  /// Encodes the supplied input.
  /// </summary>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    var encoder = new LzwEncoder(output, minBits: 9, maxBits: 16,
      useClearCode: true, useStopCode: true, bitOrder: BitOrder.LsbFirst);
    encoder.Encode(data);
    return output.ToArray();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    var decoder = new LzwDecoder(input, minBits: 9, maxBits: 16,
      useClearCode: true, useStopCode: true, bitOrder: BitOrder.LsbFirst);
    return decoder.Decode();
  }
}

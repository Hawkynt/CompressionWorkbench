using Compression.Core.BitIO;
using Compression.Registry;

namespace Compression.Core.Dictionary.Lzw;

/// <summary>
/// Exposes the LZW algorithm as a benchmarkable building block.
/// </summary>
public sealed class LzwBuildingBlock : IBuildingBlock {
  /// <inheritdoc/>
  public string Id => "BB_Lzw";
  /// <inheritdoc/>
  public string DisplayName => "LZW";
  /// <inheritdoc/>
  public string Description => "Lempel-Ziv-Welch dictionary coding, used in GIF and Unix compress";
  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Dictionary;

  /// <inheritdoc/>
  public byte[] Compress(ReadOnlySpan<byte> data) {
    using var output = new MemoryStream();
    var encoder = new LzwEncoder(output, minBits: 9, maxBits: 16,
      useClearCode: true, useStopCode: true, bitOrder: BitOrder.LsbFirst);
    encoder.Encode(data);
    return output.ToArray();
  }

  /// <inheritdoc/>
  public byte[] Decompress(ReadOnlySpan<byte> data) {
    using var input = new MemoryStream(data.ToArray());
    var decoder = new LzwDecoder(input, minBits: 9, maxBits: 16,
      useClearCode: true, useStopCode: true, bitOrder: BitOrder.LsbFirst);
    return decoder.Decode();
  }
}

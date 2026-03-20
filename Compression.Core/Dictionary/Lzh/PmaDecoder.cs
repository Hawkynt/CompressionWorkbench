using Compression.Core.Entropy.Ppmd;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes data compressed with the PMA (Prediction by Matching of Algorithms) method
/// used in LHA archives with methods -pm1- and -pm2-.
/// PMA uses PPMd (Prediction by Partial Matching) context modeling with arithmetic coding.
/// </summary>
public static class PmaDecoder {
  /// <summary>
  /// Decodes PMA-compressed data.
  /// </summary>
  /// <param name="compressed">The compressed data.</param>
  /// <param name="originalSize">The expected decompressed size.</param>
  /// <param name="order">Context order: 2 for -pm1-, 3 for -pm2-.</param>
  /// <returns>The decompressed data.</returns>
  public static byte[] Decode(byte[] compressed, int originalSize, int order) {
    if (originalSize == 0)
      return [];

    using var ms = new MemoryStream(compressed);
    var decoder = new PpmdRangeDecoder(ms);
    var model = new PpmdModelH(order);

    var output = new byte[originalSize];
    for (int i = 0; i < originalSize; ++i)
      output[i] = model.DecodeSymbol(decoder);

    return output;
  }
}

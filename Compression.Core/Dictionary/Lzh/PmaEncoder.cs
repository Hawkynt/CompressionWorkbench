using Compression.Core.Entropy.Ppmd;

namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Encodes data using the PMA (Prediction by Matching of Algorithms) method
/// used in LHA archives with methods -pm1- and -pm2-.
/// PMA uses PPMd (Prediction by Partial Matching) context modeling with arithmetic coding.
/// </summary>
public static class PmaEncoder {
  /// <summary>
  /// Encodes data using PMA compression.
  /// </summary>
  /// <param name="data">The data to compress.</param>
  /// <param name="order">Context order: 2 for -pm1-, 3 for -pm2-.</param>
  /// <returns>The compressed data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data, int order) {
    if (data.Length == 0)
      return [];

    using var ms = new MemoryStream();
    var encoder = new PpmdRangeEncoder(ms);
    var model = new PpmdModelH(order);

    for (var i = 0; i < data.Length; ++i)
      model.EncodeSymbol(encoder, data[i]);

    encoder.Finish();
    return ms.ToArray();
  }
}

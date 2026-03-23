using Compression.Core.Entropy.Arithmetic;

namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// SQX multimedia compressor/decompressor using delta coders (E0-E4) and arithmetic coding.
/// </summary>
/// <remarks>
/// Delta orders: E0=raw, E1=1st order (dx), E2=2nd order (d²x), E3=3rd, E4=4th.
/// The best delta order is selected automatically by trying all orders on a small prefix.
/// Residuals are then encoded using an adaptive 256-symbol arithmetic coder.
/// Format: [1 byte deltaOrder] [arithmetic-coded residuals]
/// </remarks>
public static class SqxMultimediaCodec {
  /// <summary>
  /// Compresses multimedia data using automatic delta order selection + arithmetic coding.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var bestOrder = SelectDeltaOrder(data);
    var residuals = ApplyDelta(data, bestOrder);

    using var ms = new MemoryStream();
    ms.WriteByte((byte)bestOrder);

    var encoder = new ArithmeticEncoder(ms);
    var model = new AdaptiveModel(256);

    for (var i = 0; i < residuals.Length; ++i) {
      int sym = residuals[i];
      var cumFreq = (uint)model.GetCumulativeFrequency(sym);
      var symFreq = (uint)model.GetFrequency(sym);
      var totalFreq = (uint)model.TotalFrequency;

      encoder.EncodeSymbol(cumFreq, symFreq, totalFreq);
      model.Update(sym);
    }

    encoder.Finish();
    return ms.ToArray();
  }

  /// <summary>
  /// Decompresses multimedia data.
  /// </summary>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    if (compressed.Length == 0 || originalSize == 0)
      return new byte[originalSize];

    using var ms = new MemoryStream(compressed);
    var deltaOrder = ms.ReadByte();

    var decoder = new ArithmeticDecoder(ms);
    var model = new AdaptiveModel(256);

    var residuals = new byte[originalSize];
    for (var i = 0; i < originalSize; ++i) {
      var totalFreq = (uint)model.TotalFrequency;
      var count = decoder.GetCumulativeCount(totalFreq);
      var sym = model.FindSymbol((int)count);

      var cumFreq = (uint)model.GetCumulativeFrequency(sym);
      var symFreq = (uint)model.GetFrequency(sym);
      decoder.UpdateSymbol(cumFreq, symFreq, totalFreq);

      residuals[i] = (byte)sym;
      model.Update(sym);
    }

    return InverseDelta(residuals, deltaOrder);
  }

  private static int SelectDeltaOrder(ReadOnlySpan<byte> data) {
    var testLen = Math.Min(data.Length, 1024);
    var bestScore = long.MaxValue;
    var bestOrder = 0;

    for (var order = 0; order <= 4; ++order) {
      var residuals = ApplyDelta(data[..testLen], order);
      var score = EstimateEntropyScore(residuals);
      if (score < bestScore) {
        bestScore = score;
        bestOrder = order;
      }
    }

    return bestOrder;
  }

  private static long EstimateEntropyScore(byte[] data) {
    // Use sum of absolute deviations from mean as proxy for entropy
    long sum = 0;
    foreach (var b in data) sum += b;
    var mean = sum / Math.Max(data.Length, 1);
    long dev = 0;
    foreach (var b in data) dev += Math.Abs(b - mean);
    return dev;
  }

  private static byte[] ApplyDelta(ReadOnlySpan<byte> data, int order) {
    var result = data.ToArray();
    for (var pass = 0; pass < order; ++pass) {
      byte prev = 0;
      for (var i = 0; i < result.Length; ++i) {
        var cur = result[i];
        result[i] = (byte)(cur - prev);
        prev = cur;
      }
    }
    return result;
  }

  private static byte[] InverseDelta(byte[] residuals, int order) {
    var result = (byte[])residuals.Clone();
    for (var pass = 0; pass < order; ++pass) {
      byte prev = 0;
      for (var i = 0; i < result.Length; ++i) {
        result[i] = (byte)(result[i] + prev);
        prev = result[i];
      }
    }
    return result;
  }
}

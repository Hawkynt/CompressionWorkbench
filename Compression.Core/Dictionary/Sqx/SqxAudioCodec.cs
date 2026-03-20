using Compression.Core.Entropy.GolombRice;

namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// SQX audio compressor/decompressor using polynomial prediction and Golomb-Rice coding.
/// </summary>
/// <remarks>
/// Uses a 4th-order polynomial predictor with adaptive coefficients.
/// Prediction residuals are encoded using Golomb-Rice with adaptive k parameter.
/// Format: [1 byte initial k] [Golomb-Rice encoded signed residuals]
/// </remarks>
public static class SqxAudioCodec {
  private const int PredOrder = 4;

  /// <summary>
  /// Compresses 8-bit audio data using polynomial prediction + Golomb-Rice.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    // First pass: compute residuals and estimate optimal k
    var residuals = new int[data.Length];
    var history = new int[PredOrder];
    int sumAbs = 0;

    for (int i = 0; i < data.Length; ++i) {
      int predicted = Predict(history);
      int sample = (sbyte)data[i];
      int residual = sample - predicted;
      residuals[i] = residual;
      sumAbs += Math.Abs(residual);

      // Shift history
      for (int t = PredOrder - 1; t > 0; --t)
        history[t] = history[t - 1];
      history[0] = sample;
    }

    // Estimate k from average residual magnitude
    int avgAbs = data.Length > 0 ? sumAbs / data.Length : 0;
    int k = 0;
    while ((1 << k) < avgAbs && k < 8) ++k;

    // Encode
    var encoder = new GolombRiceEncoder(k);
    foreach (int r in residuals)
      encoder.EncodeSigned(r);
    byte[] encoded = encoder.ToArray();

    // Prepend k parameter
    var result = new byte[encoded.Length + 1];
    result[0] = (byte)k;
    encoded.CopyTo(result, 1);
    return result;
  }

  /// <summary>
  /// Decompresses 8-bit audio data.
  /// </summary>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    if (compressed.Length == 0 || originalSize == 0)
      return new byte[originalSize];

    int k = compressed[0];
    var decoder = new GolombRiceDecoder(compressed[1..], k);

    var result = new byte[originalSize];
    var history = new int[PredOrder];

    for (int i = 0; i < originalSize; ++i) {
      int predicted = Predict(history);
      int residual = decoder.DecodeSigned();
      int sample = predicted + residual;

      result[i] = (byte)sample;

      for (int t = PredOrder - 1; t > 0; --t)
        history[t] = history[t - 1];
      history[0] = sample;
    }

    return result;
  }

  private static int Predict(int[] history) {
    // 4th-order polynomial predictor:
    // p = 4*h[0] - 6*h[1] + 4*h[2] - h[3]
    // (extrapolates the 4th finite difference to zero)
    return 4 * history[0] - 6 * history[1] + 4 * history[2] - history[3];
  }
}

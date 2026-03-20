namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// ACE 2.0 SOUND sub-mode filter. Decorrelates audio data
/// using adaptive linear prediction with LMS weight updates.
/// </summary>
/// <remarks>
/// Size-preserving: input and output have the same length.
/// Uses a 3-tap adaptive predictor per channel.
/// All arithmetic is done in unsigned byte domain (mod 256).
/// </remarks>
public static class AceSoundFilter {
  private const int NumTaps = 3;

  /// <summary>
  /// Forward transform: converts audio samples to prediction residuals.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<byte> data, int channels = 1) {
    channels = Math.Clamp(channels, 1, 4);
    var result = new byte[data.Length];
    if (data.Length == 0) return result;

    var weights = new int[channels * NumTaps];
    var history = new int[channels * NumTaps];

    for (int i = 0; i < data.Length; ++i) {
      int ch = i % channels;
      int whBase = ch * NumTaps;

      int predicted = 0;
      for (int t = 0; t < NumTaps; ++t)
        predicted += weights[whBase + t] * history[whBase + t];
      predicted >>= 4;

      int sample = data[i];
      int residual = (sample - predicted) & 0xFF;
      result[i] = (byte)residual;

      // LMS weight update using signed error
      int error = (sbyte)(byte)residual;
      for (int t = 0; t < NumTaps; ++t) {
        if (history[whBase + t] > 128)
          weights[whBase + t] += error > 0 ? 1 : (error < 0 ? -1 : 0);
        else if (history[whBase + t] < 128)
          weights[whBase + t] += error > 0 ? -1 : (error < 0 ? 1 : 0);
      }

      for (int t = NumTaps - 1; t > 0; --t)
        history[whBase + t] = history[whBase + t - 1];
      history[whBase] = sample;
    }

    return result;
  }

  /// <summary>
  /// Inverse transform: reconstructs audio samples from prediction residuals.
  /// </summary>
  public static byte[] Decode(ReadOnlySpan<byte> data, int channels = 1) {
    channels = Math.Clamp(channels, 1, 4);
    var result = new byte[data.Length];
    if (data.Length == 0) return result;

    var weights = new int[channels * NumTaps];
    var history = new int[channels * NumTaps];

    for (int i = 0; i < data.Length; ++i) {
      int ch = i % channels;
      int whBase = ch * NumTaps;

      int predicted = 0;
      for (int t = 0; t < NumTaps; ++t)
        predicted += weights[whBase + t] * history[whBase + t];
      predicted >>= 4;

      int residual = data[i];
      int sample = (residual + predicted) & 0xFF;
      result[i] = (byte)sample;

      // LMS weight update (must match encoder exactly)
      int error = (sbyte)(byte)residual;
      for (int t = 0; t < NumTaps; ++t) {
        if (history[whBase + t] > 128)
          weights[whBase + t] += error > 0 ? 1 : (error < 0 ? -1 : 0);
        else if (history[whBase + t] < 128)
          weights[whBase + t] += error > 0 ? -1 : (error < 0 ? 1 : 0);
      }

      for (int t = NumTaps - 1; t > 0; --t)
        history[whBase + t] = history[whBase + t - 1];
      history[whBase] = sample;
    }

    return result;
  }
}

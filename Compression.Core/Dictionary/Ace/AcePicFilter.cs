namespace Compression.Core.Dictionary.Ace;

/// <summary>
/// ACE 2.0 PIC sub-mode filter. Decorrelates image data using Paeth-style
/// prediction based on left and above neighbor bytes.
/// </summary>
/// <remarks>
/// Size-preserving: input and output have the same length.
/// Treats data as a 2D grid with configurable stride (row width in bytes).
/// Each byte is predicted from its left, above, and above-left neighbors.
/// </remarks>
public static class AcePicFilter {
  /// <summary>
  /// Forward transform: converts pixels to prediction residuals.
  /// </summary>
  /// <param name="data">Raw pixel data (row-major).</param>
  /// <param name="stride">Row width in bytes. If 0, uses sqrt(length) heuristic.</param>
  /// <returns>Transformed residual data (same length as input).</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data, int stride = 0) {
    if (data.Length == 0) return [];
    if (stride <= 0) stride = EstimateStride(data.Length);

    var result = new byte[data.Length];
    for (var i = 0; i < data.Length; ++i) {
      var col = i % stride;
      var a = col > 0 ? data[i - 1] : 0;
      var b = i >= stride ? data[i - stride] : 0;
      var c = (col > 0 && i >= stride) ? data[i - stride - 1] : 0;
      var predicted = PaethPredictor(a, b, c);
      result[i] = (byte)(data[i] - predicted);
    }
    return result;
  }

  /// <summary>
  /// Inverse transform: reconstructs pixels from prediction residuals.
  /// </summary>
  /// <param name="data">Residual data.</param>
  /// <param name="stride">Row width in bytes. If 0, uses sqrt(length) heuristic.</param>
  /// <returns>Reconstructed pixel data (same length as input).</returns>
  public static byte[] Decode(ReadOnlySpan<byte> data, int stride = 0) {
    if (data.Length == 0) return [];
    if (stride <= 0) stride = EstimateStride(data.Length);

    var result = new byte[data.Length];
    for (var i = 0; i < data.Length; ++i) {
      var col = i % stride;
      var a = col > 0 ? result[i - 1] : 0;
      var b = i >= stride ? result[i - stride] : 0;
      var c = (col > 0 && i >= stride) ? result[i - stride - 1] : 0;
      var predicted = PaethPredictor(a, b, c);
      result[i] = (byte)(data[i] + predicted);
    }
    return result;
  }

  private static int PaethPredictor(int a, int b, int c) {
    var p = a + b - c;
    var pa = Math.Abs(p - a);
    var pb = Math.Abs(p - b);
    var pc = Math.Abs(p - c);
    if (pa <= pb && pa <= pc) return a;
    if (pb <= pc) return b;
    return c;
  }

  private static int EstimateStride(int length) {
    var sqrtApprox = (int)Math.Sqrt(length);
    return Math.Max(sqrtApprox, 1);
  }
}

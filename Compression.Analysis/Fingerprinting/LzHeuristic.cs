namespace Compression.Analysis.Fingerprinting;

/// <summary>
/// Detects LZ-family (LZSS/LZ77) compressed data by looking for flag-byte patterns
/// where every Nth byte acts as a flag byte controlling literal vs. match interpretation.
/// </summary>
public sealed class LzHeuristic : IHeuristic {

  /// <inheritdoc />
  public FingerprintResult? Analyze(ReadOnlySpan<byte> data) {
    if (data.Length < 18) return null;

    // LZSS typically uses a flag byte every 9 bytes (1 flag + 8 items)
    // Check if bytes at positions 0, 9, 18, ... have flag-byte characteristics
    var bestScore = 0.0;
    var bestStride = 0;

    // Try common strides: 9 (8 items), 17 (16 items)
    foreach (var stride in new[] { 9, 17 }) {
      var flagPositions = 0;
      var totalPositions = 0;
      for (var i = 0; i + stride < data.Length; i += stride) {
        totalPositions++;
        var flag = data[i];
        // Flag bytes tend to have mixed bits (some 0, some 1)
        var bits = CountBits(flag);
        if (bits is >= 2 and <= 6) flagPositions++;
      }

      if (totalPositions < 3) continue;
      var score = (double)flagPositions / totalPositions;
      if (score > bestScore) {
        bestScore = score;
        bestStride = stride;
      }
    }

    if (bestScore < 0.5 || bestStride == 0) return null;

    var confidence = Math.Min(0.7, 0.3 + bestScore * 0.4);
    return new("LZ/LZSS", confidence, $"Flag-byte pattern at stride {bestStride}: {bestScore:P0} match rate");
  }

  private static int CountBits(byte b) {
    var count = 0;
    while (b != 0) { count += b & 1; b >>= 1; }
    return count;
  }
}

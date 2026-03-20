namespace Compression.Lib;

/// <summary>
/// Detects incompressible (already compressed, encrypted, or random) data using
/// a chi-square goodness-of-fit test on byte frequency distribution.
/// </summary>
internal static class EntropyDetector {

  /// <summary>Maximum sample size for large files (256 KB). Sampling head + tail.</summary>
  private const int MaxSampleSize = 256 * 1024;

  /// <summary>
  /// Chi-square threshold for 255 degrees of freedom at p=0.01.
  /// When the chi-square statistic is below this value, the byte distribution is
  /// uniform enough to be considered incompressible (compressed/encrypted/random).
  /// A truly uniform distribution yields chi-square ≈ 0; compressible text yields
  /// values well above this threshold.
  /// </summary>
  private const double ChiSquareThreshold = 310.0;

  /// <summary>
  /// Returns true if the data appears incompressible (compressed, encrypted, or random).
  /// Uses a chi-square test: uniform byte distribution → incompressible.
  /// </summary>
  internal static bool IsIncompressible(byte[] data) {
    if (data.Length < 64) return false; // too small to tell

    var sample = GetSample(data);
    double chiSquare = ComputeChiSquare(sample);

    // Low chi-square = uniform distribution = incompressible
    return chiSquare < ChiSquareThreshold;
  }

  /// <summary>
  /// Returns true if the file at the given path appears incompressible.
  /// Reads only a sample from the file for efficiency.
  /// </summary>
  internal static bool IsIncompressible(string filePath) {
    var fi = new FileInfo(filePath);
    if (fi.Length < 64) return false;

    byte[] sample;
    if (fi.Length <= MaxSampleSize) {
      sample = File.ReadAllBytes(filePath);
    }
    else {
      // Sample head + tail for better coverage
      sample = new byte[MaxSampleSize];
      using var fs = File.OpenRead(filePath);
      int headSize = MaxSampleSize / 2;
      int tailSize = MaxSampleSize - headSize;
      fs.ReadExactly(sample, 0, headSize);
      fs.Seek(-tailSize, SeekOrigin.End);
      fs.ReadExactly(sample, headSize, tailSize);
    }

    double chiSquare = ComputeChiSquare(sample);
    return chiSquare < ChiSquareThreshold;
  }

  private static ReadOnlySpan<byte> GetSample(byte[] data) {
    if (data.Length <= MaxSampleSize) return data;
    // For in-memory data, just use the first MaxSampleSize bytes
    return data.AsSpan(0, MaxSampleSize);
  }

  /// <summary>
  /// Computes the chi-square statistic for byte frequency distribution.
  /// Expected frequency = N/256 for each byte value (uniform distribution).
  /// </summary>
  private static double ComputeChiSquare(ReadOnlySpan<byte> data) {
    Span<int> counts = stackalloc int[256];
    counts.Clear();

    for (int i = 0; i < data.Length; i++)
      counts[data[i]]++;

    double expected = data.Length / 256.0;
    double chiSquare = 0;
    for (int i = 0; i < 256; i++) {
      double diff = counts[i] - expected;
      chiSquare += diff * diff / expected;
    }

    return chiSquare;
  }
}

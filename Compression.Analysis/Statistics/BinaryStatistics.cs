namespace Compression.Analysis.Statistics;

/// <summary>
/// Provides statistical analysis of binary data: entropy, chi-square, serial correlation,
/// Monte Carlo pi estimation, byte frequency, and n-gram diversity.
/// </summary>
public static class BinaryStatistics {

  /// <summary>Result of a full statistical analysis.</summary>
  public sealed record StatisticsResult(
    double Entropy,
    double Mean,
    double ChiSquare,
    double PValue,
    double SerialCorrelation,
    double MonteCarloPi,
    int UniqueBytesCount,
    int MostCommonByte,
    long MostCommonCount,
    int LeastCommonByte,
    long LeastCommonCount,
    long[] ByteFrequency,
    int UniqueBigrams,
    int UniqueTrigrams,
    int UniqueQuadgrams
  );

  /// <summary>Runs all statistical analyses on the data.</summary>
  public static StatisticsResult Analyze(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return new(0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, new long[256], 0, 0, 0);

    var freq = ComputeByteFrequency(data);
    var entropy = ComputeEntropy(freq, data.Length);
    var mean = ComputeMean(data);
    var chiSq = ComputeChiSquare(freq, data.Length);
    var pValue = ChiSquarePValue(chiSq, 255);
    var serialCorr = ComputeSerialCorrelation(data);
    var piEst = ComputeMonteCarloPi(data);

    var unique = 0;
    var mostIdx = 0;
    var leastIdx = -1;
    for (var i = 0; i < 256; i++) {
      if (freq[i] > 0) unique++;
      if (freq[i] > freq[mostIdx]) mostIdx = i;
      if (freq[i] > 0 && (leastIdx < 0 || freq[i] < freq[leastIdx])) leastIdx = i;
    }
    if (leastIdx < 0) leastIdx = 0;

    var (bigrams, trigrams, quadgrams) = CountUniqueNgrams(data);

    return new(
      entropy, mean, chiSq, pValue, serialCorr, piEst,
      unique, mostIdx, freq[mostIdx], leastIdx, freq[leastIdx],
      freq, bigrams, trigrams, quadgrams
    );
  }

  /// <summary>Computes Shannon entropy in bits per byte (0-8).</summary>
  public static double ComputeEntropy(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return 0;
    var freq = ComputeByteFrequency(data);
    return ComputeEntropy(freq, data.Length);
  }

  /// <summary>Computes Shannon entropy from pre-computed frequency table.</summary>
  public static double ComputeEntropy(long[] freq, int total) {
    if (total == 0) return 0;
    var entropy = 0.0;
    foreach (var f in freq) {
      if (f <= 0) continue;
      var p = (double)f / total;
      entropy -= p * Math.Log2(p);
    }
    return entropy;
  }

  /// <summary>Computes chi-square statistic for byte distribution uniformity.</summary>
  public static double ComputeChiSquare(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return 0;
    var freq = ComputeByteFrequency(data);
    return ComputeChiSquare(freq, data.Length);
  }

  /// <summary>Computes chi-square from pre-computed frequency table.</summary>
  public static double ComputeChiSquare(long[] freq, int total) {
    if (total == 0) return 0;
    var expected = (double)total / 256.0;
    var chiSq = 0.0;
    foreach (var f in freq) {
      var diff = f - expected;
      chiSq += diff * diff / expected;
    }
    return chiSq;
  }

  /// <summary>Computes approximate p-value for chi-square with given degrees of freedom using Wilson-Hilferty.</summary>
  public static double ChiSquarePValue(double chiSquare, double degreesOfFreedom) {
    var k = degreesOfFreedom;
    var z = Math.Pow(chiSquare / k, 1.0 / 3.0) - (1.0 - 2.0 / (9.0 * k));
    z /= Math.Sqrt(2.0 / (9.0 * k));
    return 0.5 * Erfc(z / Math.Sqrt(2.0));
  }

  /// <summary>Computes serial correlation coefficient between adjacent bytes.</summary>
  public static double ComputeSerialCorrelation(ReadOnlySpan<byte> data) {
    if (data.Length < 2) return 0;
    var n = data.Length;
    var mean = ComputeMean(data);

    double num = 0, denA = 0, denB = 0;
    for (var i = 0; i < n - 1; i++) {
      var a = data[i] - mean;
      var b = data[i + 1] - mean;
      num += a * b;
      denA += a * a;
      denB += b * b;
    }
    {
      var a = data[n - 1] - mean;
      var b = data[0] - mean;
      num += a * b;
      denA += a * a;
      denB += b * b;
    }
    var denom = Math.Sqrt(denA * denB);
    return denom > 0 ? num / denom : 0;
  }

  /// <summary>Estimates pi using Monte Carlo method on byte pairs.</summary>
  public static double ComputeMonteCarloPi(ReadOnlySpan<byte> data) {
    if (data.Length < 6) return 0;
    long inside = 0;
    var pairs = data.Length / 2;
    for (var i = 0; i + 1 < data.Length; i += 2) {
      var x = data[i] - 127.5;
      var y = data[i + 1] - 127.5;
      if (x * x + y * y <= 127.5 * 127.5) inside++;
    }
    return 4.0 * inside / pairs;
  }

  /// <summary>Computes the mean byte value.</summary>
  public static double ComputeMean(ReadOnlySpan<byte> data) {
    if (data.Length == 0) return 0;
    long sum = 0;
    foreach (var b in data) sum += b;
    return (double)sum / data.Length;
  }

  /// <summary>Computes byte value frequency distribution.</summary>
  public static long[] ComputeByteFrequency(ReadOnlySpan<byte> data) {
    var freq = new long[256];
    foreach (var b in data) freq[b]++;
    return freq;
  }

  /// <summary>Counts unique n-grams (2, 3, 4-byte sequences). Samples up to 256KB.</summary>
  public static (int Bigrams, int Trigrams, int Quadgrams) CountUniqueNgrams(ReadOnlySpan<byte> data) {
    var sampleLen = Math.Min(data.Length, 256 * 1024);
    var bigrams = new HashSet<int>();
    var trigrams = new HashSet<long>();
    var quadgrams = new HashSet<long>();
    for (var i = 0; i < sampleLen - 1; i++) {
      bigrams.Add(data[i] << 8 | data[i + 1]);
      if (i < sampleLen - 2)
        trigrams.Add((long)data[i] << 16 | (long)data[i + 1] << 8 | data[i + 2]);
      if (i < sampleLen - 3)
        quadgrams.Add((long)data[i] << 24 | (long)data[i + 1] << 16 | (long)data[i + 2] << 8 | data[i + 3]);
    }
    return (bigrams.Count, trigrams.Count, quadgrams.Count);
  }

  /// <summary>Computes Kullback-Leibler divergence D(P||Q) between two frequency distributions.</summary>
  public static double KullbackLeiblerDivergence(long[] freqP, int totalP, long[] freqQ, int totalQ)
    => BoundaryDetector.KullbackLeiblerDivergence(freqP, totalP, freqQ, totalQ);

  /// <summary>Computes two-sample chi-square statistic between two byte distributions.</summary>
  public static double TwoSampleChiSquare(ReadOnlySpan<byte> sample1, ReadOnlySpan<byte> sample2)
    => BoundaryDetector.TwoSampleChiSquare(sample1, sample2);

  /// <summary>Complementary error function (Horner approximation).</summary>
  internal static double Erfc(double x) {
    if (x < 0) return 2.0 - Erfc(-x);
    const double a1 = 0.254829592, a2 = -0.284496736, a3 = 1.421413741, a4 = -1.453152027, a5 = 1.061405429;
    var t = 1.0 / (1.0 + 0.3275911 * x);
    return t * (a1 + t * (a2 + t * (a3 + t * (a4 + t * a5)))) * Math.Exp(-x * x);
  }
}

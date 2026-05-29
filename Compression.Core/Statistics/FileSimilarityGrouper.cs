#pragma warning disable CS1591

namespace Compression.Core.Statistics;

/// <summary>
/// A statistical fingerprint of file contents: bigram histogram, entropy, and chi-square score.
/// Two files with similar fingerprints are likely to compress well together in a solid block.
/// </summary>
/// <param name="Entropy">Shannon entropy in bits (0-8).</param>
/// <param name="ChiSquare">Chi-square uniformity score (higher = more structured/compressible).</param>
/// <param name="BigramHistogram">256-element XOR-folded bigram frequency vector (sums to ~1.0).</param>
/// <param name="SampleSize">Number of bytes actually sampled.</param>
public sealed record FileFingerprint(
  double Entropy,
  double ChiSquare,
  double[] BigramHistogram,
  int SampleSize
);

/// <summary>
/// Computes statistical fingerprints of file contents and groups statistically-similar
/// files together for better solid-block compression.
/// </summary>
public static class FileSimilarityGrouper {

  /// <summary>Maximum bytes to sample from a file (64 KB). Larger files are truncated.</summary>
  private const int MaxSampleSize = 64 * 1024;

  /// <summary>Distance weight for bigram histogram Pearson correlation.</summary>
  private const double W1 = 0.6;

  /// <summary>Distance weight for entropy difference.</summary>
  private const double W2 = 0.2;

  /// <summary>Distance weight for chi-square divergence.</summary>
  private const double W3 = 0.2;

  /// <summary>Maximum merge distance for agglomerative clustering.</summary>
  private const double MergeThreshold = 0.5;

  /// <summary>Number of XOR-folded bigram histogram buckets.</summary>
  private const int HistogramBuckets = 256;

  /// <summary>
  /// Computes a statistical fingerprint for a byte array: bigram histogram,
  /// entropy, chi-square uniformity score. Two files with similar fingerprints
  /// are likely to compress well together in a solid block.
  /// </summary>
  public static FileFingerprint ComputeFingerprint(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);

    if (data.Length == 0)
      return new(0.0, 0.0, new double[HistogramBuckets], 0);

    var sample = data.Length <= MaxSampleSize ? data : data.AsSpan(0, MaxSampleSize);
    var sampleSize = sample.Length;

    var entropy = _ComputeEntropy(sample);
    var chiSquare = _ComputeChiSquare(sample);
    var bigramHistogram = _ComputeBigramHistogram(sample);

    return new(entropy, chiSquare, bigramHistogram, sampleSize);
  }

  /// <summary>
  /// Measures similarity between two fingerprints (0.0 = identical, 1.0 = maximally different).
  /// Uses Pearson correlation of bigram histograms + entropy distance + chi-square divergence.
  /// </summary>
  public static double Distance(FileFingerprint a, FileFingerprint b) {
    ArgumentNullException.ThrowIfNull(a);
    ArgumentNullException.ThrowIfNull(b);

    // Pearson correlation component: 1 - r maps [−1,1] to [0,2]; clamp to [0,1]
    var pearson = _PearsonCorrelation(a.BigramHistogram, b.BigramHistogram);
    var bigramDistance = Math.Clamp(1.0 - pearson, 0.0, 1.0);

    // Entropy distance: |a-b| / 8, range [0,1]
    var entropyDistance = Math.Abs(a.Entropy - b.Entropy) / 8.0;

    // Chi-square divergence: |a-b| / max(a,b), range [0,1]
    var maxChi = Math.Max(a.ChiSquare, b.ChiSquare);
    var chiDistance = maxChi > 0 ? Math.Abs(a.ChiSquare - b.ChiSquare) / maxChi : 0.0;

    return W1 * bigramDistance + W2 * entropyDistance + W3 * chiDistance;
  }

  /// <summary>
  /// Groups files into clusters of statistically-similar content.
  /// Uses greedy agglomerative clustering: start with each file as its own cluster,
  /// repeatedly merge the two most-similar clusters until we reach the target count
  /// or max-cluster-size is hit.
  /// </summary>
  /// <param name="files">The file contents to group.</param>
  /// <param name="maxGroups">Target maximum number of groups.</param>
  /// <param name="maxGroupSize">Maximum total byte size of a single group.</param>
  /// <returns>List of groups, each containing the indices into <paramref name="files"/>.</returns>
  public static List<List<int>> GroupBySimilarity(
    IReadOnlyList<byte[]> files, int maxGroups, long maxGroupSize
  ) {
    ArgumentNullException.ThrowIfNull(files);
    if (maxGroups < 1)
      throw new ArgumentOutOfRangeException(nameof(maxGroups), "Must be >= 1.");
    if (maxGroupSize < 1)
      throw new ArgumentOutOfRangeException(nameof(maxGroupSize), "Must be >= 1.");

    if (files.Count == 0)
      return [];

    // Compute fingerprints and sizes
    var fingerprints = new FileFingerprint[files.Count];
    var sizes = new long[files.Count];
    for (var i = 0; i < files.Count; i++) {
      fingerprints[i] = ComputeFingerprint(files[i]);
      sizes[i] = files[i].Length;
    }

    // Initialize clusters: each file is its own cluster
    var clusters = new List<_Cluster>(files.Count);
    for (var i = 0; i < files.Count; i++)
      clusters.Add(new([i], fingerprints[i], sizes[i]));

    // Agglomerative clustering: repeatedly merge closest pair
    while (clusters.Count > maxGroups) {
      var bestI = -1;
      var bestJ = -1;
      var bestDist = double.MaxValue;

      for (var i = 0; i < clusters.Count; i++) {
        for (var j = i + 1; j < clusters.Count; j++) {
          // Skip if merge would exceed max group size
          if (clusters[i].TotalSize + clusters[j].TotalSize > maxGroupSize)
            continue;

          var dist = Distance(clusters[i].Fingerprint, clusters[j].Fingerprint);
          if (dist < bestDist) {
            bestDist = dist;
            bestI = i;
            bestJ = j;
          }
        }
      }

      // Stop if no valid merge found or best distance exceeds threshold
      if (bestI < 0 || bestDist > MergeThreshold)
        break;

      // Merge j into i
      var merged = _MergeClusters(clusters[bestI], clusters[bestJ]);
      clusters[bestI] = merged;
      clusters.RemoveAt(bestJ);
    }

    return clusters.Select(c => c.Indices).ToList();
  }

  private static double _ComputeEntropy(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return 0.0;

    Span<int> counts = stackalloc int[256];
    counts.Clear();

    for (var i = 0; i < data.Length; i++)
      counts[data[i]]++;

    var length = (double)data.Length;
    var entropy = 0.0;
    for (var i = 0; i < 256; i++) {
      if (counts[i] == 0)
        continue;
      var p = counts[i] / length;
      entropy -= p * Math.Log2(p);
    }

    return entropy;
  }

  private static double _ComputeChiSquare(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return 0.0;

    Span<int> counts = stackalloc int[256];
    counts.Clear();

    for (var i = 0; i < data.Length; i++)
      counts[data[i]]++;

    var expected = data.Length / 256.0;
    var chiSquare = 0.0;
    for (var i = 0; i < 256; i++) {
      var diff = counts[i] - expected;
      chiSquare += diff * diff / expected;
    }

    return chiSquare;
  }

  private static double[] _ComputeBigramHistogram(ReadOnlySpan<byte> data) {
    var histogram = new double[HistogramBuckets];

    if (data.Length < 2)
      return histogram;

    // Count all 65536 bigrams, XOR-fold into 256 buckets
    // bucket[hi ^ lo] += 1 for each bigram (hi, lo)
    var counts = new int[HistogramBuckets];
    for (var i = 0; i < data.Length - 1; i++) {
      var bucket = data[i] ^ data[i + 1];
      counts[bucket]++;
    }

    // Normalize to frequencies
    var total = (double)(data.Length - 1);
    for (var i = 0; i < HistogramBuckets; i++)
      histogram[i] = counts[i] / total;

    return histogram;
  }

  private static double _PearsonCorrelation(double[] x, double[] y) {
    if (x.Length != y.Length || x.Length == 0)
      return 0.0;

    var n = x.Length;
    var sumX = 0.0;
    var sumY = 0.0;
    for (var i = 0; i < n; i++) {
      sumX += x[i];
      sumY += y[i];
    }

    var meanX = sumX / n;
    var meanY = sumY / n;

    var covXY = 0.0;
    var varX = 0.0;
    var varY = 0.0;
    for (var i = 0; i < n; i++) {
      var dx = x[i] - meanX;
      var dy = y[i] - meanY;
      covXY += dx * dy;
      varX += dx * dx;
      varY += dy * dy;
    }

    var denom = Math.Sqrt(varX * varY);
    return denom < 1e-15 ? 0.0 : covXY / denom;
  }

  private static _Cluster _MergeClusters(_Cluster a, _Cluster b) {
    var indices = new List<int>(a.Indices.Count + b.Indices.Count);
    indices.AddRange(a.Indices);
    indices.AddRange(b.Indices);

    // Mean fingerprint
    var aHist = a.Fingerprint.BigramHistogram;
    var bHist = b.Fingerprint.BigramHistogram;
    var mergedHist = new double[HistogramBuckets];

    var aWeight = (double)a.Indices.Count / indices.Count;
    var bWeight = (double)b.Indices.Count / indices.Count;

    for (var i = 0; i < HistogramBuckets; i++)
      mergedHist[i] = aHist[i] * aWeight + bHist[i] * bWeight;

    var mergedEntropy = a.Fingerprint.Entropy * aWeight + b.Fingerprint.Entropy * bWeight;
    var mergedChi = a.Fingerprint.ChiSquare * aWeight + b.Fingerprint.ChiSquare * bWeight;
    var mergedSample = (int)(a.Fingerprint.SampleSize * aWeight + b.Fingerprint.SampleSize * bWeight);

    var fingerprint = new FileFingerprint(mergedEntropy, mergedChi, mergedHist, mergedSample);
    return new(indices, fingerprint, a.TotalSize + b.TotalSize);
  }

  private sealed record _Cluster(List<int> Indices, FileFingerprint Fingerprint, long TotalSize);
}

namespace Compression.Analysis.Statistics;

/// <summary>
/// Detects boundaries between distinct data regions using CUSUM change-point detection,
/// KL-divergence confirmation, chi-square validation, and edge sharpening.
/// </summary>
public static class BoundaryDetector {

  /// <summary>
  /// Detects region boundaries in binary data using a 5-phase algorithm.
  /// </summary>
  /// <param name="data">The binary data to analyze.</param>
  /// <param name="minRegionSize">Minimum region size in bytes (default 256).</param>
  /// <param name="maxRegions">Maximum number of regions to return (default 500).</param>
  /// <param name="cancellationToken">Optional cancellation token.</param>
  /// <returns>List of region profiles covering the entire data.</returns>
  public static List<RegionProfile> DetectBoundaries(
      ReadOnlySpan<byte> data,
      int minRegionSize = 256,
      int maxRegions = 500,
      CancellationToken cancellationToken = default) {

    if (data.Length == 0) return [];
    if (data.Length < 4096)
      return EntropyMap.Profile(data, Math.Max(64, minRegionSize));

    // Phase 1: Multi-resolution entropy pyramid (256B scale)
    var signal = ComputeEntropySignal(data, 256);
    cancellationToken.ThrowIfCancellationRequested();

    // Phase 2: CUSUM binary segmentation
    var candidates = new List<int>();
    var threshold = 2.0 * Math.Sqrt(signal.Length);
    BinarySegment(signal, 0, signal.Length, threshold, candidates, maxDepth: 20);
    candidates.Sort();
    cancellationToken.ThrowIfCancellationRequested();

    // Phase 3: KL-divergence confirmation
    var confirmed = new List<int>();
    foreach (var candIdx in candidates) {
      var byteOffset = candIdx * 256;
      if (byteOffset < 512 || byteOffset >= data.Length - 512) continue;

      var left = data.Slice(byteOffset - 512, 512);
      var right = data.Slice(byteOffset, Math.Min(512, data.Length - byteOffset));

      var freqL = BinaryStatistics.ComputeByteFrequency(left);
      var freqR = BinaryStatistics.ComputeByteFrequency(right);
      var kl = KullbackLeiblerDivergence(freqL, left.Length, freqR, right.Length);

      if (kl >= 0.1)
        confirmed.Add(byteOffset);
    }
    cancellationToken.ThrowIfCancellationRequested();

    // Phase 4: Chi-square validation
    var validated = new List<int>();
    foreach (var offset in confirmed) {
      var leftStart = Math.Max(0, offset - 512);
      var rightEnd = Math.Min(data.Length, offset + 512);
      var left = data.Slice(leftStart, offset - leftStart);
      var right = data.Slice(offset, rightEnd - offset);

      var chiSq = TwoSampleChiSquare(left, right);
      // df=255, critical value at p=0.05 is ~293. Use chi > 300 as threshold.
      if (chiSq > 300)
        validated.Add(offset);
    }
    cancellationToken.ThrowIfCancellationRequested();

    // Phase 5: Edge sharpening (skip for files > 50MB)
    var sharpened = new List<int>();
    if (data.Length <= 50 * 1024 * 1024) {
      foreach (var offset in validated) {
        var best = SharpenEdge(data, offset);
        sharpened.Add(best);
      }
    }
    else {
      sharpened.AddRange(validated);
    }
    cancellationToken.ThrowIfCancellationRequested();

    // Phase 6: Build region profiles
    return BuildRegions(data, sharpened, minRegionSize, maxRegions);
  }

  // ── Phase 1: Entropy signal ─────────────────────────────────────────

  internal static double[] ComputeEntropySignal(ReadOnlySpan<byte> data, int windowSize) {
    var count = (data.Length + windowSize - 1) / windowSize;
    var signal = new double[count];
    for (var i = 0; i < count; i++) {
      var offset = i * windowSize;
      var len = Math.Min(windowSize, data.Length - offset);
      signal[i] = BinaryStatistics.ComputeEntropy(data.Slice(offset, len));
    }
    return signal;
  }

  // ── Phase 2: CUSUM binary segmentation ──────────────────────────────

  internal static void BinarySegment(double[] signal, int start, int end, double threshold,
      List<int> candidates, int maxDepth) {
    if (end - start < 2 || maxDepth <= 0) return;

    var mean = 0.0;
    for (var i = start; i < end; i++) mean += signal[i];
    mean /= (end - start);

    var cusum = 0.0;
    var maxCusum = 0.0;
    var maxIdx = start;
    for (var i = start; i < end; i++) {
      cusum += signal[i] - mean;
      var abs = Math.Abs(cusum);
      if (abs > maxCusum) {
        maxCusum = abs;
        maxIdx = i;
      }
    }

    if (maxCusum > threshold) {
      candidates.Add(maxIdx);
      BinarySegment(signal, start, maxIdx, threshold, candidates, maxDepth - 1);
      BinarySegment(signal, maxIdx, end, threshold, candidates, maxDepth - 1);
    }
  }

  // ── Phase 3: KL divergence ──────────────────────────────────────────

  /// <summary>Computes KL divergence D(P||Q) between two frequency distributions.</summary>
  public static double KullbackLeiblerDivergence(long[] freqP, int totalP, long[] freqQ, int totalQ) {
    if (totalP == 0 || totalQ == 0) return 0;

    var kl = 0.0;
    for (var i = 0; i < 256; i++) {
      // Add 1 smoothing to avoid log(0)
      var p = (freqP[i] + 1.0) / (totalP + 256.0);
      var q = (freqQ[i] + 1.0) / (totalQ + 256.0);
      kl += p * Math.Log(p / q);
    }
    return kl;
  }

  // ── Phase 4: Two-sample chi-square ──────────────────────────────────

  /// <summary>Computes two-sample chi-square statistic between two byte distributions.</summary>
  public static double TwoSampleChiSquare(ReadOnlySpan<byte> sample1, ReadOnlySpan<byte> sample2) {
    if (sample1.Length == 0 || sample2.Length == 0) return 0;

    var freq1 = BinaryStatistics.ComputeByteFrequency(sample1);
    var freq2 = BinaryStatistics.ComputeByteFrequency(sample2);
    var n1 = (double)sample1.Length;
    var n2 = (double)sample2.Length;

    var chiSq = 0.0;
    for (var i = 0; i < 256; i++) {
      var sum = freq1[i] + freq2[i];
      if (sum == 0) continue;
      var diff = freq1[i] / n1 - freq2[i] / n2;
      chiSq += diff * diff / (sum / (n1 + n2));
    }
    return chiSq * n1 * n2 / (n1 + n2);
  }

  // ── Phase 5: Edge sharpening ────────────────────────────────────────

  private static int SharpenEdge(ReadOnlySpan<byte> data, int roughOffset) {
    var searchStart = Math.Max(0, roughOffset - 128);
    var searchEnd = Math.Min(data.Length, roughOffset + 128);
    const int windowSize = 64;

    var bestGradient = 0.0;
    var bestOffset = roughOffset;

    for (var pos = searchStart; pos < searchEnd - windowSize; pos++) {
      var left = data.Slice(Math.Max(0, pos - windowSize), Math.Min(windowSize, pos));
      var right = data.Slice(pos, Math.Min(windowSize, data.Length - pos));
      if (left.Length < 16 || right.Length < 16) continue;

      var entropyLeft = BinaryStatistics.ComputeEntropy(left);
      var entropyRight = BinaryStatistics.ComputeEntropy(right);
      var gradient = Math.Abs(entropyRight - entropyLeft);

      if (gradient > bestGradient) {
        bestGradient = gradient;
        bestOffset = pos;
      }
    }

    return bestOffset;
  }

  // ── Phase 6: Build regions ──────────────────────────────────────────

  private static List<RegionProfile> BuildRegions(ReadOnlySpan<byte> data,
      List<int> boundaries, int minRegionSize, int maxRegions) {

    // Deduplicate and sort
    var unique = new SortedSet<int>(boundaries);
    unique.Add(0);
    unique.Add(data.Length);

    // Remove boundaries too close together
    var sorted = new List<int>(unique);
    var filtered = new List<int> { 0 };
    for (var i = 1; i < sorted.Count; i++) {
      if (sorted[i] - filtered[^1] >= minRegionSize)
        filtered.Add(sorted[i]);
    }
    if (filtered[^1] != data.Length)
      filtered.Add(data.Length);

    // Cap at maxRegions by merging smallest adjacent pairs
    while (filtered.Count - 1 > maxRegions) {
      var minSize = int.MaxValue;
      var minIdx = 1;
      for (var i = 1; i < filtered.Count - 1; i++) {
        var size = filtered[i + 1] - filtered[i];
        if (size < minSize) {
          minSize = size;
          minIdx = i;
        }
      }
      filtered.RemoveAt(minIdx);
    }

    // Build profiles
    var results = new List<RegionProfile>(filtered.Count - 1);
    for (var i = 0; i < filtered.Count - 1; i++) {
      var offset = filtered[i];
      var length = filtered[i + 1] - offset;
      if (length <= 0) continue;

      var slice = data.Slice(offset, length);
      var freq = BinaryStatistics.ComputeByteFrequency(slice);
      var entropy = BinaryStatistics.ComputeEntropy(freq, length);
      var chiSq = BinaryStatistics.ComputeChiSquare(freq, length);
      var mean = BinaryStatistics.ComputeMean(slice);
      var classification = EntropyMap.Classify(entropy);
      results.Add(new RegionProfile(offset, length, entropy, chiSq, mean, classification));
    }

    return results;
  }
}

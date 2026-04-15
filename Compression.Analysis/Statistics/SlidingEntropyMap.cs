#pragma warning disable CS1591

namespace Compression.Analysis.Statistics;

/// <summary>
/// Classification of a data region based on its entropy characteristics.
/// </summary>
public enum RegionType {
  /// <summary>High entropy (>7.0): compressed, encrypted, or random data.</summary>
  HighEntropy,

  /// <summary>Low entropy (&lt;3.0): headers, padding, structured text, or empty regions.</summary>
  LowEntropy,

  /// <summary>Medium entropy (3.0-7.0): structured binary data, code, or loosely structured content.</summary>
  MediumEntropy,
}

/// <summary>
/// Result of a sliding-window entropy computation.
/// </summary>
/// <param name="Entropy">Entropy values (0-8 bits/byte) at each step position.</param>
/// <param name="Offsets">Byte offsets corresponding to each entropy value.</param>
/// <param name="WindowSize">Window size used for the computation.</param>
/// <param name="StepSize">Step size used for the computation.</param>
public sealed record EntropyMapResult(double[] Entropy, int[] Offsets, int WindowSize, int StepSize);

/// <summary>
/// A contiguous classified region within the entropy map.
/// </summary>
/// <param name="StartOffset">Start byte offset in the original data.</param>
/// <param name="EndOffset">End byte offset (exclusive) in the original data.</param>
/// <param name="Type">Classification of the region.</param>
/// <param name="AverageEntropy">Average entropy across the region.</param>
public sealed record ClassifiedRegion(int StartOffset, int EndOffset, RegionType Type, double AverageEntropy);

/// <summary>
/// Computes a sliding-window entropy map over binary data, providing fine-grained
/// entropy visualization and region classification for reverse engineering unknown formats.
/// </summary>
public static class SlidingEntropyMap {

  /// <summary>
  /// Computes Shannon entropy at regular intervals using a sliding window.
  /// </summary>
  /// <param name="data">Binary data to analyze.</param>
  /// <param name="windowSize">Size of the sliding window in bytes (default 256).</param>
  /// <param name="stepSize">Step size between consecutive windows in bytes (default 64).</param>
  /// <returns>An <see cref="EntropyMapResult"/> with entropy values and corresponding offsets.</returns>
  public static EntropyMapResult ComputeEntropyMap(ReadOnlySpan<byte> data, int windowSize = 256, int stepSize = 64) {
    if (windowSize < 1) windowSize = 1;
    if (stepSize < 1) stepSize = 1;

    if (data.Length == 0)
      return new EntropyMapResult([], [], windowSize, stepSize);

    // If data is smaller than the window, compute a single entropy value
    if (data.Length <= windowSize)
      return new EntropyMapResult(
        [BinaryStatistics.ComputeEntropy(data)],
        [0],
        windowSize,
        stepSize
      );

    var count = (data.Length - windowSize) / stepSize + 1;
    var entropy = new double[count];
    var offsets = new int[count];

    for (var i = 0; i < count; i++) {
      var offset = i * stepSize;
      var window = data.Slice(offset, windowSize);
      entropy[i] = BinaryStatistics.ComputeEntropy(window);
      offsets[i] = offset;
    }

    return new EntropyMapResult(entropy, offsets, windowSize, stepSize);
  }

  /// <summary>
  /// Classifies the entropy map into contiguous regions of high, medium, and low entropy.
  /// Adjacent steps with the same classification are merged into single regions.
  /// </summary>
  /// <param name="map">Entropy map result from <see cref="ComputeEntropyMap"/>.</param>
  /// <returns>List of classified regions covering the data.</returns>
  public static List<ClassifiedRegion> ClassifyRegions(EntropyMapResult map) {
    if (map.Entropy.Length == 0) return [];

    var regions = new List<ClassifiedRegion>();
    var currentType = Classify(map.Entropy[0]);
    var startIdx = 0;

    for (var i = 1; i < map.Entropy.Length; i++) {
      var type = Classify(map.Entropy[i]);
      if (type != currentType) {
        // Emit the current region
        regions.Add(CreateRegion(map, startIdx, i, currentType));
        currentType = type;
        startIdx = i;
      }
    }

    // Emit the final region
    regions.Add(CreateRegion(map, startIdx, map.Entropy.Length, currentType));

    return regions;
  }

  /// <summary>
  /// Classifies an entropy value into a region type.
  /// </summary>
  public static RegionType Classify(double entropy) => entropy switch {
    < 3.0 => RegionType.LowEntropy,
    > 7.0 => RegionType.HighEntropy,
    _ => RegionType.MediumEntropy,
  };

  private static ClassifiedRegion CreateRegion(EntropyMapResult map, int startIdx, int endIdx, RegionType type) {
    var startOffset = map.Offsets[startIdx];

    // End offset: the last window ends at offset + windowSize,
    // but we cap it using the last step's offset + window
    var lastIdx = endIdx - 1;
    var endOffset = map.Offsets[lastIdx] + map.WindowSize;

    // Compute average entropy for the region
    var sum = 0.0;
    for (var i = startIdx; i < endIdx; i++)
      sum += map.Entropy[i];
    var avgEntropy = sum / (endIdx - startIdx);

    return new ClassifiedRegion(startOffset, endOffset, type, avgEntropy);
  }
}

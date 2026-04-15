using Compression.Core.Simd;

namespace Compression.Core.Transforms;

/// <summary>
/// Burrows-Wheeler Transform for data preprocessing before compression.
/// </summary>
public static class BurrowsWheelerTransform {
  /// <summary>
  /// Performs the forward BWT on the input data.
  /// Returns the transformed data and the index of the original string in the sorted rotations.
  /// </summary>
  /// <param name="data">The input data.</param>
  /// <returns>A tuple of the transformed byte array and the original string index.</returns>
  public static (byte[] Transformed, int OriginalIndex) Forward(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return ([], 0);

    var length = data.Length;
    var input = data.ToArray();
    var suffixArray = BuildRotationSort(input, length);

    var transformed = new byte[length];
    var originalIndex = 0;

    for (var i = 0; i < length; ++i)
      if (suffixArray[i] == 0) {
        originalIndex = i;
        transformed[i] = input[length - 1];
      }
      else
        transformed[i] = input[suffixArray[i] - 1];

    return (transformed, originalIndex);
  }

  /// <summary>
  /// Performs the inverse BWT to recover the original data.
  /// </summary>
  /// <param name="data">The BWT-transformed data.</param>
  /// <param name="originalIndex">The index of the original string in the sorted rotations.</param>
  /// <returns>The original data.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The original index is out of range.</exception>
  public static byte[] Inverse(ReadOnlySpan<byte> data, int originalIndex) {
    if (data.Length == 0)
      return [];

    var length = data.Length;
    ArgumentOutOfRangeException.ThrowIfNegative(originalIndex);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(originalIndex, length);

    // Count occurrences of each byte using SIMD-accelerated histogram
    Span<int> count = stackalloc int[256];
    count.Clear();
    SimdHistogram.ComputeHistogram(data[..length], count);

    // Cumulative counts (first occurrence of each byte in sorted first column)
    var cumulative = new int[256];
    var sum = 0;
    for (var c = 0; c < 256; ++c) {
      cumulative[c] = sum;
      sum += count[c];
    }

    // Build LF-mapping: for each position in the last column, where does it map in the first column?
    var lfMap = new int[length];
    var tempCount = new int[256];
    cumulative.AsSpan().CopyTo(tempCount);
    for (var i = 0; i < length; ++i) {
      lfMap[i] = tempCount[data[i]];
      ++tempCount[data[i]];
    }

    // Reconstruct original by following LF pointers
    var result = new byte[length];
    var idx = originalIndex;
    for (var i = length - 1; i >= 0; --i) {
      result[i] = data[idx];
      idx = lfMap[idx];
    }

    return result;
  }

  /// <summary>
  /// Sorts rotation indices using O(n log^2 n) prefix-doubling.
  /// Unlike a suffix array, comparisons wrap around the input cyclically.
  /// The first pass uses counting sort (radix sort) with SIMD-accelerated histogram
  /// for the initial byte ranking, avoiding the expensive comparison sort for gap=1.
  /// </summary>
  private static int[] BuildRotationSort(byte[] data, int length) {
    var sa = new int[length];
    var rank = new int[length];
    var tmp = new int[length];

    // Initial ranking by first byte
    for (var i = 0; i < length; ++i) {
      sa[i] = i;
      rank[i] = data[i];
    }

    // First pass (gap=1): use counting sort on (rank[i], data[(i+1) % length]) pairs
    // This avoids O(n log n) comparison sort for the first doubling step
    {
      // Build a combined 16-bit key: high byte = rank[i] (which is data[i]), low byte = data[(i+1) % length]
      // Counting sort on this key gives stable sort by both components
      Span<int> bucketCounts = stackalloc int[65536];
      bucketCounts.Clear();

      // Count occurrences of each key
      for (var i = 0; i < length; ++i) {
        var key = (data[i] << 8) | data[(i + 1) % length];
        ++bucketCounts[key];
      }

      // Prefix sum
      var runningSum = 0;
      for (var i = 0; i < 65536; ++i) {
        var c = bucketCounts[i];
        bucketCounts[i] = runningSum;
        runningSum += c;
      }

      // Place indices in sorted order
      for (var i = 0; i < length; ++i) {
        var key = (data[i] << 8) | data[(i + 1) % length];
        sa[bucketCounts[key]++] = i;
      }

      // Compute new ranks
      tmp[sa[0]] = 0;
      for (var i = 1; i < length; ++i) {
        tmp[sa[i]] = tmp[sa[i - 1]];
        var prevSecond = data[(sa[i - 1] + 1) % length];
        var curSecond = data[(sa[i] + 1) % length];
        if (data[sa[i]] != data[sa[i - 1]] || curSecond != prevSecond)
          ++tmp[sa[i]];
      }

      tmp.AsSpan().CopyTo(rank);

      if (rank[sa[length - 1]] == length - 1)
        return sa;
    }

    // Subsequent passes: prefix-doubling with comparison sort
    for (var gap = 2; gap < length; gap *= 2) {
      var g = gap;
      var len = length;
      var r = rank;
      Array.Sort(sa, (a, b) => {
        if (r[a] != r[b])
          return r[a].CompareTo(r[b]);

        var ra = r[(a + g) % len];
        var rb = r[(b + g) % len];
        return ra.CompareTo(rb);
      });

      tmp[sa[0]] = 0;
      for (var i = 1; i < length; ++i) {
        tmp[sa[i]] = tmp[sa[i - 1]];
        var prevSecond = rank[(sa[i - 1] + g) % length];
        var curSecond = rank[(sa[i] + g) % length];
        if (rank[sa[i]] != rank[sa[i - 1]] || curSecond != prevSecond)
          ++tmp[sa[i]];
      }

      tmp.AsSpan().CopyTo(rank);

      if (rank[sa[length - 1]] == length - 1)
        break;
    }

    return sa;
  }
}

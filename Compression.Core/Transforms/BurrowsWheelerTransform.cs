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

    int n = data.Length;
    byte[] input = data.ToArray();
    int[] suffixArray = BuildRotationSort(input, n);

    byte[] transformed = new byte[n];
    int originalIndex = 0;

    for (int i = 0; i < n; ++i) {
      if (suffixArray[i] == 0) {
        originalIndex = i;
        transformed[i] = input[n - 1];
      }
      else
        transformed[i] = input[suffixArray[i] - 1];
    }

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

    int n = data.Length;
    ArgumentOutOfRangeException.ThrowIfNegative(originalIndex);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(originalIndex, n);

    // Count occurrences of each byte
    int[] count = new int[256];
    for (int i = 0; i < n; ++i)
      ++count[data[i]];

    // Cumulative counts (first occurrence of each byte in sorted first column)
    int[] cumulative = new int[256];
    int sum = 0;
    for (int c = 0; c < 256; ++c) {
      cumulative[c] = sum;
      sum += count[c];
    }

    // Build LF-mapping: for each position in the last column, where does it map in the first column?
    int[] lfMap = new int[n];
    int[] tempCount = new int[256];
    cumulative.AsSpan().CopyTo(tempCount);
    for (int i = 0; i < n; ++i) {
      lfMap[i] = tempCount[data[i]];
      ++tempCount[data[i]];
    }

    // Reconstruct original by following LF pointers
    byte[] result = new byte[n];
    int idx = originalIndex;
    for (int i = n - 1; i >= 0; --i) {
      result[i] = data[idx];
      idx = lfMap[idx];
    }

    return result;
  }

  /// <summary>
  /// Sorts rotation indices using O(n log^2 n) prefix-doubling.
  /// Unlike a suffix array, comparisons wrap around the input cyclically.
  /// </summary>
  private static int[] BuildRotationSort(byte[] data, int n) {
    int[] sa = new int[n];
    int[] rank = new int[n];
    int[] tmp = new int[n];

    for (int i = 0; i < n; ++i) {
      sa[i] = i;
      rank[i] = data[i];
    }

    for (int gap = 1; gap < n; gap *= 2) {
      int g = gap;
      int len = n;
      int[] r = rank;
      Array.Sort(sa, (a, b) => {
        if (r[a] != r[b])
          return r[a].CompareTo(r[b]);

        int ra = r[(a + g) % len];
        int rb = r[(b + g) % len];
        return ra.CompareTo(rb);
      });

      tmp[sa[0]] = 0;
      for (int i = 1; i < n; ++i) {
        tmp[sa[i]] = tmp[sa[i - 1]];
        int prevSecond = rank[(sa[i - 1] + g) % n];
        int curSecond = rank[(sa[i] + g) % n];
        if (rank[sa[i]] != rank[sa[i - 1]] || curSecond != prevSecond)
          ++tmp[sa[i]];
      }

      tmp.AsSpan().CopyTo(rank);

      if (rank[sa[n - 1]] == n - 1)
        break;
    }

    return sa;
  }
}

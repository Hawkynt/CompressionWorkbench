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

    int length = data.Length;
    byte[] input = data.ToArray();
    int[] suffixArray = BuildRotationSort(input, length);

    byte[] transformed = new byte[length];
    int originalIndex = 0;

    for (int i = 0; i < length; ++i) {
      if (suffixArray[i] == 0) {
        originalIndex = i;
        transformed[i] = input[length - 1];
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

    int length = data.Length;
    ArgumentOutOfRangeException.ThrowIfNegative(originalIndex);
    ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(originalIndex, length);

    // Count occurrences of each byte (4-way unrolled to reduce loop overhead)
    int[] count = new int[256];
    int[] count1 = new int[256];
    int[] count2 = new int[256];
    int[] count3 = new int[256];
    int i4 = 0;
    int end4 = length - 3;
    while (i4 < end4) {
      ++count[data[i4]];
      ++count1[data[i4 + 1]];
      ++count2[data[i4 + 2]];
      ++count3[data[i4 + 3]];
      i4 += 4;
    }

    for (; i4 < length; ++i4)
      ++count[data[i4]];

    for (int k = 0; k < 256; ++k)
      count[k] += count1[k] + count2[k] + count3[k];

    // Cumulative counts (first occurrence of each byte in sorted first column)
    int[] cumulative = new int[256];
    int sum = 0;
    for (int c = 0; c < 256; ++c) {
      cumulative[c] = sum;
      sum += count[c];
    }

    // Build LF-mapping: for each position in the last column, where does it map in the first column?
    int[] lfMap = new int[length];
    int[] tempCount = new int[256];
    cumulative.AsSpan().CopyTo(tempCount);
    for (int i = 0; i < length; ++i) {
      lfMap[i] = tempCount[data[i]];
      ++tempCount[data[i]];
    }

    // Reconstruct original by following LF pointers
    byte[] result = new byte[length];
    int idx = originalIndex;
    for (int i = length - 1; i >= 0; --i) {
      result[i] = data[idx];
      idx = lfMap[idx];
    }

    return result;
  }

  /// <summary>
  /// Sorts rotation indices using O(n log^2 n) prefix-doubling.
  /// Unlike a suffix array, comparisons wrap around the input cyclically.
  /// </summary>
  private static int[] BuildRotationSort(byte[] data, int length) {
    int[] sa = new int[length];
    int[] rank = new int[length];
    int[] tmp = new int[length];

    for (int i = 0; i < length; ++i) {
      sa[i] = i;
      rank[i] = data[i];
    }

    for (int gap = 1; gap < length; gap *= 2) {
      int g = gap;
      int len = length;
      int[] r = rank;
      Array.Sort(sa, (a, b) => {
        if (r[a] != r[b])
          return r[a].CompareTo(r[b]);

        int ra = r[(a + g) % len];
        int rb = r[(b + g) % len];
        return ra.CompareTo(rb);
      });

      tmp[sa[0]] = 0;
      for (int i = 1; i < length; ++i) {
        tmp[sa[i]] = tmp[sa[i - 1]];
        int prevSecond = rank[(sa[i - 1] + g) % length];
        int curSecond = rank[(sa[i] + g) % length];
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

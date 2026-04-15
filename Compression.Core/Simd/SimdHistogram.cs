#pragma warning disable CS1591

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Compression.Core.Simd;

/// <summary>
/// SIMD-accelerated byte frequency histogram for entropy analysis and Huffman tree building.
/// Uses four-way unrolled counting with SIMD scatter when available, falling back to
/// a four-way scalar unroll for cache-friendly counting.
/// </summary>
public static class SimdHistogram {
  /// <summary>
  /// Counts the frequency of each byte value (0-255) in the given data.
  /// Returns a 256-element array where index <c>i</c> is the count of byte value <c>i</c>.
  /// </summary>
  /// <param name="data">The input data to analyze.</param>
  /// <returns>A 256-element frequency array.</returns>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static long[] ComputeHistogram(ReadOnlySpan<byte> data) {
    var result = new long[256];
    ComputeHistogram(data, result);
    return result;
  }

  /// <summary>
  /// Counts the frequency of each byte value (0-255) in the given data,
  /// writing results into the provided 256-element array (which is NOT cleared first).
  /// </summary>
  /// <param name="data">The input data to analyze.</param>
  /// <param name="counts">A pre-allocated 256-element array to receive counts.</param>
  public static void ComputeHistogram(ReadOnlySpan<byte> data, long[] counts) {
    if (data.Length == 0)
      return;

    if (Vector256.IsHardwareAccelerated && data.Length >= 128)
      _ComputeHistogramSimd(data, counts);
    else
      _ComputeHistogramScalar(data, counts);
  }

  /// <summary>
  /// Counts the frequency of each byte value (0-255) in the given data,
  /// writing results into the provided 256-element span (which is NOT cleared first).
  /// Uses a scalar four-way unrolled loop for cache-friendly counting.
  /// </summary>
  /// <param name="data">The input data to analyze.</param>
  /// <param name="counts">A pre-allocated 256-element span to receive counts.</param>
  public static void ComputeHistogram(ReadOnlySpan<byte> data, Span<int> counts) {
    if (data.Length == 0)
      return;

    // Four separate count arrays to avoid store-forwarding stalls
    // when multiple bytes in the same unrolled iteration hash to the same bucket
    Span<int> c0 = stackalloc int[256];
    Span<int> c1 = stackalloc int[256];
    Span<int> c2 = stackalloc int[256];
    Span<int> c3 = stackalloc int[256];
    c0.Clear();
    c1.Clear();
    c2.Clear();
    c3.Clear();

    var i = 0;
    var end4 = data.Length - 3;

    while (i < end4) {
      ++c0[data[i]];
      ++c1[data[i + 1]];
      ++c2[data[i + 2]];
      ++c3[data[i + 3]];
      i += 4;
    }

    for (; i < data.Length; ++i)
      ++c0[data[i]];

    // Merge the four histograms
    if (Vector256.IsHardwareAccelerated) {
      var j = 0;
      while (j + Vector256<int>.Count <= 256) {
        var v0 = Vector256.Create<int>(c0.Slice(j, Vector256<int>.Count));
        var v1 = Vector256.Create<int>(c1.Slice(j, Vector256<int>.Count));
        var v2 = Vector256.Create<int>(c2.Slice(j, Vector256<int>.Count));
        var v3 = Vector256.Create<int>(c3.Slice(j, Vector256<int>.Count));
        var vc = Vector256.Create<int>(counts.Slice(j, Vector256<int>.Count));
        var sum = vc + v0 + v1 + v2 + v3;
        sum.CopyTo(counts.Slice(j, Vector256<int>.Count));
        j += Vector256<int>.Count;
      }
    } else
      for (var k = 0; k < 256; ++k)
        counts[k] += c0[k] + c1[k] + c2[k] + c3[k];
  }

  private static void _ComputeHistogramSimd(ReadOnlySpan<byte> data, long[] counts) {
    // Use four separate int arrays to reduce store-forwarding stalls,
    // then SIMD-merge at the end into the long[] result
    var c0 = new int[256];
    var c1 = new int[256];
    var c2 = new int[256];
    var c3 = new int[256];

    var i = 0;
    var end4 = data.Length - 3;

    while (i < end4) {
      ++c0[data[i]];
      ++c1[data[i + 1]];
      ++c2[data[i + 2]];
      ++c3[data[i + 3]];
      i += 4;
    }

    for (; i < data.Length; ++i)
      ++c0[data[i]];

    // SIMD merge: add four int[256] arrays and widen to long[256]
    // Vector256<int>.Count = 8 ints per vector, Widen splits into two Vector256<long> of 4 each
    for (var j = 0; j < 256; j += Vector256<int>.Count) {
      var v0 = Vector256.Create<int>(c0.AsSpan(j, Vector256<int>.Count));
      var v1 = Vector256.Create<int>(c1.AsSpan(j, Vector256<int>.Count));
      var v2 = Vector256.Create<int>(c2.AsSpan(j, Vector256<int>.Count));
      var v3 = Vector256.Create<int>(c3.AsSpan(j, Vector256<int>.Count));
      var sum = v0 + v1 + v2 + v3;

      // Widen 8 ints -> 2 x 4 longs, accumulate into counts[j..j+8]
      var (lo, hi) = Vector256.Widen(sum);
      (lo + Vector256.Create<long>(counts.AsSpan(j, Vector256<long>.Count)))
        .CopyTo(counts.AsSpan(j, Vector256<long>.Count));
      (hi + Vector256.Create<long>(counts.AsSpan(j + Vector256<long>.Count, Vector256<long>.Count)))
        .CopyTo(counts.AsSpan(j + Vector256<long>.Count, Vector256<long>.Count));
    }
  }

  private static void _ComputeHistogramScalar(ReadOnlySpan<byte> data, long[] counts) {
    // Four-way unrolled scalar path
    var c0 = new int[256];
    var c1 = new int[256];
    var c2 = new int[256];
    var c3 = new int[256];

    var i = 0;
    var end4 = data.Length - 3;

    while (i < end4) {
      ++c0[data[i]];
      ++c1[data[i + 1]];
      ++c2[data[i + 2]];
      ++c3[data[i + 3]];
      i += 4;
    }

    for (; i < data.Length; ++i)
      ++c0[data[i]];

    for (var k = 0; k < 256; ++k)
      counts[k] += c0[k] + c1[k] + c2[k] + c3[k];
  }

  /// <summary>
  /// Computes the Shannon entropy (in bits per byte) of the given data.
  /// Uses the SIMD-accelerated histogram internally.
  /// </summary>
  /// <param name="data">The input data.</param>
  /// <returns>Entropy in bits per byte (0.0 = fully uniform single byte, 8.0 = maximum entropy).</returns>
  public static double ComputeEntropy(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return 0.0;

    var histogram = ComputeHistogram(data);
    var length = (double)data.Length;
    var entropy = 0.0;

    for (var i = 0; i < 256; ++i) {
      if (histogram[i] == 0)
        continue;

      var p = histogram[i] / length;
      entropy -= p * Math.Log2(p);
    }

    return entropy;
  }
}

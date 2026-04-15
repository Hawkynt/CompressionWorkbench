#pragma warning disable CS1591

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Compression.Core.Simd;

/// <summary>
/// SIMD-accelerated run-length scanning for RLE encoders.
/// Uses <see cref="Vector256{T}"/> to quickly find where consecutive equal bytes end
/// (i.e., the first position where <c>data[i] != data[i-1]</c>).
/// </summary>
public static class SimdRunScan {
  /// <summary>
  /// Starting from position <paramref name="start"/> in <paramref name="data"/>,
  /// returns the length of the run of bytes equal to <c>data[start]</c>.
  /// The run length is capped at <paramref name="maxRun"/>.
  /// </summary>
  /// <param name="data">The input data.</param>
  /// <param name="start">The starting position of the run.</param>
  /// <param name="maxRun">Maximum run length to report.</param>
  /// <returns>The run length (at least 1 if <paramref name="start"/> is valid).</returns>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int GetRunLength(ReadOnlySpan<byte> data, int start, int maxRun) {
    if (start >= data.Length)
      return 0;

    var value = data[start];
    var remaining = Math.Min(maxRun, data.Length - start);
    var run = 1;

    if (Vector256.IsHardwareAccelerated && remaining >= Vector256<byte>.Count + 1) {
      var broadcast = Vector256.Create(value);

      // Check 32 bytes at a time starting from start+1
      while (run + Vector256<byte>.Count <= remaining) {
        var chunk = Vector256.Create<byte>(data.Slice(start + run, Vector256<byte>.Count));
        var cmp = Vector256.Equals(chunk, broadcast);

        if (cmp == Vector256<byte>.AllBitsSet) {
          // All 32 bytes equal the run value
          run += Vector256<byte>.Count;
          continue;
        }

        // Find first non-matching byte
        var mismatchMask = (~cmp).ExtractMostSignificantBits();
        run += int.TrailingZeroCount((int)mismatchMask);
        return run;
      }
    }

    // Scalar tail
    while (run < remaining && data[start + run] == value)
      ++run;

    return run;
  }

  /// <summary>
  /// Scans <paramref name="data"/> and returns an array of (start, length, value) tuples
  /// representing all runs. Uses SIMD acceleration when available.
  /// </summary>
  /// <param name="data">The input data.</param>
  /// <param name="maxRunLength">Maximum run length per entry (e.g. 255 for byte-counted RLE).</param>
  /// <returns>List of runs as (start index, run length, byte value).</returns>
  public static List<(int Start, int Length, byte Value)> FindAllRuns(ReadOnlySpan<byte> data, int maxRunLength = 255) {
    var runs = new List<(int, int, byte)>();
    var i = 0;

    while (i < data.Length) {
      var runLen = GetRunLength(data, i, maxRunLength);
      runs.Add((i, runLen, data[i]));
      i += runLen;
    }

    return runs;
  }
}

#pragma warning disable CS1591

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Compression.Core.Simd;

/// <summary>
/// SIMD-accelerated match length calculation for LZ-style compressors.
/// Compares two spans byte-by-byte and returns the length of the matching prefix,
/// using <see cref="Vector256{T}"/> to compare 32 bytes at a time when available.
/// </summary>
public static class SimdMatchLength {
  /// <summary>
  /// Returns the number of leading bytes that are identical in <paramref name="data"/>
  /// starting at positions <paramref name="pos1"/> and <paramref name="pos2"/>,
  /// up to a maximum of <paramref name="limit"/> bytes.
  /// </summary>
  /// <param name="data">The data buffer containing both sequences.</param>
  /// <param name="pos1">Start of the first sequence.</param>
  /// <param name="pos2">Start of the second sequence.</param>
  /// <param name="limit">Maximum number of bytes to compare.</param>
  /// <returns>The number of matching bytes at the start of both sequences.</returns>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int GetMatchLength(ReadOnlySpan<byte> data, int pos1, int pos2, int limit) {
    if (limit <= 0)
      return 0;

    var matched = 0;

    if (Vector256.IsHardwareAccelerated) {
      // Compare 32 bytes at a time using SIMD
      while (matched + Vector256<byte>.Count <= limit) {
        var v1 = Vector256.Create<byte>(data.Slice(pos1 + matched, Vector256<byte>.Count));
        var v2 = Vector256.Create<byte>(data.Slice(pos2 + matched, Vector256<byte>.Count));
        var cmp = Vector256.Equals(v1, v2);

        if (cmp == Vector256<byte>.AllBitsSet) {
          // All 32 bytes match
          matched += Vector256<byte>.Count;
          continue;
        }

        // Find first mismatch: ~Equals gives 0xFF at mismatches, extract bitmask
        var mismatchMask = (~cmp).ExtractMostSignificantBits();
        matched += int.TrailingZeroCount((int)mismatchMask);
        return matched;
      }
    }

    // Scalar tail (or full scalar fallback)
    while (matched < limit && data[pos1 + matched] == data[pos2 + matched])
      ++matched;

    return matched;
  }

  /// <summary>
  /// Returns the number of leading bytes that are identical between two separate spans,
  /// up to a maximum of <paramref name="limit"/> bytes.
  /// </summary>
  /// <param name="a">The first byte sequence.</param>
  /// <param name="b">The second byte sequence.</param>
  /// <param name="limit">Maximum number of bytes to compare.</param>
  /// <returns>The number of matching bytes at the start of both sequences.</returns>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static int GetMatchLength(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int limit) {
    if (limit <= 0)
      return 0;

    var matched = 0;

    if (Vector256.IsHardwareAccelerated) {
      while (matched + Vector256<byte>.Count <= limit) {
        var v1 = Vector256.Create<byte>(a.Slice(matched, Vector256<byte>.Count));
        var v2 = Vector256.Create<byte>(b.Slice(matched, Vector256<byte>.Count));
        var cmp = Vector256.Equals(v1, v2);

        if (cmp == Vector256<byte>.AllBitsSet) {
          matched += Vector256<byte>.Count;
          continue;
        }

        var mismatchMask = (~cmp).ExtractMostSignificantBits();
        matched += int.TrailingZeroCount((int)mismatchMask);
        return matched;
      }
    }

    while (matched < limit && a[matched] == b[matched])
      ++matched;

    return matched;
  }
}

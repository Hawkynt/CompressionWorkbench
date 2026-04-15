#pragma warning disable CS1591

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Compression.Core.Simd;

/// <summary>
/// SIMD-accelerated memory copy utilities for bulk literal output in decompressors.
/// Uses <see cref="Vector256{T}"/> to copy 32 bytes at a time when available,
/// falling back to <see cref="ReadOnlySpan{T}.CopyTo"/> for smaller blocks.
/// </summary>
public static class SimdMemCopy {
  /// <summary>
  /// Copies <paramref name="length"/> bytes from <paramref name="source"/> at
  /// <paramref name="srcOffset"/> to <paramref name="destination"/> at
  /// <paramref name="dstOffset"/> using SIMD when beneficial.
  /// </summary>
  /// <param name="source">The source buffer.</param>
  /// <param name="srcOffset">Offset into the source buffer.</param>
  /// <param name="destination">The destination buffer.</param>
  /// <param name="dstOffset">Offset into the destination buffer.</param>
  /// <param name="length">Number of bytes to copy.</param>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void Copy(ReadOnlySpan<byte> source, int srcOffset, Span<byte> destination, int dstOffset, int length) {
    if (length <= 0)
      return;

    // For small copies or when SIMD is unavailable, use built-in copy
    if (!Vector256.IsHardwareAccelerated || length < Vector256<byte>.Count) {
      source.Slice(srcOffset, length).CopyTo(destination.Slice(dstOffset, length));
      return;
    }

    var copied = 0;

    // Copy 32 bytes at a time
    while (copied + Vector256<byte>.Count <= length) {
      var vec = Vector256.Create<byte>(source.Slice(srcOffset + copied, Vector256<byte>.Count));
      vec.CopyTo(destination.Slice(dstOffset + copied, Vector256<byte>.Count));
      copied += Vector256<byte>.Count;
    }

    // Copy remaining bytes
    if (copied < length)
      source.Slice(srcOffset + copied, length - copied).CopyTo(destination.Slice(dstOffset + copied));
  }

  /// <summary>
  /// Fills <paramref name="length"/> bytes in <paramref name="destination"/> starting
  /// at <paramref name="offset"/> with the given <paramref name="value"/> using SIMD broadcast.
  /// </summary>
  /// <param name="destination">The destination buffer.</param>
  /// <param name="offset">Offset into the destination buffer.</param>
  /// <param name="length">Number of bytes to fill.</param>
  /// <param name="value">The byte value to fill with.</param>
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public static void Fill(Span<byte> destination, int offset, int length, byte value) {
    if (length <= 0)
      return;

    if (!Vector256.IsHardwareAccelerated || length < Vector256<byte>.Count) {
      destination.Slice(offset, length).Fill(value);
      return;
    }

    var filled = 0;
    var vec = Vector256.Create(value);

    while (filled + Vector256<byte>.Count <= length) {
      vec.CopyTo(destination.Slice(offset + filled, Vector256<byte>.Count));
      filled += Vector256<byte>.Count;
    }

    // Fill remaining bytes
    if (filled < length)
      destination.Slice(offset + filled, length - filled).Fill(value);
  }
}

using System.Runtime.Intrinsics;

namespace Compression.Core.Transforms;

/// <summary>
/// Delta filter for data preprocessing.
/// Encodes each byte as the difference from the byte at a fixed distance behind it,
/// which is effective for data with local correlations (e.g., audio samples, sensor data).
/// </summary>
public static class DeltaFilter {
  /// <summary>
  /// Encodes data using the delta filter.
  /// Each output byte is the difference between the input byte and the input byte
  /// <paramref name="distance"/> positions earlier. The first <paramref name="distance"/>
  /// bytes are copied unchanged.
  /// </summary>
  /// <param name="data">The input data.</param>
  /// <param name="distance">The delta distance in bytes. Defaults to 1.</param>
  /// <returns>The delta-encoded data.</returns>
  /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="distance"/> is less than 1.</exception>
  public static byte[] Encode(ReadOnlySpan<byte> data, int distance = 1) {
    ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);

    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];

    // Copy the first 'distance' bytes unchanged
    var copyLen = Math.Min(distance, data.Length);
    data[..copyLen].CopyTo(result);

    var i = distance;

    // SIMD path for distance=1: no cross-iteration dependency in encode
    if (distance == 1 && Vector256.IsHardwareAccelerated) {
      var simdEnd = data.Length - 31;
      while (i < simdEnd) {
        var curr = Vector256.Create<byte>(data[i..]);
        var prev = Vector256.Create<byte>(data[(i - 1)..]);
        var diff = curr - prev;
        diff.CopyTo(result.AsSpan(i));
        i += 32;
      }
    }

    // Scalar tail (or non-SIMD / distance > 1 path)
    for (; i < data.Length; ++i)
      result[i] = (byte)(data[i] - data[i - distance]);

    return result;
  }

  /// <summary>
  /// Decodes delta-encoded data back to the original.
  /// Each output byte is the sum of the encoded byte and the already-decoded byte
  /// <paramref name="distance"/> positions earlier. The first <paramref name="distance"/>
  /// bytes are copied unchanged.
  /// </summary>
  /// <param name="data">The delta-encoded data.</param>
  /// <param name="distance">The delta distance in bytes. Must match the value used during encoding. Defaults to 1.</param>
  /// <returns>The decoded data.</returns>
  /// <exception cref="ArgumentOutOfRangeException">Thrown if <paramref name="distance"/> is less than 1.</exception>
  public static byte[] Decode(ReadOnlySpan<byte> data, int distance = 1) {
    ArgumentOutOfRangeException.ThrowIfLessThan(distance, 1);

    if (data.Length == 0)
      return [];

    var result = new byte[data.Length];

    // Copy the first 'distance' bytes unchanged
    var copyLen = Math.Min(distance, data.Length);
    data[..copyLen].CopyTo(result);

    // Decode has cross-iteration dependency for distance=1, so stays scalar
    for (var i = distance; i < data.Length; ++i)
      result[i] = (byte)(data[i] + result[i - distance]);

    return result;
  }
}

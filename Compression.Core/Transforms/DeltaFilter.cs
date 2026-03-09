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

    byte[] result = new byte[data.Length];

    // Copy the first 'distance' bytes unchanged
    int copyLen = Math.Min(distance, data.Length);
    data.Slice(0, copyLen).CopyTo(result);

    // Encode remaining bytes as differences
    for (int i = distance; i < data.Length; ++i)
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

    byte[] result = new byte[data.Length];

    // Copy the first 'distance' bytes unchanged
    int copyLen = Math.Min(distance, data.Length);
    data.Slice(0, copyLen).CopyTo(result);

    // Decode remaining bytes by adding back the reference
    for (int i = distance; i < data.Length; ++i)
      result[i] = (byte)(data[i] + result[i - distance]);

    return result;
  }
}

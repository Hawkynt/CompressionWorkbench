using Compression.Core.Simd;

namespace Compression.Core.Transforms;

/// <summary>
/// Run-Length Encoding (RLE) transform.
/// Encodes runs of identical bytes as (count, value) pairs.
/// </summary>
public static class RunLengthEncoding {
  /// <summary>
  /// Encodes data using run-length encoding.
  /// Output format: repeated pairs of (count, value) where count is 1-255.
  /// Uses SIMD-accelerated run scanning when available.
  /// </summary>
  /// <param name="data">The input data to encode.</param>
  /// <returns>The RLE-encoded data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var output = new List<byte>();
    var i = 0;

    while (i < data.Length) {
      var run = SimdRunScan.GetRunLength(data, i, 255);
      output.Add((byte)run);
      output.Add(data[i]);
      i += run;
    }

    return [.. output];
  }

  /// <summary>
  /// Decodes RLE-encoded data back to the original.
  /// Input format: repeated pairs of (count, value) where count is 1-255.
  /// </summary>
  /// <param name="data">The RLE-encoded data.</param>
  /// <returns>The decoded data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    if ((data.Length & 1) != 0)
      throw new InvalidDataException("RLE data must have even length (count/value pairs).");

    var output = new List<byte>();

    for (var i = 0; i < data.Length; i += 2) {
      var count = data[i];
      var value = data[i + 1];
      for (var j = 0; j < count; ++j)
        output.Add(value);
    }

    return [.. output];
  }
}

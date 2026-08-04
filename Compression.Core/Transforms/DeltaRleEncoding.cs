namespace Compression.Core.Transforms;

/// <summary>
/// Delta + RLE: the <see cref="DeltaFilter"/> transform (distance 1) followed by a
/// marker-based run-length encoding of the resulting delta stream. Unlike the pure
/// <see cref="DeltaFilter"/>, which never changes the data length, this stage actually
/// compresses — runs of two or more identical delta bytes collapse to a 3-byte
/// (marker, count, value) triplet.
/// <para>
/// This is a different codec from <see cref="RunLengthEncoding"/>: that one always emits
/// unconditional (count, value) pairs, so every non-repeated byte costs two output bytes.
/// Here, non-repeated bytes pass through literally (one output byte), runs of 2-255
/// identical bytes are encoded as the triplet <c>(0xFF, count, value)</c>, and a literal
/// occurrence of the marker byte 0xFF is escaped as the triplet <c>(0xFF, 1, 0xFF)</c> so
/// the decoder can never mistake a genuine data byte for the start of a run. This exact
/// scheme (and the composition with delta) mirrors the reference "Delta + RLE" wire format
/// so that the two implementations are byte-for-byte interoperable.
/// </para>
/// </summary>
public static class DeltaRleEncoding {
  private const byte Marker = 255;
  private const int MaxRunLength = 255;

  /// <summary>
  /// Encodes data with the Delta filter (distance 1) followed by marker-based run-length
  /// encoding of the delta stream.
  /// </summary>
  /// <param name="data">The input data to encode.</param>
  /// <returns>The Delta+RLE-encoded data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var delta = DeltaFilter.Encode(data);
    return EncodeRuns(delta);
  }

  /// <summary>
  /// Decodes Delta+RLE-encoded data back to the original bytes.
  /// </summary>
  /// <param name="data">The Delta+RLE-encoded data.</param>
  /// <returns>The decoded data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var delta = DecodeRuns(data);
    return DeltaFilter.Decode(delta);
  }

  private static byte[] EncodeRuns(ReadOnlySpan<byte> data) {
    var result = new List<byte>(data.Length);
    var count = 1;
    var current = data[0];

    for (var i = 1; i < data.Length; i++) {
      if (data[i] == current && count < MaxRunLength) {
        ++count;
        continue;
      }

      EmitRun(result, current, count);
      current = data[i];
      count = 1;
    }

    EmitRun(result, current, count);
    return [.. result];
  }

  private static void EmitRun(List<byte> result, byte value, int count) {
    if (count > 1) {
      result.Add(Marker);
      result.Add((byte)count);
      result.Add(value);
    } else if (value == Marker) {
      // Escape a literal marker byte so the decoder can't confuse it with a run.
      result.Add(Marker);
      result.Add(1);
      result.Add(Marker);
    } else {
      result.Add(value);
    }
  }

  private static byte[] DecodeRuns(ReadOnlySpan<byte> data) {
    var result = new List<byte>(data.Length);
    var i = 0;

    while (i < data.Length) {
      if (data[i] == Marker && i + 2 < data.Length) {
        var count = data[i + 1];
        var value = data[i + 2];
        for (var j = 0; j < count; ++j)
          result.Add(value);
        i += 3;
      } else {
        result.Add(data[i]);
        ++i;
      }
    }

    return [.. result];
  }
}

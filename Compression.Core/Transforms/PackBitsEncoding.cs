namespace Compression.Core.Transforms;

/// <summary>
/// Apple PackBits run-length encoding, as specified in Apple Technical Note TN1023
/// and adopted by the TIFF 6.0 specification (section 2, "PackBits" compression).
/// The compressed stream is a sequence of control bytes, each interpreted as a
/// signed 8-bit count:
/// <list type="bullet">
/// <item>0..127: copy the following (count + 1) bytes literally.</item>
/// <item>-1..-127: repeat the single following byte (1 - count) times.</item>
/// <item>-128: no-op, reserved (skipped).</item>
/// </list>
/// The format is self-terminating — no length header is required since the decoder
/// simply consumes control bytes until the input is exhausted.
/// </summary>
public static class PackBitsEncoding {
  private const int MaxRunLength = 128;
  private const int MaxLiteralLength = 128;
  private const int MinRunLength = 3;

  /// <summary>
  /// Encodes data using Apple PackBits run-length encoding.
  /// </summary>
  /// <param name="data">The input data to encode.</param>
  /// <returns>The PackBits-encoded data.</returns>
  public static byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var output = new List<byte>();
    var i = 0;

    while (i < data.Length) {
      var runLength = RunLengthAt(data, i);

      if (runLength >= MinRunLength) {
        output.Add((byte)(257 - runLength));
        output.Add(data[i]);
        i += runLength;
        continue;
      }

      // Accumulate a literal sequence, stopping just before any run of at least
      // MinRunLength identical bytes so it can be encoded as a run instead.
      var literalStart = i;
      var literalLength = 0;
      while (i < data.Length && literalLength < MaxLiteralLength) {
        if (RunLengthAt(data, i) >= MinRunLength)
          break;
        literalLength++;
        i++;
      }

      output.Add((byte)(literalLength - 1));
      for (var j = 0; j < literalLength; j++)
        output.Add(data[literalStart + j]);
    }

    return [.. output];
  }

  /// <summary>
  /// Decodes PackBits-encoded data back to the original bytes.
  /// </summary>
  /// <param name="data">The PackBits-encoded data.</param>
  /// <returns>The decoded data.</returns>
  public static byte[] Decode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var output = new List<byte>();
    var i = 0;

    while (i < data.Length) {
      sbyte control = (sbyte)data[i++];

      switch (control) {
        case >= 0:
          var literalLength = control + 1;
          for (var j = 0; j < literalLength && i < data.Length; j++)
            output.Add(data[i++]);
          break;

        case > -128:
          if (i >= data.Length)
            break;
          var runLength = 1 - control;
          var value = data[i++];
          for (var j = 0; j < runLength; j++)
            output.Add(value);
          break;

        default:
          // -128: reserved no-op, consumes only the control byte.
          break;
      }
    }

    return [.. output];
  }

  /// <summary>Length of the run of identical bytes starting at <paramref name="pos"/>, capped at <see cref="MaxRunLength"/>.</summary>
  private static int RunLengthAt(ReadOnlySpan<byte> data, int pos) {
    var run = 1;
    while (pos + run < data.Length && data[pos + run] == data[pos] && run < MaxRunLength)
      run++;
    return run;
  }
}

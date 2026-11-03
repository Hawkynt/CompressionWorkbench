namespace Compression.Core.Dictionary.Lzf;

/// <summary>
/// LZF decompression exactly as liblzf's <c>lzf_decompress</c> performs it, with its input
/// checks.
/// </summary>
/// <remarks>
/// Converted from Marc Lehmann's liblzf 3.6 <c>lzf_d.c</c> (BSD-2-Clause; see
/// THIRD-PARTY-NOTICE.liblzf.txt). See <see cref="LzfCompressor"/> for the format.
/// </remarks>
public static class LzfDecompressor {
  /// <summary>Decompresses a bare LZF stream that expands to <paramref name="originalLength"/> bytes.</summary>
  /// <param name="data">The LZF stream.</param>
  /// <param name="originalLength">The exact uncompressed size.</param>
  /// <returns>The decompressed bytes.</returns>
  /// <exception cref="InvalidDataException">
  /// The stream is truncated, references data before its start, overflows
  /// <paramref name="originalLength"/>, or expands to fewer bytes.
  /// </exception>
  public static byte[] Decompress(ReadOnlySpan<byte> data, int originalLength) {
    ArgumentOutOfRangeException.ThrowIfNegative(originalLength);
    var output = new byte[originalLength];
    var ip = 0;
    var op = 0;

    while (ip < data.Length) {
      var control = (int)data[ip++];
      if (control < 1 << 5) {
        ++control;
        if (op + control > output.Length)
          throw new InvalidDataException("LZF: literal run overflows the output.");
        if (ip + control > data.Length)
          throw new InvalidDataException("LZF: literal run is truncated.");
        data.Slice(ip, control).CopyTo(output.AsSpan(op));
        ip += control;
        op += control;
        continue;
      }

      var length = control >> 5;
      var reference = op - ((control & 0x1F) << 8) - 1;
      if (ip >= data.Length)
        throw new InvalidDataException("LZF: back-reference is truncated.");
      if (length == 7) {
        length += data[ip++];
        if (ip >= data.Length)
          throw new InvalidDataException("LZF: back-reference is truncated.");
      }

      reference -= data[ip++];
      length += 2;
      if (op + length > output.Length)
        throw new InvalidDataException("LZF: back-reference overflows the output.");
      if (reference < 0)
        throw new InvalidDataException("LZF: back-reference points before the start.");

      // Byte by byte: an overlapping reference repeats what it has just written.
      for (var i = 0; i < length; ++i)
        output[op++] = output[reference++];
    }

    if (op != output.Length)
      throw new InvalidDataException($"LZF stream expanded to {op} bytes, expected {output.Length}.");
    return output;
  }
}

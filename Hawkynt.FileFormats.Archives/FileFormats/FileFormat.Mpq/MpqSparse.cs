namespace FileFormat.Mpq;

/// <summary>
/// MPQ compression method 0x20 ("sparse", StarCraft II): a big-endian 32-bit output size, then
/// control bytes. A control byte with the top bit set copies (low 7 bits + 1) literal bytes;
/// otherwise it emits (value + 3) zero bytes.
/// </summary>
/// <remarks>
/// Converted from StormLib <c>src/sparse/sparse.cpp</c> (Ladislav Zezula, ShadowFlare; MIT) — see
/// <c>THIRD-PARTY-NOTICE.StormLib.txt</c>. The encoder keeps Blizzard's quirk that a run of
/// exactly 0x81 non-zero bytes is split as 0x80 + 1 the long way round.
/// </remarks>
internal static class MpqSparse {

  /// <summary>Decodes; the result never exceeds <paramref name="maxOutput"/>.</summary>
  /// <exception cref="InvalidDataException">The stream is shorter than its header, claims too much, or overruns.</exception>
  internal static byte[] Decompress(ReadOnlySpan<byte> input, int maxOutput) {
    if (input.Length < 5)
      throw new InvalidDataException("MPQ sparse stream is shorter than 5 bytes.");
    var size = (uint)(input[0] << 24 | input[1] << 16 | input[2] << 8 | input[3]);
    if (size > (uint)maxOutput)
      throw new InvalidDataException($"MPQ sparse stream claims {size} bytes; at most {maxOutput} fit.");

    var output = new byte[size];
    var remaining = (int)size;
    var written = 0;
    var position = 4;
    while (position < input.Length) {
      var control = input[position++];
      if ((control & 0x80) != 0) {
        var chunk = (control & 0x7F) + 1;
        if (position + chunk > input.Length)
          throw new InvalidDataException("MPQ sparse literal run overruns the stream.");
        chunk = Math.Min(chunk, remaining);
        input.Slice(position, chunk).CopyTo(output.AsSpan(written));
        position += chunk;
        written += chunk;
        remaining -= chunk;
      } else {
        var chunk = Math.Min((control & 0x7F) + 3, remaining);
        written += chunk;
        remaining -= chunk;
      }
    }
    return output[..written];
  }

  /// <summary>Encodes the way StormLib does; returns null when the result would not be shorter.</summary>
  internal static byte[]? Compress(ReadOnlySpan<byte> input) {
    var limit = input.Length - 1; // StormLib's output buffer is one byte shorter than the sector
    if (input.Length <= 4)
      return null;
    var output = new List<byte>(input.Length) {
      (byte)(input.Length >> 24), (byte)(input.Length >> 16), (byte)(input.Length >> 8), (byte)input.Length,
    };
    var position = 0;
    var end = input.Length;

    while (position < end - 3) {
      var lastNonZero = position;
      var scan = position;
      var zeros = 0;
      for (; scan < end; ++scan) {
        if (input[scan] == 0) {
          ++zeros;
        } else {
          if (zeros >= 3)
            break;
          lastNonZero = scan + 1;
          zeros = 0;
        }
      }

      var nonZeros = lastNonZero - position;
      if (nonZeros != 0) {
        while (nonZeros > 0x81) {
          if (output.Count + 0x81 >= limit)
            return null;
          output.Add(0xFF);
          AddRange(output, input.Slice(position, 0x80));
          nonZeros -= 0x80;
          position += 0x80;
        }
        if (nonZeros > 0x80) {
          if (output.Count + 2 >= limit)
            return null;
          output.Add(0x80);
          output.Add(input[position]);
          --nonZeros;
          ++position;
        }
        if (nonZeros >= 1) {
          if (output.Count + nonZeros + 1 >= limit)
            return null;
          output.Add((byte)(0x80 | (nonZeros - 1)));
          AddRange(output, input.Slice(position, nonZeros));
          position += nonZeros;
        }
      }

      while (zeros > 0x85) {
        if (output.Count + 1 >= limit)
          return null;
        output.Add(0x7F);
        zeros -= 0x82;
        position += 0x82;
      }
      if (zeros > 0x82) {
        if (output.Count + 1 >= limit)
          return null;
        output.Add(0);
        zeros -= 3;
        position += 3;
      }
      if (zeros >= 3) {
        if (output.Count + 1 >= limit)
          return null;
        output.Add((byte)(zeros - 3));
        position += zeros;
      }
    }

    if (position < end) {
      var tail = input[position..];
      if (tail.IndexOfAnyExcept((byte)0) >= 0) {
        if (output.Count + tail.Length + 1 >= limit)
          return null;
        // StormLib writes 0xFF here (0x80 literals), which its own decoder then rejects as an
        // overrun; the run is written with its real length instead.
        output.Add((byte)(0x80 | (tail.Length - 1)));
        AddRange(output, tail);
      } else {
        if (output.Count + 1 >= limit)
          return null;
        output.Add(0x7F);
      }
    }
    return [.. output];
  }

  private static void AddRange(List<byte> list, ReadOnlySpan<byte> bytes) {
    foreach (var b in bytes)
      list.Add(b);
  }
}

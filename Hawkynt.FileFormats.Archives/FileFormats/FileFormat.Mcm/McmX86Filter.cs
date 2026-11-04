namespace FileFormat.Mcm;

/// <summary>
/// mcm's x86 branch filter (E8/E9 calls and 0F 8x conditional jumps), see
/// <c>docs/MCM-ON-DISK.md</c>, "x86 (E8/E9) filter". The transform runs in passes over a
/// 16 KiB staging buffer; both directions replay those passes, because a pass boundary
/// changes the output.
/// </summary>
internal static class McmX86Filter {
  private const int Window = 16 * 1024;
  private const int Margin = 6;
  private const byte AddressXor = 0xA2;
  private const byte Marker = 0x63;
  private const byte Escape = 0xB2;
  private const ulong ResetDistance = 3 * 1024 * 32;

  private static bool IsCandidate(byte value, int indexInPass, byte previous)
    => (value & 0xFE) == 0xE8 || ((value & 0xF0) == 0x80 && indexInPass > 0 && previous == 0x0F);

  /// <summary>Applies the forward transform to a whole block.</summary>
  public static byte[] Encode(ReadOnlySpan<byte> input) {
    var output = new List<byte>(input.Length + input.Length / 8 + 16);
    ulong offset = 17, last = 17;
    var consumed = 0;
    while (consumed < input.Length) {
      var available = Math.Min(Window, input.Length - consumed);
      var pass = input.Slice(consumed, available);
      if (available <= Margin) {
        foreach (var b in pass)
          output.Add(b);
        consumed += available;
        offset += (ulong)available;
        continue;
      }
      int i = 0, o = 0;
      while (i < available - Margin && o < Window - Margin) {
        var value = pass[i];
        output.Add(value);
        ++o;
        if (IsCandidate(value, i, i > 0 ? pass[i - 1] : (byte)0)) {
          var position = offset + (ulong)i;
          var sign = pass[i + 4];
          if (sign is 0x00 or 0xFF) {
            var delta = (int)(pass[i + 1] | (uint)pass[i + 2] << 8 | (uint)pass[i + 3] << 16 | (uint)sign << 24);
            if (delta > 0 || (delta < 0 && -delta < (int)position)) {
              if (position - last > ResetDistance)
                offset = 0;
              var address = unchecked((uint)delta + (uint)position);
              output.Add(sign);
              output.Add((byte)((address >> 16) ^ AddressXor));
              output.Add((byte)((address >> 8) ^ AddressXor));
              output.Add((byte)(address ^ AddressXor));
              output.Add(Marker);
              o += 5;
              i += 5;
              last = position;
              continue;
            }
          }
          if (pass[i + 1] is 0xFF or 0x00 or Escape) {
            output.Add(Escape);
            output.Add(pass[i + 1]);
            o += 2;
            i += 2;
            continue;
          }
        }
        ++i;
      }
      consumed += i;
      offset += (ulong)i;
    }
    return output.ToArray();
  }

  /// <summary>Inverts <see cref="Encode"/> for a block whose original length is <paramref name="length"/>.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> encoded, int length) {
    var output = new byte[length];
    ulong offset = 17, last = 17;
    int consumed = 0, read = 0;
    while (consumed < length) {
      var available = Math.Min(Window, length - consumed);
      if (available <= Margin) {
        Need(encoded, read, available);
        encoded.Slice(read, available).CopyTo(output.AsSpan(consumed));
        consumed += available;
        read += available;
        offset += (ulong)available;
        continue;
      }
      int i = 0, o = 0;
      while (i < available - Margin && o < Window - Margin) {
        Need(encoded, read + o, 1);
        var value = encoded[read + o];
        output[consumed + i] = value;
        ++o;
        if (IsCandidate(value, i, i > 0 ? output[consumed + i - 1] : (byte)0)) {
          var position = offset + (ulong)i;
          Need(encoded, read + o, 1);
          var next = encoded[read + o];
          if (next is 0x00 or 0xFF) {
            Need(encoded, read + o, 5);
            if (position - last > ResetDistance)
              offset = 0;
            var address = (uint)((encoded[read + o + 1] ^ AddressXor) << 16 | (encoded[read + o + 2] ^ AddressXor) << 8 | (encoded[read + o + 3] ^ AddressXor));
            var delta = unchecked(address - (uint)position);
            output[consumed + i + 1] = (byte)delta;
            output[consumed + i + 2] = (byte)(delta >> 8);
            output[consumed + i + 3] = (byte)(delta >> 16);
            output[consumed + i + 4] = next;
            o += 5;
            i += 5;
            last = position;
            continue;
          }
          if (next == Escape) {
            Need(encoded, read + o, 2);
            output[consumed + i + 1] = encoded[read + o + 1];
            o += 2;
            i += 2;
            continue;
          }
        }
        ++i;
      }
      consumed += i;
      read += o;
      offset += (ulong)i;
    }
    if (read != encoded.Length)
      throw new InvalidDataException("mcm x86 filter: encoded length does not match the block.");
    return output;
  }

  private static void Need(ReadOnlySpan<byte> encoded, int position, int count) {
    if (position + count > encoded.Length)
      throw new InvalidDataException("mcm x86 filter: encoded stream ends early.");
  }
}

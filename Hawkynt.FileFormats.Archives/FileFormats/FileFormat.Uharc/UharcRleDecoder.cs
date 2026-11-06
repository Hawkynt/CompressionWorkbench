namespace FileFormat.Uharc;

/// <summary>
/// UHARC's RLE stream (method 1, <c>-mr</c>), as described in docs/UHARC-ON-DISK.md section "RLE".
/// </summary>
/// <remarks>
/// Two marker bytes, a triple marker <c>T</c> and a run marker <c>R = T + 1</c>, start as
/// <c>ED</c>/<c>EE</c>. <c>T x</c> is <c>x</c> three times; <c>R n x</c> (4 ≤ n ≤ 254) is <c>x</c>
/// <c>n</c> times; <c>R FF lo hi x</c> is <c>x</c> repeated a 16-bit count; <c>R c</c> with c ≤ 3 is
/// one or two literal marker bytes (<c>T</c> for even c, <c>R</c> for odd, twice when c ≥ 2), after
/// which both markers move down by 0x40. Every other byte stands for itself.
/// </remarks>
internal static class UharcRleDecoder {

  public static byte[] Decode(byte[] packed, int offset, int length, int unpackedSize) {
    var output = new byte[unpackedSize];
    var end = offset + length;
    var position = offset;
    var written = 0;
    byte triple = 0xED;

    byte Next() => position < end ? packed[position++] : throw new InvalidDataException("UHARC: RLE stream is truncated.");

    void Fill(byte value, int count) {
      if (count > output.Length - written)
        throw new InvalidDataException("UHARC: RLE run passes the end of the stream.");
      output.AsSpan(written, count).Fill(value);
      written += count;
    }

    while (written < unpackedSize) {
      var b = Next();
      var run = (byte)(triple + 1);
      if (b == triple) {
        Fill(Next(), 3);
      } else if (b == run) {
        var count = (int)Next();
        if (count <= 3) {
          Fill((count & 1) == 0 ? triple : run, (count >> 1) + 1);
          triple -= 0x40;
        } else {
          if (count == 0xFF)
            count = Next() | Next() << 8;
          Fill(Next(), count);
        }
      } else
        Fill(b, 1);
    }

    return output;
  }
}

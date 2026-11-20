namespace FileFormat.Uharc;

/// <summary>
/// UHARC's RLE stream (method 1, <c>-mr</c>), as described in docs/UHARC-ON-DISK.md section "RLE".
/// </summary>
/// <remarks>
/// Two marker bytes, a triple marker <c>T</c> and a run marker <c>R = T + 1</c>, start as
/// <c>ED</c>/<c>EE</c>. <c>T x</c> is <c>x</c> three times; <c>R n x</c> (4 ≤ n ≤ 254) is <c>x</c>
/// <c>n</c> times; <c>R FF lo hi x</c> is <c>x</c> repeated a 16-bit count; <c>R c</c> with c ≤ 3 is
/// one or two literal marker bytes (<c>T</c> for even c, <c>R</c> for odd, twice when c ≥ 2), after
/// which both markers move down by 0x40. Every other byte stands for itself. UHARC 0.2's stream
/// differs in the long run only: <c>R FF x lo hi</c>, the byte before its count.
/// </remarks>
internal static class UharcRleDecoder {

  /// <param name="packed">Buffer holding the packed stream.</param>
  /// <param name="offset">Start of the packed stream.</param>
  /// <param name="length">Length of the packed stream.</param>
  /// <param name="unpackedSize">Number of bytes to produce.</param>
  /// <param name="byteFirst">UHARC 0.2's long run, which puts the byte before its 16-bit count.</param>
  public static byte[] Decode(byte[] packed, int offset, int length, int unpackedSize, bool byteFirst = false) {
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
          if (count != 0xFF)
            Fill(Next(), count);
          else if (byteFirst) {
            var value = Next();
            Fill(value, Next() | Next() << 8);
          } else {
            count = Next() | Next() << 8;
            Fill(Next(), count);
          }
        }
      } else
        Fill(b, 1);
    }

    return output;
  }
}

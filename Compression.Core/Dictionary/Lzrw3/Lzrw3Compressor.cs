namespace Compression.Core.Dictionary.Lzrw3;

/// <summary>
/// LZRW3 compressor, converted from Ross N. Williams' public-domain reference <c>lzrw3.c</c>
/// (release 1, 30-Jun-1991). Output matches the reference byte for byte.
/// </summary>
/// <remarks>
/// The layout is LZRW1's (four flag bytes, 16-bit little-endian control words, two-byte copy
/// items), but a copy item carries a 12-bit hash-table index instead of an offset, with
/// length <c>C1[0..3] + 3</c> (3..18). Both sides keep the same 4096-entry table, which starts
/// with every entry pointing at the constant string <c>"123456789012345678"</c>; positions
/// that begin with literals are entered only once three bytes are known.
/// </remarks>
public static class Lzrw3Compressor {
  private const int FlagBytes = 4;
  private const byte FlagCompress = 0;
  private const byte FlagCopy = 1;
  private const int MaxRawItem = 18;
  private const uint TopWord = 0xFFFF0000u;

  /// <summary>Compresses <paramref name="source"/> into an LZRW3 stream.</summary>
  /// <param name="source">The bytes to compress.</param>
  /// <returns>The LZRW3 stream, flag bytes included.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    var length = source.Length;
    var output = new byte[length + 64];
    output[0] = FlagCompress;
    var outPos = FlagBytes;
    var controlPos = outPos;
    outPos += 2;

    var hash = new int[Lzrw3Table.Length];
    hash.AsSpan().Fill(Lzrw3Table.StartString);
    var srcMax1 = length - MaxRawItem;
    var srcMax16 = length - MaxRawItem * 16;
    var control = TopWord;
    var pending1 = -1; // table slot of the youngest literal still waiting for its hash
    var pending2 = -1; // table slot of the second youngest
    var pos = 0;

    while (true) {
      if (outPos > length)
        return Stored(source);

      var unroll = 16;
      var endgame = false;
      if (pos > srcMax16) {
        unroll = 1;
        if (pos > srcMax1) {
          if (pos == length)
            break;
          endgame = true;
        }
      }

      do {
        if (endgame) {
          // The reference jumps straight to its literal code here; the table entries it touches
          // are never looked up again.
          output[outPos++] = source[pos++];
          control &= 0xFFFEFFFFu;
        } else {
          var index = Lzrw3Table.Hash(source, pos);
          var candidate = hash[index];
          var ziv = pos;
          if (Lzrw3Table.At(source, candidate) != source[ziv]
              || Lzrw3Table.At(source, candidate + 1) != source[ziv + 1]
              || Lzrw3Table.At(source, candidate + 2) != source[ziv + 2]) {
            output[outPos++] = source[pos++];
            control &= 0xFFFEFFFFu;
            if (pending2 >= 0)
              hash[pending2] = ziv - 2;
            pending2 = pending1;
            pending1 = index;
          } else {
            var matched = 3;
            while (matched < MaxRawItem && Lzrw3Table.At(source, candidate + matched) == source[ziv + matched])
              ++matched;

            output[outPos++] = (byte)(((index & 0xF00) >> 4) | (matched - 3));
            output[outPos++] = (byte)index;
            pos = ziv + matched;
            if (pending1 >= 0) {
              if (pending2 >= 0) {
                hash[pending2] = ziv - 2;
                pending2 = -1;
              }

              hash[pending1] = ziv - 1;
              pending1 = -1;
            }

            hash[index] = ziv;
          }
        }

        control >>= 1;
      } while (--unroll > 0);

      if ((control & TopWord) == 0) {
        output[controlPos] = (byte)control;
        output[controlPos + 1] = (byte)(control >> 8);
        controlPos = outPos;
        outPos += 2;
        control = TopWord;
      }
    }

    while ((control & TopWord) != 0)
      control >>= 1;
    output[controlPos++] = (byte)control;
    output[controlPos++] = (byte)(control >> 8);
    if (controlPos == outPos)
      outPos -= 2;

    return output.AsSpan(0, outPos).ToArray();
  }

  private static byte[] Stored(ReadOnlySpan<byte> source) {
    var output = new byte[FlagBytes + source.Length];
    output[0] = FlagCopy;
    source.CopyTo(output.AsSpan(FlagBytes));
    return output;
  }
}

/// <summary>The hash table conventions LZRW3's compressor and decompressor share.</summary>
internal static class Lzrw3Table {
  /// <summary>Number of table entries.</summary>
  internal const int Length = 4096;

  /// <summary>Position that stands for the reference's constant start string.</summary>
  internal const int StartString = -18;

  private static ReadOnlySpan<byte> Start => "123456789012345678"u8;

  /// <summary>The byte at <paramref name="position"/>; negative positions are in the start string.</summary>
  internal static byte At(ReadOnlySpan<byte> data, int position)
    => position < 0 ? Start[position - StartString] : data[position];

  /// <summary>The reference's <c>HASH</c> of the three bytes at <paramref name="position"/>.</summary>
  internal static int Hash(ReadOnlySpan<byte> data, int position)
    => (int)((40543u * (uint)((data[position] << 8) ^ (data[position + 1] << 4) ^ data[position + 2])) >> 4) & 0xFFF;
}

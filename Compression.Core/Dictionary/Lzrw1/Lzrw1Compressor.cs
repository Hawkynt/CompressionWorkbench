namespace Compression.Core.Dictionary.Lzrw1;

/// <summary>
/// LZRW1 compressor, converted from Ross N. Williams' public-domain reference <c>lzrw1.c</c>
/// (31-Mar-1991, as printed in "An Extremely Fast Ziv-Lempel Data Compression Algorithm",
/// DCC'91). Output matches the reference byte for byte when its hash table starts empty.
/// </summary>
/// <remarks>
/// Stream layout: four flag bytes (<c>0</c> = compressed, <c>1</c> = stored copy follows), then
/// groups of a 16-bit little-endian control word and up to 16 items. Control bit <c>i</c>
/// (least significant first) marks item <c>i</c> as a copy: two bytes
/// <c>oooo LLLL, oooooooo</c> with length <c>L + 1</c> (3..16) and offset 1..4095. Literals are
/// one byte. The stream has no length field; it ends with its last item. When compressing would
/// exceed the input length, the reference stores the input instead.
/// </remarks>
public static class Lzrw1Compressor {
  private const int FlagBytes = 4;
  private const byte FlagCompress = 0;
  private const byte FlagCopy = 1;
  private const int ItemMax = 16;
  private const int MaxOffset = 4095;

  /// <summary>Compresses <paramref name="source"/> into an LZRW1 stream.</summary>
  /// <param name="source">The bytes to compress.</param>
  /// <returns>The LZRW1 stream, flag bytes included.</returns>
  public static byte[] Compress(ReadOnlySpan<byte> source) {
    var length = source.Length;
    var output = new byte[length + 64];
    output[0] = FlagCompress;
    var outPos = FlagBytes;
    var controlPos = outPos;
    outPos += 2;

    var hash = new int[4096];
    hash.AsSpan().Fill(-1);
    var srcMax1 = length - ItemMax;
    var srcMax16 = length - 16 * ItemMax;
    var control = 0;
    var controlBits = 0;
    var pos = 0;

    while (true) {
      // The reference compares against the input length, flag bytes included in the output.
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
        var copy = false;
        if (!endgame) {
          var index = Hash(source, pos);
          var candidate = hash[index];
          hash[index] = pos;
          var offset = pos - candidate;
          if (candidate >= 0 && offset is > 0 and <= MaxOffset
              && source[candidate] == source[pos] && source[candidate + 1] == source[pos + 1] && source[candidate + 2] == source[pos + 2]) {
            var matched = 3;
            while (matched < ItemMax && source[candidate + matched] == source[pos + matched])
              ++matched;

            output[outPos++] = (byte)(((offset & 0xF00) >> 4) + (matched - 1));
            output[outPos++] = (byte)offset;
            pos += matched;
            control = (control >> 1) | 0x8000;
            ++controlBits;
            copy = true;
          }
        }

        if (!copy) {
          output[outPos++] = source[pos++];
          control >>= 1;
          ++controlBits;
        }
      } while (--unroll > 0);

      if (controlBits == 16) {
        output[controlPos] = (byte)control;
        output[controlPos + 1] = (byte)(control >> 8);
        controlPos = outPos;
        outPos += 2;
        control = controlBits = 0;
      }
    }

    control >>= 16 - controlBits;
    output[controlPos++] = (byte)control;
    output[controlPos++] = (byte)(control >> 8);
    if (controlPos == outPos)
      outPos -= 2;

    return output.AsSpan(0, outPos).ToArray();
  }

  /// <summary>The reference's hash of the three bytes at <paramref name="position"/>.</summary>
  internal static int Hash(ReadOnlySpan<byte> data, int position)
    => (int)((40543u * (uint)((((data[position] << 4) ^ data[position + 1]) << 4) ^ data[position + 2])) >> 4) & 0xFFF;

  private static byte[] Stored(ReadOnlySpan<byte> source) {
    var output = new byte[FlagBytes + source.Length];
    output[0] = FlagCopy;
    source.CopyTo(output.AsSpan(FlagBytes));
    return output;
  }
}

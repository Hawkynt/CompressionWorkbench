namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LArc <c>-lz5-</c>: LZSS over a 4 KiB ring buffer, byte-aligned.
/// </summary>
/// <remarks>
/// <para>A flag byte governs the next eight commands, least significant bit first: 1 is a literal byte,
/// 0 is a two-byte copy whose first byte and the high nibble of the second give a 12-bit absolute ring
/// position, and whose low nibble gives a length of 3-18. The ring buffer starts with a fixed pattern
/// (13 copies of each byte value, the values ascending then descending, 128 zeros, 110 spaces and 18
/// zeros) and the write position starts 18 bytes before its end.</para>
/// <para>Sourcing: converted from lhasa's <c>lz5_decoder.c</c> (ISC, Simon Howard), initial ring
/// contents copied exactly; see THIRD-PARTY-NOTICE.lhasa.txt beside this file. Checked against the
/// <c>-lz5-</c> archives of lhasa's test suite.</para>
/// </remarks>
public static class Lz5Decoder {
  internal const int RingSize = 4096;
  internal const int StartOffset = 18;
  internal const int Threshold = 3;

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-lz5-</c> data.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize)
    => Decode(compressed.ToArray(), originalSize);

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-lz5-</c> data.</summary>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    ArgumentNullException.ThrowIfNull(compressed);
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    var output = new byte[originalSize];
    var ring = InitialRing();
    var ringPos = RingSize - StartOffset;
    var src = 0;
    var outPos = 0;

    while (outPos < originalSize) {
      if (src >= compressed.Length)
        throw new InvalidDataException("-lz5- stream ends before the member is complete.");
      int flags = compressed[src++];
      for (var bit = 0; bit < 8 && outPos < originalSize; ++bit) {
        if ((flags & (1 << bit)) != 0) {
          if (src >= compressed.Length)
            throw new InvalidDataException("-lz5- stream ends inside a literal.");
          output[outPos++] = ring[ringPos] = compressed[src++];
          ringPos = (ringPos + 1) % RingSize;
          continue;
        }
        if (src + 1 >= compressed.Length)
          throw new InvalidDataException("-lz5- stream ends inside a copy.");
        int c0 = compressed[src++];
        int c1 = compressed[src++];
        var pos = ((c1 & 0xF0) << 4) | c0;
        var length = (c1 & 0x0F) + Threshold;
        for (var i = 0; i < length && outPos < originalSize; ++i) {
          var b = ring[(pos + i) % RingSize];
          output[outPos++] = ring[ringPos] = b;
          ringPos = (ringPos + 1) % RingSize;
        }
      }
    }
    return output;
  }

  /// <summary>The ring buffer's initial contents, as LArc defines them.</summary>
  internal static byte[] InitialRing() {
    var ring = new byte[RingSize];
    var p = 0;
    for (var i = 0; i < 256; ++i)
      for (var j = 0; j < 13; ++j)
        ring[p++] = (byte)i;
    for (var i = 0; i < 256; ++i)
      ring[p++] = (byte)i;
    for (var i = 0; i < 256; ++i)
      ring[p++] = (byte)(255 - i);
    p += 128;                        // zeros
    for (var i = 0; i < 110; ++i)
      ring[p++] = (byte)' ';
    // the final 18 bytes stay zero
    return ring;
  }
}

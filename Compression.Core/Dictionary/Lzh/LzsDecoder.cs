namespace Compression.Core.Dictionary.Lzh;

/// <summary>
/// Decodes LArc <c>-lzs-</c>: LZSS over a 2 KiB ring buffer, with no entropy coding.
/// </summary>
/// <remarks>
/// <para>The stream is MSB-first bits. Each command opens with a flag bit: 1 is followed by an 8-bit
/// literal, 0 by an 11-bit absolute ring-buffer position and a 4-bit length (copies of 2-17 bytes).
/// The ring buffer starts filled with spaces and the write position starts 17 bytes before its end;
/// positions are absolute, not distances.</para>
/// <para>Sourcing: converted from lhasa's <c>lzs_decoder.c</c> (ISC, Simon Howard); see
/// THIRD-PARTY-NOTICE.lhasa.txt beside this file. Checked against the <c>-lzs-</c> archives of lhasa's
/// test suite.</para>
/// </remarks>
public static class LzsDecoder {
  internal const int RingSize = 2048;
  internal const int StartOffset = 17;
  internal const int Threshold = 2;

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-lzs-</c> data.</summary>
  public static byte[] Decode(ReadOnlySpan<byte> compressed, int originalSize)
    => Decode(compressed.ToArray(), originalSize);

  /// <summary>Decodes <paramref name="originalSize"/> bytes of <c>-lzs-</c> data.</summary>
  public static byte[] Decode(byte[] compressed, int originalSize) {
    ArgumentNullException.ThrowIfNull(compressed);
    ArgumentOutOfRangeException.ThrowIfNegative(originalSize);
    var output = new byte[originalSize];
    var ring = new byte[RingSize];
    Array.Fill(ring, (byte)' ');
    var ringPos = RingSize - StartOffset;
    var bits = new LhaBitInput(compressed);
    var outPos = 0;

    while (outPos < originalSize) {
      if (bits.Exhausted)
        throw new InvalidDataException("-lzs- stream ends before the member is complete.");
      if (bits.ReadBit() != 0) {
        var b = (byte)bits.ReadBits(8);
        output[outPos++] = ring[ringPos] = b;
        ringPos = (ringPos + 1) % RingSize;
        continue;
      }
      var pos = bits.ReadBits(11);
      var length = bits.ReadBits(4) + Threshold;
      for (var i = 0; i < length && outPos < originalSize; ++i) {
        var b = ring[(pos + i) % RingSize];
        output[outPos++] = ring[ringPos] = b;
        ringPos = (ringPos + 1) % RingSize;
      }
    }
    return output;
  }
}

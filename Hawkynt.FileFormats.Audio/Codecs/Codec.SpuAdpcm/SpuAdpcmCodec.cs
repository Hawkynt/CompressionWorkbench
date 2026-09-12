#pragma warning disable CS1591
namespace Codec.SpuAdpcm;

/// <summary>
/// Sony PlayStation SPU ADPCM (a.k.a. VAG / XA ADPCM) encoder and decoder. The PS1/PS2
/// Sound Processing Unit decodes audio in fixed 16-byte blocks, each yielding 28 mono
/// samples:
/// <list type="bullet">
///   <item>byte 0 — header: low nibble = <c>shift</c> (0..12), high nibble = <c>filter</c> (0..4).</item>
///   <item>byte 1 — flags (loop start / loop end / end markers); decoding processes every block.</item>
///   <item>bytes 2..15 — 28 signed 4-bit nibbles, low nibble of each byte first.</item>
/// </list>
/// Each sample is reconstructed as
/// <c>s = (signExtend4(nibble) &lt;&lt; 12) &gt;&gt; shift</c>, then a second-order predictor is
/// applied: <c>s += (hist1*K1[filter] + hist2*K2[filter] + 32) &gt;&gt; 6</c>, clamped to
/// <see cref="short"/>. The two histories then shift forward.
/// </summary>
public static class SpuAdpcmCodec {

  /// <summary>Predictor coefficient 1, indexed by filter (scaled by 1/64).</summary>
  private static readonly int[] FilterK1 = [0, 60, 115, 98, 122];

  /// <summary>Predictor coefficient 2, indexed by filter (scaled by 1/64).</summary>
  private static readonly int[] FilterK2 = [0, 0, -52, -55, -60];

  /// <summary>Number of PCM samples carried by one 16-byte SPU ADPCM block.</summary>
  public const int SamplesPerBlock = 28;

  /// <summary>Size in bytes of one SPU ADPCM block.</summary>
  public const int BlockSize = 16;

  /// <summary>
  /// Decodes a mono SPU ADPCM stream (a whole number of 16-byte blocks) to 16-bit PCM.
  /// Each full block yields <see cref="SamplesPerBlock"/> samples. A trailing partial
  /// block (shorter than <see cref="BlockSize"/>) is ignored.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data) {
    var blockCount = data.Length / BlockSize;
    var output = new short[blockCount * SamplesPerBlock];

    var hist1 = 0;
    var hist2 = 0;

    for (var b = 0; b < blockCount; ++b) {
      var blockStart = b * BlockSize;
      var header = data[blockStart];
      var shift = header & 0x0F;
      var filter = (header >> 4) & 0x0F;

      // Hardware clamps out-of-range coefficients to keep playback defined.
      if (shift > 12) shift = 12;
      if (filter > 4) filter = 4;

      var k1 = FilterK1[filter];
      var k2 = FilterK2[filter];

      for (var i = 0; i < SamplesPerBlock; ++i) {
        var nibble = (data[blockStart + 2 + (i >> 1)] >> ((i & 1) * 4)) & 0x0F;
        var s = SignExtend4(nibble) << 12;
        s >>= shift;
        s += (hist1 * k1 + hist2 * k2 + 32) >> 6;
        s = Clamp16(s);
        output[b * SamplesPerBlock + i] = (short)s;
        hist2 = hist1;
        hist1 = s;
      }
    }

    return output;
  }

  /// <summary>
  /// Encodes mono 16-bit PCM into SPU ADPCM blocks. Each group of
  /// <see cref="SamplesPerBlock"/> samples is encoded by brute-forcing every filter and
  /// every legal shift and keeping the combination with the lowest reconstruction error
  /// (the standard VAG encoder strategy). The final sample group is zero-padded to a full
  /// block; the last emitted block carries flags byte <c>0x01</c> (end marker), all others
  /// <c>0x00</c>.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var blockCount = (pcm.Length + SamplesPerBlock - 1) / SamplesPerBlock;
    if (blockCount == 0) return [];

    var output = new byte[blockCount * BlockSize];

    // History carried across blocks: the encoder must predict exactly as the decoder will,
    // so it tracks the already-emitted (reconstructed) samples.
    var hist1 = 0;
    var hist2 = 0;

    Span<short> sourceBlock = stackalloc short[SamplesPerBlock];
    Span<int> bestNibbles = stackalloc int[SamplesPerBlock];
    Span<int> tryNibbles = stackalloc int[SamplesPerBlock];

    for (var b = 0; b < blockCount; ++b) {
      // Gather (and zero-pad) this block's source samples.
      var srcStart = b * SamplesPerBlock;
      for (var i = 0; i < SamplesPerBlock; ++i) {
        var idx = srcStart + i;
        sourceBlock[i] = idx < pcm.Length ? pcm[idx] : (short)0;
      }

      var bestError = long.MaxValue;
      var bestFilter = 0;
      var bestShift = 0;
      var bestHist1 = hist1;
      var bestHist2 = hist2;

      for (var filter = 0; filter < FilterK1.Length; ++filter) {
        var k1 = FilterK1[filter];
        var k2 = FilterK2[filter];
        for (var shift = 0; shift <= 12; ++shift) {
          var h1 = hist1;
          var h2 = hist2;
          long error = 0;

          for (var i = 0; i < SamplesPerBlock; ++i) {
            var predicted = (h1 * k1 + h2 * k2 + 32) >> 6;
            var residual = sourceBlock[i] - predicted;

            // Quantise the residual into a signed 4-bit nibble at this shift.
            var quant = (residual << shift) + (1 << 11); // round
            quant >>= 12;
            if (quant > 7) quant = 7;
            else if (quant < -8) quant = -8;
            tryNibbles[i] = quant & 0x0F;

            // Reconstruct exactly as the decoder would, to track real error + history.
            var s = SignExtend4(quant & 0x0F) << 12;
            s >>= shift;
            s += predicted;
            s = Clamp16(s);

            var diff = (long)s - sourceBlock[i];
            error += diff * diff;

            h2 = h1;
            h1 = s;
          }

          if (error >= bestError)
            continue;

          bestError = error;
          bestFilter = filter;
          bestShift = shift;
          bestHist1 = h1;
          bestHist2 = h2;
          tryNibbles.CopyTo(bestNibbles);
          if (error == 0) break; // exact fit; no better shift for this filter
        }
      }

      var blockStart = b * BlockSize;
      output[blockStart] = (byte)((bestFilter << 4) | bestShift);
      output[blockStart + 1] = (byte)(b == blockCount - 1 ? 0x01 : 0x00);
      for (var i = 0; i < SamplesPerBlock; i += 2)
        output[blockStart + 2 + (i >> 1)] = (byte)(bestNibbles[i] | (bestNibbles[i + 1] << 4));

      hist1 = bestHist1;
      hist2 = bestHist2;
    }

    return output;
  }

  /// <summary>Sign-extends a 4-bit value (0..15) to the full signed range -8..7.</summary>
  private static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;

  private static int Clamp16(int value) => value > 32767 ? 32767 : value < -32768 ? -32768 : value;
}

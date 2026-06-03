#pragma warning disable CS1591
namespace Codec.AicaAdpcm;

/// <summary>
/// Yamaha AICA 4-bit ADPCM (Sega Dreamcast sound chip; the same quantiser as the
/// YM2608 ADPCM-B family).
/// <para>
/// A purely differential codec of the OKI / Dialogic lineage: every 4-bit nibble
/// carries a 3-bit delta magnitude plus a sign bit (bit 3), and a per-sample
/// quantiser <c>step</c> walks a multiplicative adaptation table rather than the
/// 49-entry index table OKI uses. Per nibble the decoder computes
/// <c>diff = ((nibble &amp; 7) * 2 + 1) * step / 8</c> (an integer right-shift by 3),
/// adds or subtracts it from a full 16-bit predictor (sign bit 3), clamps the
/// predictor to <c>[-32768, 32767]</c>, then advances the step by
/// <c>step = step * rate[nibble &amp; 7] / 256</c>, clamped to <c>[127, 24576]</c>.
/// </para>
/// <para>
/// The bitstream packs two samples per byte, <b>LOW nibble first</b> then the HIGH
/// nibble — the AICA / ADPCM-B convention. Decoding begins from predictor 0 and
/// step 127.
/// </para>
/// </summary>
public static class AicaAdpcmCodec {

  // Multiplicative step-adaptation table indexed by the 3-bit magnitude (canonical AICA).
  private static readonly int[] StepRate = [230, 230, 230, 230, 307, 409, 512, 614];

  private const int StepMin = 127;
  private const int StepMax = 24576;
  private const int InitialStep = 127;

  /// <summary>
  /// Decodes a mono AICA ADPCM byte stream to 16-bit PCM. Each input byte yields two
  /// samples (low nibble first), so the output holds <c>data.Length * 2</c> samples.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data) {
    var output = new short[data.Length * 2];
    var predictor = 0;
    var step = InitialStep;
    var o = 0;
    foreach (var b in data) {
      output[o++] = DecodeNibble((byte)(b & 0x0F), ref predictor, ref step);
      output[o++] = DecodeNibble((byte)(b >> 4), ref predictor, ref step);
    }
    return output;
  }

  /// <summary>
  /// Encodes 16-bit PCM to a mono AICA ADPCM byte stream using the same state machine
  /// as <see cref="Decode"/>, so round-tripping reproduces the waveform within the
  /// codec's lossy tolerance. Two samples pack into each byte (low nibble first); an
  /// odd trailing sample is paired with a zero (silence) high nibble.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var output = new byte[(pcm.Length + 1) / 2];
    var predictor = 0;
    var step = InitialStep;
    for (var i = 0; i < pcm.Length; i += 2) {
      var lo = EncodeNibble(pcm[i], ref predictor, ref step);
      var hi = i + 1 < pcm.Length ? EncodeNibble(pcm[i + 1], ref predictor, ref step) : (byte)0;
      output[i / 2] = (byte)((hi << 4) | lo);
    }
    return output;
  }

  private static short DecodeNibble(byte nibble, ref int predictor, ref int step) {
    var magnitude = nibble & 0x07;
    var diff = ((2 * magnitude + 1) * step) >> 3;
    if ((nibble & 8) != 0) predictor -= diff;
    else predictor += diff;
    predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
    step = Math.Clamp((step * StepRate[magnitude]) >> 8, StepMin, StepMax);
    return (short)predictor;
  }

  private static byte EncodeNibble(short sample, ref int predictor, ref int step) {
    var diff = sample - predictor;

    byte nibble = 0;
    if (diff < 0) {
      nibble = 8;
      diff = -diff;
    }
    // Greedily pick the 3-bit magnitude whose reconstruction diff is closest from below.
    // Magnitude m reconstructs (2*m+1)*step/8; choose the largest m whose diff fits.
    var magnitude = 0;
    for (var m = 7; m >= 0; --m) {
      if (((2 * m + 1) * step) >> 3 <= diff) {
        magnitude = m;
        break;
      }
    }
    nibble |= (byte)magnitude;

    // Advance the shared state exactly as the decoder will, so encode/decode stay in lockstep.
    DecodeNibble(nibble, ref predictor, ref step);
    return nibble;
  }
}

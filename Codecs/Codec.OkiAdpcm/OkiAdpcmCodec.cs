#pragma warning disable CS1591
namespace Codec.OkiAdpcm;

/// <summary>
/// OKI / Dialogic VOX 4-bit ADPCM (MSM6258 / MSM6585 family).
/// <para>
/// A purely differential codec: each 4-bit nibble carries the sign + magnitude of
/// the delta to the next sample, with the quantiser step walking a 49-entry table.
/// Unlike IMA ADPCM the predictor is a 12-bit value clamped to
/// <c>[-2048, 2047]</c>; samples are scaled to 16-bit on output by shifting left 4
/// bits. The bitstream packs two samples per byte, <b>HIGH nibble first</b> then the
/// LOW nibble — the convention used by Dialogic <c>.vox</c> files.
/// </para>
/// <para>
/// The delta is derived from the canonical OKI accumulation:
/// <c>e = step/8; if(b2) e += step/4; if(b1) e += step/2; if(b0) e += step</c>,
/// added to or subtracted from the predictor by the sign bit (b3). Equivalent to
/// <c>e = step * ((nibble&amp;7)*2 + 1) / 8</c>.
/// </para>
/// </summary>
public static class OkiAdpcmCodec {

  private static readonly int[] StepTable = [
    16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66,
    73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
    337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411,
    1552
  ];

  private static readonly int[] IndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8];

  private const int MaxStepIndex = 48;
  private const int PredictorMin = -2048;
  private const int PredictorMax = 2047;

  /// <summary>
  /// Decodes a mono VOX ADPCM byte stream to 16-bit PCM. Each input byte yields two
  /// samples (high nibble first), so the output holds <c>data.Length * 2</c> samples.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data) {
    var output = new short[data.Length * 2];
    var predictor = 0;
    var index = 0;
    var o = 0;
    foreach (var b in data) {
      output[o++] = DecodeNibble((byte)(b >> 4), ref predictor, ref index);
      output[o++] = DecodeNibble((byte)(b & 0x0F), ref predictor, ref index);
    }
    return output;
  }

  /// <summary>
  /// Encodes 16-bit PCM to a mono VOX ADPCM byte stream using the same state machine
  /// as <see cref="Decode"/>, so round-tripping reproduces the waveform within the
  /// codec's lossy tolerance. Two samples pack into each byte (high nibble first); an
  /// odd trailing sample is paired with a zero (silence) low nibble.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var output = new byte[(pcm.Length + 1) / 2];
    var predictor = 0;
    var index = 0;
    for (var i = 0; i < pcm.Length; i += 2) {
      var hi = EncodeNibble(pcm[i], ref predictor, ref index);
      var lo = i + 1 < pcm.Length ? EncodeNibble(pcm[i + 1], ref predictor, ref index) : (byte)0;
      output[i / 2] = (byte)((hi << 4) | lo);
    }
    return output;
  }

  private static short DecodeNibble(byte nibble, ref int predictor, ref int index) {
    var step = StepTable[index];
    var delta = step >> 3;
    if ((nibble & 4) != 0) delta += step;
    if ((nibble & 2) != 0) delta += step >> 1;
    if ((nibble & 1) != 0) delta += step >> 2;
    if ((nibble & 8) != 0) predictor -= delta;
    else predictor += delta;
    predictor = Math.Clamp(predictor, PredictorMin, PredictorMax);
    index = Math.Clamp(index + IndexAdjust[nibble & 0x07], 0, MaxStepIndex);
    return (short)(predictor << 4);
  }

  private static byte EncodeNibble(short sample, ref int predictor, ref int index) {
    // Target predictor value lives in the 12-bit domain (sample is 16-bit).
    var target = sample >> 4;
    var step = StepTable[index];
    var diff = target - predictor;

    byte nibble = 0;
    if (diff < 0) {
      nibble = 8;
      diff = -diff;
    }
    // Greedily build the magnitude nibble from the same step fractions the decoder uses.
    if (diff >= step) { nibble |= 4; diff -= step; }
    if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; }
    if (diff >= step >> 2) nibble |= 1;

    // Advance the shared state exactly as the decoder will, so encode/decode stay in lockstep.
    DecodeNibble(nibble, ref predictor, ref index);
    return nibble;
  }
}

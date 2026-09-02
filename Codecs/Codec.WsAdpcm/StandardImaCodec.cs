#pragma warning disable CS1591
namespace Codec.WsAdpcm;

/// <summary>
/// Standard IMA ADPCM with a single, continuous predictor/step-index state — the form
/// used by Westwood <c>.aud</c> (codec id 99) and CRYO <c>.apc</c> streams, where the
/// adaptive state runs across the whole stream rather than resetting per WAV block.
/// Nibbles are read <b>low nibble first</b> within each byte, matching both formats.
/// <para>
/// This complements <c>Codec.ImaAdpcm.ImaAdpcmCodec</c>, whose public surface only
/// covers the block-structured WAV and QuickTime packet layouts. The encoder mirrors
/// the decoder's state machine exactly so a decode→encode→decode round-trip reproduces
/// the waveform within IMA's lossy tolerance. It lives here, alongside the other
/// classic-game audio codecs, so both <c>FileFormat.Aud</c> and <c>FileFormat.Apc</c>
/// can share one streaming IMA implementation.
/// </para>
/// </summary>
public static class StandardImaCodec {

  private static readonly int[] StepTable = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31,
    34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
    157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658,
    724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024,
    3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
    15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
  ];

  private static readonly int[] IndexAdjust = [-1, -1, -1, -1, 2, 4, 6, 8];

  /// <summary>Mutable IMA decoder/encoder state (predictor + step index).</summary>
  public struct State {
    /// <summary>
    /// Provides the predictor value.
    /// </summary>
    public int Predictor;
    /// <summary>
    /// Provides the step index value.
    /// </summary>
    public int StepIndex;

    /// <summary>
    /// Initializes a new instance of <see cref="State"/>.
    /// </summary>
    public State(int predictor, int stepIndex) {
      this.Predictor = predictor;
      this.StepIndex = Math.Clamp(stepIndex, 0, 88);
    }
  }

  /// <summary>
  /// Decodes a continuous IMA byte stream (two nibbles per byte, low nibble first) into
  /// signed 16-bit PCM, advancing <paramref name="state"/> across the whole buffer.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, ref State state) {
    var output = new short[data.Length * 2];
    var o = 0;
    foreach (var b in data) {
      output[o++] = DecodeNibble((byte)(b & 0x0F), ref state);
      output[o++] = DecodeNibble((byte)(b >> 4), ref state);
    }
    return output;
  }

  /// <summary>
  /// Encodes signed 16-bit PCM into a continuous IMA byte stream (low nibble first),
  /// advancing <paramref name="state"/>. An odd trailing sample is paired with a zero
  /// high nibble.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm, ref State state) {
    var output = new byte[(pcm.Length + 1) / 2];
    for (var i = 0; i < pcm.Length; i += 2) {
      var lo = EncodeNibble(pcm[i], ref state);
      var hi = i + 1 < pcm.Length ? EncodeNibble(pcm[i + 1], ref state) : (byte)0;
      output[i / 2] = (byte)((hi << 4) | lo);
    }
    return output;
  }

  /// <summary>
  /// Decodes a single IMA nibble against <paramref name="state"/>, returning the new
  /// 16-bit sample. Exposed for stereo streams that interleave nibbles per channel
  /// (e.g. CRYO APC: low nibble left, high nibble right) and so need per-nibble control.
  /// </summary>
  public static short DecodeOneNibble(byte nibble, ref State state) => DecodeNibble(nibble, ref state);

  private static short DecodeNibble(byte nibble, ref State state) {
    var step = StepTable[state.StepIndex];
    var diff = step >> 3;
    if ((nibble & 1) != 0) diff += step >> 2;
    if ((nibble & 2) != 0) diff += step >> 1;
    if ((nibble & 4) != 0) diff += step;
    if ((nibble & 8) != 0) state.Predictor -= diff;
    else state.Predictor += diff;
    state.Predictor = Math.Clamp(state.Predictor, -32768, 32767);
    state.StepIndex = Math.Clamp(state.StepIndex + IndexAdjust[nibble & 0x07], 0, 88);
    return (short)state.Predictor;
  }

  private static byte EncodeNibble(short sample, ref State state) {
    var step = StepTable[state.StepIndex];
    var diff = sample - state.Predictor;

    byte nibble = 0;
    if (diff < 0) {
      nibble = 8;
      diff = -diff;
    }
    var temp = step;
    if (diff >= temp) { nibble |= 4; diff -= temp; }
    temp >>= 1;
    if (diff >= temp) { nibble |= 2; diff -= temp; }
    temp >>= 1;
    if (diff >= temp) nibble |= 1;

    // Advance the shared state exactly as the decoder will.
    DecodeNibble(nibble, ref state);
    return nibble;
  }
}

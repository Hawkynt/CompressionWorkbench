#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// Gremlin DPCM (ffmpeg <c>gremlin_dpcm</c> in <c>libavcodec/dpcm.c</c>, the audio in Gremlin
/// Interactive game streams). Each payload byte indexes a 256-entry signed delta table that is
/// added to the running per-channel predictor; channels are interleaved byte-by-byte. The table is
/// generated procedurally (the same loop ffmpeg uses) rather than stored literally: odd indices
/// hold the growing positive delta and even indices its negation.
/// </summary>
public static class Gremlin {

  /// <summary>
  /// The 256-entry delta table, built by the ffmpeg generator
  /// (<c>delta += code &gt;&gt; 5; code += step; step += 2</c>; positive at odd indices, negated at
  /// even). Entry 0 is zero and entry 255 carries the final extrapolated delta.
  /// </summary>
  public static readonly short[] DeltaTable = BuildTable();

  private static short[] BuildTable() {
    var table = new short[256];
    var delta = 0;
    var code = 64;
    var step = 45;
    table[0] = 0;
    for (var i = 0; i < 127; ++i) {
      delta += code >> 5;
      code += step;
      step += 2;
      table[i * 2 + 1] = (short)delta;
      table[i * 2 + 2] = (short)-delta;
    }
    table[255] = (short)(delta + (code >> 5));
    return table;
  }

  /// <summary>
  /// Decodes a Gremlin DPCM payload into interleaved PCM16. <paramref name="channels"/> is 1 or 2;
  /// each channel keeps its own predictor and they are interleaved byte-by-byte.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("Gremlin DPCM supports 1 or 2 channels.", nameof(channels));

    var predictor = new int[channels];
    var output = new short[data.Length];
    var ch = 0;
    for (var i = 0; i < data.Length; ++i) {
      predictor[ch] = ImaCore.Clamp16(predictor[ch] + DeltaTable[data[i]]);
      output[i] = (short)predictor[ch];
      ch = channels == 1 ? 0 : ch ^ 1;
    }

    return output;
  }
}

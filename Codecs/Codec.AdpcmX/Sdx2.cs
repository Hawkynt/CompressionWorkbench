#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// SDX2 DPCM (ffmpeg <c>sdx2_dpcm</c> in <c>libavcodec/dpcm.c</c>, the "square 2" coding in some
/// SDX/3DO streams). Each payload byte is a signed value <c>n</c>: the magnitude is squared and
/// doubled (sign preserved) to form a delta. The low bit of the byte is also a reset flag — when it
/// is clear the running predictor is zeroed before the delta is applied (an absolute, rather than
/// differential, sample). Channels are interleaved byte-by-byte.
/// </summary>
public static class Sdx2 {

  /// <summary>
  /// The 256-entry square-double delta table (ffmpeg builds it as <c>square = i*i*2</c>, negated
  /// for negative <c>i</c>). Index by the signed byte value plus 128.
  /// </summary>
  public static readonly short[] SquareTable = BuildTable();

  private static short[] BuildTable() {
    var table = new short[256];
    for (var i = -128; i < 128; ++i) {
      var square = i * i * 2;
      table[i + 128] = (short)(i < 0 ? -square : square);
    }
    return table;
  }

  /// <summary>
  /// Decodes an SDX2 payload into interleaved PCM16. <paramref name="channels"/> is 1 or 2;
  /// channels are interleaved byte-by-byte and each carries its own predictor.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("SDX2 supports 1 or 2 channels.", nameof(channels));

    var predictor = new int[channels];
    var output = new short[data.Length];
    var ch = 0;
    for (var i = 0; i < data.Length; ++i) {
      var n = (sbyte)data[i];
      if ((n & 1) == 0)
        predictor[ch] = 0; // even code → absolute sample
      predictor[ch] = ImaCore.Clamp16(predictor[ch] + SquareTable[n + 128]);
      output[i] = (short)predictor[ch];
      ch = channels == 1 ? 0 : ch ^ 1;
    }

    return output;
  }
}

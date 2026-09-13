#pragma warning disable CS1591
namespace Codec.AdpcmX;

/// <summary>
/// Xan DPCM (ffmpeg <c>xan_dpcm</c> in <c>libavcodec/dpcm.c</c>, the audio in Wing Commander III/IV
/// <c>.xan</c> movies). It is a shift-based differential scheme with a per-channel adaptive shift
/// that starts at 4. Each payload byte is one delta for the current channel: the low two bits
/// <c>n</c> adjust the shift (<c>n == 3</c> increments it, otherwise it decreases by <c>2*n</c>,
/// clamped to an unsigned 5-bit range), and the byte's upper bits — sign-extended after being
/// shifted left by 8 — are arithmetically shifted right by the (post-adjust) shift to form the
/// delta added to the running predictor. Channels are interleaved byte-by-byte.
/// </summary>
public static class XanDpcm {

  /// <summary>
  /// Decodes a Xan DPCM payload into interleaved PCM16. <paramref name="channels"/> is 1 or 2;
  /// <paramref name="startPredictors"/> seeds each channel's predictor (the container carries the
  /// initial samples out of band).
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels, ReadOnlySpan<int> startPredictors) {
    if (channels is < 1 or > 2)
      throw new ArgumentException("Xan DPCM supports 1 or 2 channels.", nameof(channels));
    if (startPredictors.Length < channels)
      throw new ArgumentException("Need a start predictor per channel.", nameof(startPredictors));

    var predictor = new int[channels];
    var shift = new int[channels];
    for (var c = 0; c < channels; ++c) {
      predictor[c] = startPredictors[c];
      shift[c] = 4;
    }

    var output = new short[data.Length];
    var ch = 0;
    for (var i = 0; i < data.Length; ++i) {
      int diff = data[i];
      var n = diff & 3;
      if (n == 3)
        ++shift[ch];
      else
        shift[ch] -= 2 * n;
      if (shift[ch] < 0) shift[ch] = 0;
      else if (shift[ch] > 31) shift[ch] = 31; // av_clip_uintp2(shift, 5)

      // sign_extend((diff & ~3) << 8, 16) then arithmetic >> shift
      diff = (short)((diff & ~3) << 8);
      diff >>= shift[ch];
      predictor[ch] = ImaCore.Clamp16(predictor[ch] + diff);
      output[i] = (short)predictor[ch];

      ch = channels == 1 ? 0 : ch ^ 1;
    }

    return output;
  }
}

#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.EaXa;

/// <summary>
/// Electronic Arts EA-XA ADPCM (the "EA XA" / XAS-style block scheme carried by EA SCHl
/// streams) encoder and decoder. Audio is stored as fixed 15-byte frames, one per channel,
/// interleaved frame-by-frame (channel 0's frame, channel 1's frame, …). Each frame yields
/// <see cref="SamplesPerFrame"/> samples for its channel:
/// <list type="bullet">
///   <item>byte 0 — header: high nibble = <c>coefIndex</c> (0..3), low nibble = <c>shift</c>
///         (0..12). The reserved header value <c>0xEE</c> marks an uncompressed frame.</item>
///   <item>bytes 1..14 — 28 signed 4-bit nibbles, HIGH nibble of each byte first.</item>
/// </list>
/// Each sample is reconstructed as
/// <c>prediction = (hist1*K0[coef] + hist2*K1[coef]) &gt;&gt; 8</c>, then
/// <c>s = clamp16(prediction + (signExtend4(nibble) &lt;&lt; (12 - shift)))</c>, and the two
/// histories shift forward. An <c>0xEE</c> frame instead carries 14 bytes that begin a run
/// of raw 16-bit big-endian samples; on decode the histories track the last two raw samples.
/// </summary>
public static class EaXaCodec {

  /// <summary>Predictor coefficient 0, indexed by coefIndex (scaled by 1/256).</summary>
  private static readonly int[] CoefK0 = [0, 240, 460, 392];

  /// <summary>Predictor coefficient 1, indexed by coefIndex (scaled by 1/256).</summary>
  private static readonly int[] CoefK1 = [0, 0, -208, -220];

  /// <summary>Number of PCM samples carried by one 15-byte EA-XA frame.</summary>
  public const int SamplesPerFrame = 28;

  /// <summary>Size in bytes of one EA-XA frame.</summary>
  public const int FrameSize = 15;

  /// <summary>Header value flagging an uncompressed (raw 16-bit BE) frame.</summary>
  public const byte RawFrameMarker = 0xEE;

  /// <summary>
  /// Decodes an EA-XA stream (channel-interleaved 15-byte frames) into interleaved 16-bit
  /// PCM. Frames are consumed channel-by-channel: for <paramref name="channels"/> = 2 the
  /// layout is [ch0 frame][ch1 frame][ch0 frame]… and the output weaves the per-channel
  /// samples back together (L,R,L,R…). A trailing partial group of frames is ignored.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data, int channels) {
    if (channels < 1)
      throw new ArgumentException("EA-XA needs at least one channel.", nameof(channels));

    var groupBytes = FrameSize * channels;
    var groups = data.Length / groupBytes;
    var output = new short[groups * SamplesPerFrame * channels];

    var hist1 = new int[channels];
    var hist2 = new int[channels];

    for (var g = 0; g < groups; ++g) {
      for (var c = 0; c < channels; ++c) {
        var frameStart = g * groupBytes + c * FrameSize;
        var header = data[frameStart];

        if (header == RawFrameMarker) {
          // Uncompressed frame: 14 bytes carry 7 raw 16-bit BE samples; the remaining
          // 21 samples of the frame are decoded as zero-delta against the running history.
          for (var i = 0; i < SamplesPerFrame; ++i) {
            int s;
            if (i < 7) {
              s = BinaryPrimitives.ReadInt16BigEndian(data.Slice(frameStart + 1 + i * 2, 2));
            } else {
              s = hist1[c];
            }
            output[(g * SamplesPerFrame + i) * channels + c] = (short)s;
            hist2[c] = hist1[c];
            hist1[c] = s;
          }
          continue;
        }

        var coef = (header >> 4) & 0x0F;
        var shift = header & 0x0F;
        if (coef > 3) coef = 3;
        if (shift > 12) shift = 12;
        var k0 = CoefK0[coef];
        var k1 = CoefK1[coef];

        for (var i = 0; i < SamplesPerFrame; ++i) {
          var nibble = (data[frameStart + 1 + (i >> 1)] >> ((i & 1) == 0 ? 4 : 0)) & 0x0F;
          var prediction = (hist1[c] * k0 + hist2[c] * k1) >> 8;
          var s = Clamp16(prediction + (SignExtend4(nibble) << (12 - shift)));
          output[(g * SamplesPerFrame + i) * channels + c] = (short)s;
          hist2[c] = hist1[c];
          hist1[c] = s;
        }
      }
    }

    return output;
  }

  /// <summary>
  /// Encodes interleaved 16-bit PCM into channel-interleaved EA-XA frames. Each channel's
  /// 28-sample frame is encoded by brute-forcing every coefficient index and every legal
  /// shift and keeping the combination with the lowest reconstruction error (mirroring the
  /// decoder's prediction exactly, so the histories stay in lockstep). The final group is
  /// zero-padded to a whole frame per channel.
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, int channels) {
    if (channels < 1)
      throw new ArgumentException("EA-XA needs at least one channel.", nameof(channels));
    if (interleaved.Length % channels != 0)
      throw new ArgumentException("Interleaved sample count must be a multiple of the channel count.", nameof(interleaved));

    var framesPerChannel = interleaved.Length / channels;
    var groups = (framesPerChannel + SamplesPerFrame - 1) / SamplesPerFrame;
    if (groups == 0) return [];

    var groupBytes = FrameSize * channels;
    var output = new byte[groups * groupBytes];

    var hist1 = new int[channels];
    var hist2 = new int[channels];

    Span<short> source = stackalloc short[SamplesPerFrame];
    Span<int> bestNibbles = stackalloc int[SamplesPerFrame];
    Span<int> tryNibbles = stackalloc int[SamplesPerFrame];

    for (var g = 0; g < groups; ++g) {
      for (var c = 0; c < channels; ++c) {
        // Gather (and zero-pad) this frame's source samples for channel c.
        for (var i = 0; i < SamplesPerFrame; ++i) {
          var sampleIndex = g * SamplesPerFrame + i;
          source[i] = sampleIndex < framesPerChannel
            ? interleaved[sampleIndex * channels + c]
            : (short)0;
        }

        var bestError = long.MaxValue;
        var bestCoef = 0;
        var bestShift = 0;
        var bestHist1 = hist1[c];
        var bestHist2 = hist2[c];

        for (var coef = 0; coef < CoefK0.Length; ++coef) {
          var k0 = CoefK0[coef];
          var k1 = CoefK1[coef];
          for (var shift = 0; shift <= 12; ++shift) {
            var h1 = hist1[c];
            var h2 = hist2[c];
            long error = 0;

            for (var i = 0; i < SamplesPerFrame; ++i) {
              var prediction = (h1 * k0 + h2 * k1) >> 8;
              var residual = source[i] - prediction;

              // Quantise the residual into a signed 4-bit nibble at this shift.
              var quant = SymmetricShiftRight(residual, 12 - shift);
              if (quant > 7) quant = 7;
              else if (quant < -8) quant = -8;
              tryNibbles[i] = quant & 0x0F;

              var s = Clamp16(prediction + (SignExtend4(quant & 0x0F) << (12 - shift)));
              var diff = (long)s - source[i];
              error += diff * diff;

              h2 = h1;
              h1 = s;
            }

            if (error >= bestError)
              continue;

            bestError = error;
            bestCoef = coef;
            bestShift = shift;
            bestHist1 = h1;
            bestHist2 = h2;
            tryNibbles.CopyTo(bestNibbles);
            if (error == 0) break; // exact fit
          }
        }

        var frameStart = g * groupBytes + c * FrameSize;
        output[frameStart] = (byte)((bestCoef << 4) | bestShift);
        for (var i = 0; i < SamplesPerFrame; i += 2)
          output[frameStart + 1 + (i >> 1)] = (byte)((bestNibbles[i] << 4) | bestNibbles[i + 1]);

        hist1[c] = bestHist1;
        hist2[c] = bestHist2;
      }
    }

    return output;
  }

  /// <summary>Sign-extends a 4-bit value (0..15) to the full signed range -8..7.</summary>
  private static int SignExtend4(int nibble) => (nibble & 0x08) != 0 ? nibble - 16 : nibble;

  private static int Clamp16(int value) => value > 32767 ? 32767 : value < -32768 ? -32768 : value;

  /// <summary>
  /// Rounds <paramref name="value"/> divided by <c>2^shift</c> to the nearest integer using
  /// symmetric (round-half-away-from-zero) rounding, so positive and negative residuals
  /// quantise the same way (a plain arithmetic shift would bias negatives downward).
  /// </summary>
  private static int SymmetricShiftRight(int value, int shift) {
    if (shift <= 0) return value;
    var half = 1 << (shift - 1);
    return value >= 0 ? (value + half) >> shift : -((-value + half) >> shift);
  }
}

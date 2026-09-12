#pragma warning disable CS1591
namespace Codec.DspAdpcm;

/// <summary>
/// Nintendo GameCube/Wii DSP ADPCM (4-bit) codec. This is the canonical scheme used by
/// <c>.dsp</c>, <c>.brstm</c>, <c>.bcstm</c> and the rest of Nintendo's audio containers.
/// Each channel carries its own table of eight predictor coefficient pairs
/// (<c>short[16]</c>) supplied by the container; the codec itself only walks frames.
/// <para>
/// Data is a stream of 8-byte frames. Byte 0 is the frame header: the high nibble is the
/// predictor index (0..7, selecting <c>coefs[2*p]</c>/<c>coefs[2*p+1]</c>) and the low nibble
/// is the scale/shift. Bytes 1..7 hold 14 signed 4-bit samples, HIGH nibble first. Each
/// sample is reconstructed as
/// <c>out = (((nibble &lt;&lt; shift) &lt;&lt; 11) + coefs[2*p]*hist1 + coefs[2*p+1]*hist2 + 1024) &gt;&gt; 11</c>,
/// clamped to <see cref="short"/>, then fed back as history.
/// </para>
/// </summary>
public static class DspAdpcmCodec {

  /// <summary>Bytes per encoded frame (1 header + 7 data bytes = 14 samples).</summary>
  public const int BytesPerFrame = 8;

  /// <summary>Decoded samples carried by one frame.</summary>
  public const int SamplesPerFrame = 14;

  /// <summary>
  /// Decodes DSP ADPCM to PCM16. <paramref name="coefs"/> is the channel's eight predictor
  /// pairs (<c>short[16]</c>). At most <paramref name="sampleCount"/> samples are returned;
  /// the final partial frame is truncated rather than padded.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> adpcm, ReadOnlySpan<short> coefs, int sampleCount) {
    if (coefs.Length < 16)
      throw new ArgumentException("DSP ADPCM needs 8 predictor pairs (short[16]).", nameof(coefs));
    if (sampleCount < 0)
      throw new ArgumentOutOfRangeException(nameof(sampleCount));

    var output = new short[sampleCount];
    var produced = 0;
    var hist1 = 0;
    var hist2 = 0;
    var pos = 0;

    while (produced < sampleCount && pos + BytesPerFrame <= adpcm.Length) {
      var header = adpcm[pos];
      var predictor = (header >> 4) & 0x0F;
      var shift = header & 0x0F;
      if (predictor > 7) predictor = 7; // defensive: spec-valid frames use 0..7
      var c1 = coefs[2 * predictor];
      var c2 = coefs[2 * predictor + 1];

      for (var b = 0; b < 7 && produced < sampleCount; ++b) {
        var dataByte = adpcm[pos + 1 + b];
        for (var n = 0; n < 2 && produced < sampleCount; ++n) {
          // HIGH nibble first.
          var nibble = n == 0 ? (dataByte >> 4) & 0x0F : dataByte & 0x0F;
          var s = nibble >= 8 ? nibble - 16 : nibble; // sign-extend 4 bits
          var predicted = ((s << shift) << 11) + c1 * hist1 + c2 * hist2;
          var sample = (predicted + 1024) >> 11;
          if (sample > short.MaxValue) sample = short.MaxValue;
          else if (sample < short.MinValue) sample = short.MinValue;
          output[produced++] = (short)sample;
          hist2 = hist1;
          hist1 = sample;
        }
      }
      pos += BytesPerFrame;
    }

    return output;
  }

  /// <summary>
  /// Encodes PCM16 to DSP ADPCM. The encoder derives ONE predictor pair from the signal via
  /// the standard normal-equation fit of a second-order predictor, zeroes the remaining seven
  /// pairs, and brute-forces the per-frame scale (and nibbles) that minimise reconstruction
  /// error while staying numerically consistent with <see cref="Decode"/> (it decodes with the
  /// same history feedback so the returned buffer round-trips through this codec exactly).
  /// This is intentionally lossy — closeness, not bit-parity with Nintendo's encoder, is the bar.
  /// </summary>
  public static (byte[] Adpcm, short[] Coefs) Encode(ReadOnlySpan<short> pcm) {
    var coefs = new short[16];
    var (c1, c2) = FitPredictor(pcm);
    coefs[0] = c1;
    coefs[1] = c2;

    var frameCount = (pcm.Length + SamplesPerFrame - 1) / SamplesPerFrame;
    var adpcm = new byte[frameCount * BytesPerFrame];

    var hist1 = 0;
    var hist2 = 0;
    var outPos = 0;

    for (var f = 0; f < frameCount; ++f) {
      var start = f * SamplesPerFrame;
      var count = Math.Min(SamplesPerFrame, pcm.Length - start);

      var (bestShift, nibbles, endHist1, endHist2) = EncodeFrame(pcm.Slice(start, count), c1, c2, hist1, hist2);

      adpcm[outPos] = (byte)bestShift; // predictor index 0 in high nibble (0), shift in low nibble
      for (var i = 0; i < 14; ++i) {
        var nib = i < nibbles.Length ? nibbles[i] : 0;
        if ((i & 1) == 0)
          adpcm[outPos + 1 + i / 2] = (byte)((nib & 0x0F) << 4);
        else
          adpcm[outPos + 1 + i / 2] |= (byte)(nib & 0x0F);
      }

      hist1 = endHist1;
      hist2 = endHist2;
      outPos += BytesPerFrame;
    }

    return (adpcm, coefs);
  }

  // Brute-force the best scale + nibbles for one frame given fixed coefficients and entry history.
  private static (int Shift, int[] Nibbles, int Hist1, int Hist2) EncodeFrame(
      ReadOnlySpan<short> samples, short c1, short c2, int startHist1, int startHist2) {
    var bestShift = 0;
    long bestError = long.MaxValue;
    int[] bestNibbles = new int[samples.Length];
    var bestHist1 = startHist1;
    var bestHist2 = startHist2;

    for (var shift = 0; shift < 16; ++shift) {
      var hist1 = startHist1;
      var hist2 = startHist2;
      long error = 0;
      var nibbles = new int[samples.Length];

      for (var i = 0; i < samples.Length; ++i) {
        var predicted = c1 * hist1 + c2 * hist2;
        // Ideal residual that, after << shift << 11 + predicted, lands on the target sample.
        // target ≈ ((nib << shift) << 11 + predicted + 1024) >> 11
        // ⇒ nib << shift ≈ target - (predicted >> 11)
        var targetScaled = ((long)samples[i] << 11) - predicted;
        var ideal = targetScaled / ((long)(1 << shift) << 11);
        var nib = (int)Math.Clamp(ideal, -8, 7);

        var recon = ((nib << shift) << 11) + predicted;
        var sample = (int)((recon + 1024) >> 11);
        if (sample > short.MaxValue) sample = short.MaxValue;
        else if (sample < short.MinValue) sample = short.MinValue;

        var d = sample - samples[i];
        error += (long)d * d;
        nibbles[i] = nib & 0x0F;
        hist2 = hist1;
        hist1 = sample;
      }

      if (error < bestError) {
        bestError = error;
        bestShift = shift;
        bestNibbles = nibbles;
        bestHist1 = hist1;
        bestHist2 = hist2;
        if (error == 0) break;
      }
    }

    return (bestShift, bestNibbles, bestHist1, bestHist2);
  }

  // Second-order linear predictor via the normal equations (least squares).
  private static (short C1, short C2) FitPredictor(ReadOnlySpan<short> pcm) {
    if (pcm.Length < 3)
      return (0, 0);

    double r0 = 0, r1 = 0, r2 = 0, r11 = 0, r12 = 0, r22 = 0;
    for (var i = 2; i < pcm.Length; ++i) {
      double x = pcm[i];
      double x1 = pcm[i - 1];
      double x2 = pcm[i - 2];
      r11 += x1 * x1;
      r12 += x1 * x2;
      r22 += x2 * x2;
      r1 += x * x1;
      r2 += x * x2;
      r0 += x * x;
    }

    // Solve [[r11 r12];[r12 r22]] * [a1;a2] = [r1;r2].
    var det = r11 * r22 - r12 * r12;
    if (Math.Abs(det) < 1e-6)
      return (0, 0);

    var a1 = (r1 * r22 - r2 * r12) / det;
    var a2 = (r11 * r2 - r12 * r1) / det;

    // Coefficients are stored as Q11 fixed point (the decoder divides by 2048).
    var q1 = (int)Math.Round(a1 * 2048.0);
    var q2 = (int)Math.Round(a2 * 2048.0);
    q1 = Math.Clamp(q1, short.MinValue, short.MaxValue);
    q2 = Math.Clamp(q2, short.MinValue, short.MaxValue);
    return ((short)q1, (short)q2);
  }
}

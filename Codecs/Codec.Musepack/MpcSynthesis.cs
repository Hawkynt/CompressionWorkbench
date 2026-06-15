#pragma warning disable CS1591

namespace Codec.Musepack;

/// <summary>
/// 32-band polyphase synthesis filterbank shared by MPEG-1 Layer II and Musepack
/// (SV8 reuses the exact MP2 filter). This is a clean managed float port of
/// FFmpeg's <c>ff_dct32_float</c> + <c>ff_mpadsp_apply_window_float</c>
/// (<c>libavcodec/dct32_template.c</c>, <c>mpegaudiodsp_template.c</c>): a 32-point
/// DCT feeding the 512-tap prototype window built from <c>ff_mpa_enwindow</c>,
/// maintaining a 512-sample ring buffer per channel. Each call turns 32 subband
/// values into 32 PCM samples.
/// <para>
/// Note on reuse: <c>Codec.Mp3.Mp3Synthesis</c> implements the same filterbank but
/// is <c>internal</c> to that assembly and shaped around minimp3's 18-sample
/// granule stride and packed stereo layout, which does not fit MPC's
/// per-band-per-channel call pattern; the filterbank is therefore replicated here
/// against the canonical FFmpeg layout.
/// </para>
/// </summary>
internal sealed class MpcSynthesis {

  // Combined window/output scale. FFmpeg's float synthesis window is
  // ff_mpa_enwindow[i] / 2^(WFRAC_BITS + FRAC_BITS) = enwindow[i] / 2^(16+23),
  // and the dequantised subband samples enter at full int32 magnitude, so the
  // windowed sum lands directly in the signed-16-bit sample range.
  private const double WindowScale = 1.0 / (1L << (16 + 23));

  private static readonly float[] _Window = BuildWindow();

  // 512-sample ring buffer with a duplicated 512-sample tail so the windowing
  // inner loops can index up to 512 samples ahead of the moving offset without
  // any modulo, exactly as FFmpeg's 512*2 synth_buf does.
  private readonly float[] _synthBuf = new float[512 * 2];
  private int _offset;

  /// <summary>
  /// Synthesises 32 subband samples into 32 PCM samples written to
  /// <paramref name="outSamples"/> at <paramref name="outOffset"/> with stride
  /// <paramref name="incr"/> (1 for mono/per-channel buffers).
  /// </summary>
  public void Filter(float[] subbandSamples, short[] outSamples, int outOffset, int incr) {
    // Ring-buffer position decremented by 32 per call, wrapping at 512.
    var buf = this._synthBuf;
    Dct32(subbandSamples, buf, this._offset);
    ApplyWindow(buf, this._offset, outSamples, outOffset, incr);
    this._offset = (this._offset - 32) & 511;
  }

  // --- 512-tap synthesis window construction (mpa_synth_init, float path) ----

  private static float[] BuildWindow() {
    // 512 base coefficients (with the 257-entry mirror) plus the 256-entry
    // shuffle tail FFmpeg appends for its SIMD-free SUM8 access pattern.
    var window = new float[512 + 256];
    for (var i = 0; i < 257; ++i) {
      var v = MpcEnwindow.Enwindow[i] * WindowScale;
      window[i] = (float)v;
      if ((i & 63) != 0)
        v = -v;
      if (i != 0)
        window[512 - i] = (float)v;
    }
    for (var i = 0; i < 8; ++i)
      for (var j = 0; j < 16; ++j)
        window[512 + 16 * i + j] = window[64 * i + 32 - j];
    for (var i = 0; i < 8; ++i)
      for (var j = 0; j < 16; ++j)
        window[512 + 128 + 16 * i + j] = window[64 * i + 48 - j];
    return window;
  }

  // --- apply_window (float, faithful port of ff_mpadsp_apply_window_float) ---

  private static void ApplyWindow(float[] synthBuf, int offset, short[] samples, int sampleOffset, int incr) {
    // Mirror the 32 freshly-written samples into the wrap region so the inner
    // loops can index up to 512 ahead of `offset` without modulo.
    for (var k = 0; k < 32; ++k)
      synthBuf[offset + 512 + k] = synthBuf[offset + k];

    var w = 0;     // window cursor
    var w2 = 31;   // mirrored window cursor
    var sampleBase = sampleOffset;
    var samples2Base = sampleOffset + 31 * incr;

    // SUM8(op, sum, w, p): accumulates 8 taps at stride 64 from window[w..] and
    // synthBuf[offset + p..]; `subtract` selects MLSS over MACS.
    float Sum8(int wbase, int pbase, bool subtract) {
      var s = 0f;
      for (var n = 0; n < 8; ++n) {
        var prod = _Window[wbase + n * 64] * synthBuf[offset + pbase + n * 64];
        s += subtract ? -prod : prod;
      }
      return s;
    }

    // The float round_sample zeroes the accumulator after each emit, so every
    // output sample is an independent windowed sum (dither_state stays 0).
    var sum = 0f;
    sum += Sum8(w, 16, false);
    sum += Sum8(w + 32, 48, true);
    samples[sampleBase] = Clip(sum);
    sum = 0f;
    sampleBase += incr;
    ++w;

    for (var j = 1; j < 16; ++j) {
      var sum2 = 0f;
      // p = synth_buf + 16 + j ; SUM8P2(sum MACS, sum2 MLSS, w, w2, p)
      for (var n = 0; n < 8; ++n) {
        var tmp = synthBuf[offset + 16 + j + n * 64];
        sum += _Window[w + n * 64] * tmp;
        sum2 -= _Window[w2 + n * 64] * tmp;
      }
      // p = synth_buf + 48 - j ; SUM8P2(sum MLSS, sum2 MLSS, w+32, w2+32, p)
      for (var n = 0; n < 8; ++n) {
        var tmp = synthBuf[offset + 48 - j + n * 64];
        sum -= _Window[w + 32 + n * 64] * tmp;
        sum2 -= _Window[w2 + 32 + n * 64] * tmp;
      }

      samples[sampleBase] = Clip(sum);
      sampleBase += incr;
      sum = sum2; // round_sample zeroed sum; FFmpeg then adds sum2 in
      samples[samples2Base] = Clip(sum);
      sum = 0f;
      samples2Base -= incr;
      ++w;
      --w2;
    }

    sum = Sum8(w + 32, 32, true); // SUM8(MLSS, ...) on p = synth_buf + 32
    samples[sampleBase] = Clip(sum);
  }

  private static short Clip(float v) {
    var r = (int)MathF.Round(v);
    if (r > short.MaxValue)
      return short.MaxValue;
    if (r < short.MinValue)
      return short.MinValue;
    return (short)r;
  }

  // --- DCT32 (faithful float port of ff_dct32_float) -------------------------

  private const float Sqrt12 = 0.70710678118654752440f; // M_SQRT1_2

  private static float Mulh3(float x, float c, int s) => s * c * x;

  private static void Dct32(float[] tab, float[] outBuf, int outOffset) {
    float tmp0, tmp1;
    float val0, val1, val2, val3, val4, val5, val6, val7,
          val8, val9, val10, val11, val12, val13, val14, val15,
          val16, val17, val18, val19, val20, val21, val22, val23,
          val24, val25, val26, val27, val28, val29, val30, val31;

    // cos tables (FIXHR(x) == (float)x for the float build)
    const float COS0_0 = 0.50060299823519630134f / 2, COS0_1 = 0.50547095989754365998f / 2,
                COS0_2 = 0.51544730992262454697f / 2, COS0_3 = 0.53104259108978417447f / 2,
                COS0_4 = 0.55310389603444452782f / 2, COS0_5 = 0.58293496820613387367f / 2,
                COS0_6 = 0.62250412303566481615f / 2, COS0_7 = 0.67480834145500574602f / 2,
                COS0_8 = 0.74453627100229844977f / 2, COS0_9 = 0.83934964541552703873f / 2,
                COS0_10 = 0.97256823786196069369f / 2, COS0_11 = 1.16943993343288495515f / 4,
                COS0_12 = 1.48416461631416627724f / 4, COS0_13 = 2.05778100995341155085f / 8,
                COS0_14 = 3.40760841846871878570f / 8, COS0_15 = 10.19000812354805681150f / 32;
    const float COS1_0 = 0.50241928618815570551f / 2, COS1_1 = 0.52249861493968888062f / 2,
                COS1_2 = 0.56694403481635770368f / 2, COS1_3 = 0.64682178335999012954f / 2,
                COS1_4 = 0.78815462345125022473f / 2, COS1_5 = 1.06067768599034747134f / 4,
                COS1_6 = 1.72244709823833392782f / 4, COS1_7 = 5.10114861868916385802f / 16;
    const float COS2_0 = 0.50979557910415916894f / 2, COS2_1 = 0.60134488693504528054f / 2,
                COS2_2 = 0.89997622313641570463f / 2, COS2_3 = 2.56291544774150617881f / 8;
    const float COS3_0 = 0.54119610014619698439f / 2, COS3_1 = 1.30656296487637652785f / 4;
    const float COS4_0 = Sqrt12 / 2;

    // pass 1
    tmp0 = tab[0] + tab[31]; tmp1 = tab[0] - tab[31]; val0 = tmp0; val31 = Mulh3(tmp1, COS0_0, 1 << 1);
    tmp0 = tab[15] + tab[16]; tmp1 = tab[15] - tab[16]; val15 = tmp0; val16 = Mulh3(tmp1, COS0_15, 1 << 5);
    // pass 2
    tmp0 = val0 + val15; tmp1 = val0 - val15; val0 = tmp0; val15 = Mulh3(tmp1, COS1_0, 1 << 1);
    tmp0 = val16 + val31; tmp1 = val16 - val31; val16 = tmp0; val31 = Mulh3(tmp1, -COS1_0, 1 << 1);
    // pass 1
    tmp0 = tab[7] + tab[24]; tmp1 = tab[7] - tab[24]; val7 = tmp0; val24 = Mulh3(tmp1, COS0_7, 1 << 1);
    tmp0 = tab[8] + tab[23]; tmp1 = tab[8] - tab[23]; val8 = tmp0; val23 = Mulh3(tmp1, COS0_8, 1 << 1);
    // pass 2
    tmp0 = val7 + val8; tmp1 = val7 - val8; val7 = tmp0; val8 = Mulh3(tmp1, COS1_7, 1 << 4);
    tmp0 = val23 + val24; tmp1 = val23 - val24; val23 = tmp0; val24 = Mulh3(tmp1, -COS1_7, 1 << 4);
    // pass 3
    tmp0 = val0 + val7; tmp1 = val0 - val7; val0 = tmp0; val7 = Mulh3(tmp1, COS2_0, 1 << 1);
    tmp0 = val8 + val15; tmp1 = val8 - val15; val8 = tmp0; val15 = Mulh3(tmp1, -COS2_0, 1 << 1);
    tmp0 = val16 + val23; tmp1 = val16 - val23; val16 = tmp0; val23 = Mulh3(tmp1, COS2_0, 1 << 1);
    tmp0 = val24 + val31; tmp1 = val24 - val31; val24 = tmp0; val31 = Mulh3(tmp1, -COS2_0, 1 << 1);
    // pass 1
    tmp0 = tab[3] + tab[28]; tmp1 = tab[3] - tab[28]; val3 = tmp0; val28 = Mulh3(tmp1, COS0_3, 1 << 1);
    tmp0 = tab[12] + tab[19]; tmp1 = tab[12] - tab[19]; val12 = tmp0; val19 = Mulh3(tmp1, COS0_12, 1 << 2);
    // pass 2
    tmp0 = val3 + val12; tmp1 = val3 - val12; val3 = tmp0; val12 = Mulh3(tmp1, COS1_3, 1 << 1);
    tmp0 = val19 + val28; tmp1 = val19 - val28; val19 = tmp0; val28 = Mulh3(tmp1, -COS1_3, 1 << 1);
    // pass 1
    tmp0 = tab[4] + tab[27]; tmp1 = tab[4] - tab[27]; val4 = tmp0; val27 = Mulh3(tmp1, COS0_4, 1 << 1);
    tmp0 = tab[11] + tab[20]; tmp1 = tab[11] - tab[20]; val11 = tmp0; val20 = Mulh3(tmp1, COS0_11, 1 << 2);
    // pass 2
    tmp0 = val4 + val11; tmp1 = val4 - val11; val4 = tmp0; val11 = Mulh3(tmp1, COS1_4, 1 << 1);
    tmp0 = val20 + val27; tmp1 = val20 - val27; val20 = tmp0; val27 = Mulh3(tmp1, -COS1_4, 1 << 1);
    // pass 3
    tmp0 = val3 + val4; tmp1 = val3 - val4; val3 = tmp0; val4 = Mulh3(tmp1, COS2_3, 1 << 3);
    tmp0 = val11 + val12; tmp1 = val11 - val12; val11 = tmp0; val12 = Mulh3(tmp1, -COS2_3, 1 << 3);
    tmp0 = val19 + val20; tmp1 = val19 - val20; val19 = tmp0; val20 = Mulh3(tmp1, COS2_3, 1 << 3);
    tmp0 = val27 + val28; tmp1 = val27 - val28; val27 = tmp0; val28 = Mulh3(tmp1, -COS2_3, 1 << 3);
    // pass 4
    tmp0 = val0 + val3; tmp1 = val0 - val3; val0 = tmp0; val3 = Mulh3(tmp1, COS3_0, 1 << 1);
    tmp0 = val4 + val7; tmp1 = val4 - val7; val4 = tmp0; val7 = Mulh3(tmp1, -COS3_0, 1 << 1);
    tmp0 = val8 + val11; tmp1 = val8 - val11; val8 = tmp0; val11 = Mulh3(tmp1, COS3_0, 1 << 1);
    tmp0 = val12 + val15; tmp1 = val12 - val15; val12 = tmp0; val15 = Mulh3(tmp1, -COS3_0, 1 << 1);
    tmp0 = val16 + val19; tmp1 = val16 - val19; val16 = tmp0; val19 = Mulh3(tmp1, COS3_0, 1 << 1);
    tmp0 = val20 + val23; tmp1 = val20 - val23; val20 = tmp0; val23 = Mulh3(tmp1, -COS3_0, 1 << 1);
    tmp0 = val24 + val27; tmp1 = val24 - val27; val24 = tmp0; val27 = Mulh3(tmp1, COS3_0, 1 << 1);
    tmp0 = val28 + val31; tmp1 = val28 - val31; val28 = tmp0; val31 = Mulh3(tmp1, -COS3_0, 1 << 1);

    // pass 1
    tmp0 = tab[1] + tab[30]; tmp1 = tab[1] - tab[30]; val1 = tmp0; val30 = Mulh3(tmp1, COS0_1, 1 << 1);
    tmp0 = tab[14] + tab[17]; tmp1 = tab[14] - tab[17]; val14 = tmp0; val17 = Mulh3(tmp1, COS0_14, 1 << 3);
    // pass 2
    tmp0 = val1 + val14; tmp1 = val1 - val14; val1 = tmp0; val14 = Mulh3(tmp1, COS1_1, 1 << 1);
    tmp0 = val17 + val30; tmp1 = val17 - val30; val17 = tmp0; val30 = Mulh3(tmp1, -COS1_1, 1 << 1);
    // pass 1
    tmp0 = tab[6] + tab[25]; tmp1 = tab[6] - tab[25]; val6 = tmp0; val25 = Mulh3(tmp1, COS0_6, 1 << 1);
    tmp0 = tab[9] + tab[22]; tmp1 = tab[9] - tab[22]; val9 = tmp0; val22 = Mulh3(tmp1, COS0_9, 1 << 1);
    // pass 2
    tmp0 = val6 + val9; tmp1 = val6 - val9; val6 = tmp0; val9 = Mulh3(tmp1, COS1_6, 1 << 2);
    tmp0 = val22 + val25; tmp1 = val22 - val25; val22 = tmp0; val25 = Mulh3(tmp1, -COS1_6, 1 << 2);
    // pass 3
    tmp0 = val1 + val6; tmp1 = val1 - val6; val1 = tmp0; val6 = Mulh3(tmp1, COS2_1, 1 << 1);
    tmp0 = val9 + val14; tmp1 = val9 - val14; val9 = tmp0; val14 = Mulh3(tmp1, -COS2_1, 1 << 1);
    tmp0 = val17 + val22; tmp1 = val17 - val22; val17 = tmp0; val22 = Mulh3(tmp1, COS2_1, 1 << 1);
    tmp0 = val25 + val30; tmp1 = val25 - val30; val25 = tmp0; val30 = Mulh3(tmp1, -COS2_1, 1 << 1);

    // pass 1
    tmp0 = tab[2] + tab[29]; tmp1 = tab[2] - tab[29]; val2 = tmp0; val29 = Mulh3(tmp1, COS0_2, 1 << 1);
    tmp0 = tab[13] + tab[18]; tmp1 = tab[13] - tab[18]; val13 = tmp0; val18 = Mulh3(tmp1, COS0_13, 1 << 3);
    // pass 2
    tmp0 = val2 + val13; tmp1 = val2 - val13; val2 = tmp0; val13 = Mulh3(tmp1, COS1_2, 1 << 1);
    tmp0 = val18 + val29; tmp1 = val18 - val29; val18 = tmp0; val29 = Mulh3(tmp1, -COS1_2, 1 << 1);
    // pass 1
    tmp0 = tab[5] + tab[26]; tmp1 = tab[5] - tab[26]; val5 = tmp0; val26 = Mulh3(tmp1, COS0_5, 1 << 1);
    tmp0 = tab[10] + tab[21]; tmp1 = tab[10] - tab[21]; val10 = tmp0; val21 = Mulh3(tmp1, COS0_10, 1 << 1);
    // pass 2
    tmp0 = val5 + val10; tmp1 = val5 - val10; val5 = tmp0; val10 = Mulh3(tmp1, COS1_5, 1 << 2);
    tmp0 = val21 + val26; tmp1 = val21 - val26; val21 = tmp0; val26 = Mulh3(tmp1, -COS1_5, 1 << 2);
    // pass 3
    tmp0 = val2 + val5; tmp1 = val2 - val5; val2 = tmp0; val5 = Mulh3(tmp1, COS2_2, 1 << 1);
    tmp0 = val10 + val13; tmp1 = val10 - val13; val10 = tmp0; val13 = Mulh3(tmp1, -COS2_2, 1 << 1);
    tmp0 = val18 + val21; tmp1 = val18 - val21; val18 = tmp0; val21 = Mulh3(tmp1, COS2_2, 1 << 1);
    tmp0 = val26 + val29; tmp1 = val26 - val29; val26 = tmp0; val29 = Mulh3(tmp1, -COS2_2, 1 << 1);
    // pass 4
    tmp0 = val1 + val2; tmp1 = val1 - val2; val1 = tmp0; val2 = Mulh3(tmp1, COS3_1, 1 << 2);
    tmp0 = val5 + val6; tmp1 = val5 - val6; val5 = tmp0; val6 = Mulh3(tmp1, -COS3_1, 1 << 2);
    tmp0 = val9 + val10; tmp1 = val9 - val10; val9 = tmp0; val10 = Mulh3(tmp1, COS3_1, 1 << 2);
    tmp0 = val13 + val14; tmp1 = val13 - val14; val13 = tmp0; val14 = Mulh3(tmp1, -COS3_1, 1 << 2);
    tmp0 = val17 + val18; tmp1 = val17 - val18; val17 = tmp0; val18 = Mulh3(tmp1, COS3_1, 1 << 2);
    tmp0 = val21 + val22; tmp1 = val21 - val22; val21 = tmp0; val22 = Mulh3(tmp1, -COS3_1, 1 << 2);
    tmp0 = val25 + val26; tmp1 = val25 - val26; val25 = tmp0; val26 = Mulh3(tmp1, COS3_1, 1 << 2);
    tmp0 = val29 + val30; tmp1 = val29 - val30; val29 = tmp0; val30 = Mulh3(tmp1, -COS3_1, 1 << 2);

    // pass 5 — BF1(a,b,c,d) and BF2(a,b,c,d)
    // BF1: BF(a,b,COS4_0,1); BF(c,d,-COS4_0,1); val_c += val_d
    // BF2: BF1 + val_a += val_c; val_c += val_b; val_b += val_d
    Bf1(ref val0, ref val1, ref val2, ref val3, COS4_0, ref tmp0, ref tmp1);
    Bf2(ref val4, ref val5, ref val6, ref val7, COS4_0, ref tmp0, ref tmp1);
    Bf1(ref val8, ref val9, ref val10, ref val11, COS4_0, ref tmp0, ref tmp1);
    Bf2(ref val12, ref val13, ref val14, ref val15, COS4_0, ref tmp0, ref tmp1);
    Bf1(ref val16, ref val17, ref val18, ref val19, COS4_0, ref tmp0, ref tmp1);
    Bf2(ref val20, ref val21, ref val22, ref val23, COS4_0, ref tmp0, ref tmp1);
    Bf1(ref val24, ref val25, ref val26, ref val27, COS4_0, ref tmp0, ref tmp1);
    Bf2(ref val28, ref val29, ref val30, ref val31, COS4_0, ref tmp0, ref tmp1);

    // pass 6
    val8 += val12; val12 += val10; val10 += val14; val14 += val9;
    val9 += val13; val13 += val11; val11 += val15;

    outBuf[outOffset + 0] = val0; outBuf[outOffset + 16] = val1; outBuf[outOffset + 8] = val2; outBuf[outOffset + 24] = val3;
    outBuf[outOffset + 4] = val4; outBuf[outOffset + 20] = val5; outBuf[outOffset + 12] = val6; outBuf[outOffset + 28] = val7;
    outBuf[outOffset + 2] = val8; outBuf[outOffset + 18] = val9; outBuf[outOffset + 10] = val10; outBuf[outOffset + 26] = val11;
    outBuf[outOffset + 6] = val12; outBuf[outOffset + 22] = val13; outBuf[outOffset + 14] = val14; outBuf[outOffset + 30] = val15;

    val24 += val28; val28 += val26; val26 += val30; val30 += val25;
    val25 += val29; val29 += val27; val27 += val31;

    outBuf[outOffset + 1] = val16 + val24; outBuf[outOffset + 17] = val17 + val25;
    outBuf[outOffset + 9] = val18 + val26; outBuf[outOffset + 25] = val19 + val27;
    outBuf[outOffset + 5] = val20 + val28; outBuf[outOffset + 21] = val21 + val29;
    outBuf[outOffset + 13] = val22 + val30; outBuf[outOffset + 29] = val23 + val31;
    outBuf[outOffset + 3] = val24 + val20; outBuf[outOffset + 19] = val25 + val21;
    outBuf[outOffset + 11] = val26 + val22; outBuf[outOffset + 27] = val27 + val23;
    outBuf[outOffset + 7] = val28 + val18; outBuf[outOffset + 23] = val29 + val19;
    outBuf[outOffset + 15] = val30 + val17; outBuf[outOffset + 31] = val31;
  }

  private static void Bf1(ref float a, ref float b, ref float c, ref float d, float cos40, ref float tmp0, ref float tmp1) {
    tmp0 = a + b; tmp1 = a - b; a = tmp0; b = Mulh3(tmp1, cos40, 1 << 1);
    tmp0 = c + d; tmp1 = c - d; c = tmp0; d = Mulh3(tmp1, -cos40, 1 << 1);
    c += d;
  }

  private static void Bf2(ref float a, ref float b, ref float c, ref float d, float cos40, ref float tmp0, ref float tmp1) {
    tmp0 = a + b; tmp1 = a - b; a = tmp0; b = Mulh3(tmp1, cos40, 1 << 1);
    tmp0 = c + d; tmp1 = c - d; c = tmp0; d = Mulh3(tmp1, -cos40, 1 << 1);
    c += d;
    a += c;
    c += b;
    b += d;
  }
}

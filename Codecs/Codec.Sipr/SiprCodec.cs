#pragma warning disable CS1591
namespace Codec.Sipr;

/// <summary>
/// SIPR / ACELP.NET ("sipr" FOURCC) speech decoder — a faithful, decode-only port of FFmpeg's
/// <c>libavcodec/sipr.c</c> (plus the ACELP / LSP / CELP helpers it relies on from
/// <c>acelp_vectors.c</c>, <c>acelp_filters.c</c>, <c>acelp_pitch_delay.c</c>,
/// <c>celp_filters.c</c> and <c>lsp.c</c>). SIPR is a CELP-style mono speech coder carried in
/// RealAudio: each coded frame holds an LSF vector quantisation, then per subframe an adaptive
/// (pitch) codebook contribution interpolated at 1/3-sample resolution, a mode-specific sparse
/// fixed codebook, VQ-coded gains with MA prediction, and an LPC synthesis filter, followed by
/// an order-2 high-pass output filter (and for 5k0 an extra AMR-style postfilter with adaptive
/// gain control).
/// <para>The codec mode is selected from the RealAudio coded-frame size (block_align): 19 →
/// 8k5, 29 → 6k5, 37 → 5k0, 20 → 16k. The three 8 kbit/s sampling modes (8k5, 6k5, 5k0) are
/// fully decoded to 8000 Hz mono 16-bit PCM. The 16k mode requires the separate
/// <c>sipr16k.c</c> table set and is surfaced as <see cref="NotSupportedException"/>.</para>
/// <para>Decode-only — there is no encoder. State (excitation history, LSF/LSP history, gain
/// memory, postfilter memories) is carried across frames exactly as the reference does, so a
/// concatenated multi-frame buffer decodes identically to feeding frames one at a time.</para>
/// </summary>
public sealed class SiprCodec {

  /// <summary>SIPR decode modes (mirrors <c>SiprMode</c> in sipr.h).</summary>
  public enum SiprMode {
        /// <summary>
    /// Specifies the mode 16k option.
    /// </summary>
Mode16k = 0,
        /// <summary>
    /// Specifies the mode 8k 5 option.
    /// </summary>
Mode8k5 = 1,
        /// <summary>
    /// Specifies the mode 6k 5 option.
    /// </summary>
Mode6k5 = 2,
        /// <summary>
    /// Specifies the mode 5k 0 option.
    /// </summary>
Mode5k0 = 3,
  }

  // ── sipr.h constants ──────────────────────────────────────────────────────
  private const int LpFilterOrder = 10;
  private const int LInterpol = LpFilterOrder + 1;     // 11
  private const int SubfrSize = 48;                    // subframe size (non-16k)
  private const int MaxSubframeCount = 5;
  private const int PitchDelayMax = 143;               // acelp_pitch_delay.h
  private const int PitchDelayMin = 20;
  private const int PitchMax = 281;                    // sipr.h (excitation buffer span)
  private const int LSubfr16k = 80;
  private const double LsfqDiffMin = 0.0125 * Math.PI;

  private static readonly double MLn10 = Math.Log(10.0);
  private static readonly double MLn2 = Math.Log(2.0);

  // ── per-mode parameters (the `modes[]` table from sipr.c) ─────────────────
  private sealed class ModeParam {
    public int BitsPerFrame;
    public int SubframeCount;
    public int FramesPerPacket;
    public float PitchSharpFactor;
    public int NumberOfFcIndexes;
    public int MaPredictorBits;
    public int[] VqIndexesBits = [];
    public int[] PitchDelayBits = [];
    public int GpIndexBits;
    public int[] FcIndexBits = [];
    public int GcIndexBits;
  }

  private static readonly ModeParam[] Modes = [
    // MODE_16k
    new() {
      BitsPerFrame = 160, SubframeCount = 2, FramesPerPacket = 1, PitchSharpFactor = 0.00f,
      NumberOfFcIndexes = 10, MaPredictorBits = 1,
      VqIndexesBits = [7, 8, 7, 7, 7], PitchDelayBits = [9, 6], GpIndexBits = 4,
      FcIndexBits = [4, 5, 4, 5, 4, 5, 4, 5, 4, 5], GcIndexBits = 5,
    },
    // MODE_8k5
    new() {
      BitsPerFrame = 152, SubframeCount = 3, FramesPerPacket = 1, PitchSharpFactor = 0.8f,
      NumberOfFcIndexes = 3, MaPredictorBits = 0,
      VqIndexesBits = [6, 7, 7, 7, 5], PitchDelayBits = [8, 5, 5], GpIndexBits = 0,
      FcIndexBits = [9, 9, 9], GcIndexBits = 7,
    },
    // MODE_6k5
    new() {
      BitsPerFrame = 232, SubframeCount = 3, FramesPerPacket = 2, PitchSharpFactor = 0.8f,
      NumberOfFcIndexes = 3, MaPredictorBits = 0,
      VqIndexesBits = [6, 7, 7, 7, 5], PitchDelayBits = [8, 5, 5], GpIndexBits = 0,
      FcIndexBits = [5, 5, 5], GcIndexBits = 7,
    },
    // MODE_5k0
    new() {
      BitsPerFrame = 296, SubframeCount = 5, FramesPerPacket = 2, PitchSharpFactor = 0.85f,
      NumberOfFcIndexes = 1, MaPredictorBits = 0,
      VqIndexesBits = [6, 7, 7, 7, 5], PitchDelayBits = [8, 5, 8, 5, 5], GpIndexBits = 0,
      FcIndexBits = [10], GcIndexBits = 7,
    },
  ];

  private readonly SiprMode _mode;
  private readonly ModeParam _p;

  // ── persistent decoder state (mirrors SiprContext, non-16k subset) ────────
  private float _pastPitchGain;
  private readonly float[] _lsfHistory = new float[LpFilterOrder];
  private readonly float[] _excitation = new float[LInterpol + PitchMax + 2 * LSubfr16k];
  private readonly float[] _synthBuf = new float[LpFilterOrder + 5 * SubfrSize + 6];
  private readonly float[] _lspHistory = new float[LpFilterOrder];
  private float _gainMem;
  private readonly float[] _energyHistory = new float[4];
  private readonly float[] _highpassFiltMem = new float[2];
  private readonly float[] _postfilterMem = new float[PitchDelayMax + LpFilterOrder];

  // 5k0 only
  private float _tiltMem;
  private float _postfilterAgc;
  private readonly float[] _postfilterMem5k0 = new float[PitchDelayMax + LpFilterOrder];
  private readonly float[] _postfilterSyn5k0 = new float[LpFilterOrder + SubfrSize * 5];

  /// <summary>The active decode mode.</summary>
  public SiprMode Mode => this._mode;

  /// <summary>Number of PCM samples this codec emits per coded frame (mono).</summary>
  public int SamplesPerFrame =>
    this._p.FramesPerPacket * this._p.SubframeCount * SubfrSize;

  /// <summary>Coded-frame size in bytes (<c>bits_per_frame / 8</c>).</summary>
  public int FrameBytes => this._p.BitsPerFrame >> 3;

  /// <summary>
  /// Constructs a decoder for the given mode. <see cref="SiprMode.Mode16k"/> is not yet
  /// supported and throws <see cref="NotSupportedException"/>.
  /// </summary>
  public SiprCodec(SiprMode mode) {
    if (mode == SiprMode.Mode16k)
      throw new NotSupportedException("SIPR 16k mode is not supported (requires sipr16k tables).");
    this._mode = mode;
    this._p = Modes[(int)mode];

    for (var i = 0; i < LpFilterOrder; ++i)
      this._lspHistory[i] = (float)Math.Cos((i + 1) * Math.PI / (LpFilterOrder + 1));
    for (var i = 0; i < 4; ++i)
      this._energyHistory[i] = -14f;
  }

  /// <summary>
  /// Maps a RealAudio coded-frame size (block_align) to a SIPR mode, exactly as
  /// <c>sipr_decoder_init</c> does: 20 → 16k, 19 → 8k5, 29 → 6k5, 37 → 5k0.
  /// Returns <see langword="null"/> for any other size.
  /// </summary>
  public static SiprMode? ModeFromBlockAlign(int blockAlign) => blockAlign switch {
    20 => SiprMode.Mode16k,
    19 => SiprMode.Mode8k5,
    29 => SiprMode.Mode6k5,
    37 => SiprMode.Mode5k0,
    _ => null,
  };

  /// <summary>
  /// Decodes one coded frame (<see cref="FrameBytes"/> bytes; a shorter span is tolerated and
  /// treated as zero-padded) into <see cref="SamplesPerFrame"/> mono 16-bit samples.
  /// </summary>
  public short[] Decode(ReadOnlySpan<byte> frame) {
    var bytes = new byte[Math.Max(this.FrameBytes, frame.Length)];
    frame.CopyTo(bytes);

    var subframeSize = SubfrSize; // non-16k
    var perFrame = subframeSize * this._p.SubframeCount;
    var outF = new float[this._p.FramesPerPacket * perFrame];

    var gb = new SiprBitReader(bytes, 0, this._p.BitsPerFrame);
    for (var f = 0; f < this._p.FramesPerPacket; ++f) {
      var parm = DecodeParameters(gb, this._p);
      this.DecodeFrame(parm, outF, f * perFrame);
    }

    var pcm = new short[outF.Length];
    for (var i = 0; i < outF.Length; ++i)
      pcm[i] = ClipInt16((int)MathF.Round(outF[i] * 32768f));
    return pcm;
  }

  /// <summary>
  /// Decodes a concatenation of coded frames. A ragged tail shorter than a full coded frame is
  /// zero-padded and decoded (matching the reference's tolerance of a short final packet).
  /// </summary>
  public short[] DecodeStream(ReadOnlySpan<byte> data) {
    var fb = this.FrameBytes;
    if (fb <= 0 || data.Length == 0)
      return [];
    var frames = (data.Length + fb - 1) / fb;
    var spf = this.SamplesPerFrame;
    var output = new short[frames * spf];
    var pos = 0;
    for (var f = 0; f < frames; ++f) {
      var start = f * fb;
      var len = Math.Min(fb, data.Length - start);
      var pcm = this.Decode(data.Slice(start, len));
      pcm.CopyTo(output.AsSpan(pos));
      pos += spf;
    }
    return output;
  }

  // ── bitstream parameter extraction (decode_parameters) ────────────────────
  private sealed class SiprParameters {
    public int MaPredSwitch;
    public readonly int[] VqIndexes = new int[5];
    public readonly int[] PitchDelay = new int[5];
    public readonly int[] GpIndex = new int[5];
    public readonly short[][] FcIndexes = [new short[10], new short[10], new short[10], new short[10], new short[10]];
    public readonly int[] GcIndex = new int[5];
  }

  private static SiprParameters DecodeParameters(SiprBitReader gb, ModeParam p) {
    var parms = new SiprParameters();

    if (p.MaPredictorBits != 0)
      parms.MaPredSwitch = gb.GetBits(p.MaPredictorBits);

    for (var i = 0; i < 5; ++i)
      parms.VqIndexes[i] = gb.GetBits(p.VqIndexesBits[i]);

    for (var i = 0; i < p.SubframeCount; ++i) {
      parms.PitchDelay[i] = gb.GetBits(p.PitchDelayBits[i]);
      if (p.GpIndexBits != 0)
        parms.GpIndex[i] = gb.GetBits(p.GpIndexBits);

      for (var j = 0; j < p.NumberOfFcIndexes; ++j)
        parms.FcIndexes[i][j] = (short)gb.GetBits(p.FcIndexBits[j]);

      parms.GcIndex[i] = gb.GetBits(p.GcIndexBits);
    }
    return parms;
  }

  // ── LSF decode (lsf_decode_fp) ────────────────────────────────────────────
  private void LsfDecodeFp(float[] lsfnew, SiprParameters parm) {
    var lsfTmp = new float[LpFilterOrder];
    // dequant: stride 2, 5 vectors.
    for (var i = 0; i < 5; ++i) {
      var cb = SiprTables.LsfCodebooks[i][parm.VqIndexes[i]];
      lsfTmp[2 * i] = cb[0];
      lsfTmp[2 * i + 1] = cb[1];
    }

    for (var i = 0; i < LpFilterOrder; ++i)
      lsfnew[i] = this._lsfHistory[i] * 0.33f + lsfTmp[i] + SiprTables.MeanLsf[i];

    SortNearlySortedFloats(lsfnew, LpFilterOrder - 1);

    // Minimum distance is enforced over the first nine values only.
    SetMinDistLsf(lsfnew, LsfqDiffMin, LpFilterOrder - 1);
    lsfnew[9] = MathF.Min(lsfnew[LpFilterOrder - 1], (float)(1.3 * Math.PI));

    Array.Copy(lsfTmp, this._lsfHistory, LpFilterOrder);

    for (var i = 0; i < LpFilterOrder - 1; ++i)
      lsfnew[i] = MathF.Cos(lsfnew[i]);
    lsfnew[LpFilterOrder - 1] *= (float)(6.153848 / Math.PI);
  }

  // ── LSP→LP across subframes (sipr_decode_lp) ──────────────────────────────
  private static void SiprDecodeLp(float[] lsfnew, float[] lsfold, float[] az, int numSubfr) {
    var lsfint = new double[LpFilterOrder];
    var t0 = 1.0 / numSubfr;
    var t = t0 * 0.5;
    var azOff = 0;
    for (var i = 0; i < numSubfr; ++i) {
      for (var j = 0; j < LpFilterOrder; ++j)
        lsfint[j] = lsfold[j] * (1 - t) + t * lsfnew[j];

      AmrwbLsp2Lpc(lsfint, az, azOff, LpFilterOrder);
      azOff += LpFilterOrder;
      t += t0;
    }
  }

  // ── adaptive impulse response (eval_ir) ───────────────────────────────────
  private static void EvalIr(float[] az, int azOff, int pitchLag, float[] freq, int freqOff,
      float pitchSharpFactor) {
    var tmp1 = new float[SubfrSize + 1];
    var tmp2 = new float[LpFilterOrder + 1];

    tmp1[0] = 1.0f;
    for (var i = 0; i < LpFilterOrder; ++i) {
      tmp1[i + 1] = az[azOff + i] * SiprTables.Pow055[i];
      tmp2[i] = az[azOff + i] * SiprTables.Pow07[i];
    }
    // memset(tmp1 + 11, 0, 37*sizeof) — already zero in a fresh array.

    CelpLpSynthesisFilterf(freq, freqOff, tmp2, 0, tmp1, 0, SubfrSize, LpFilterOrder);

    PitchSharpening(pitchLag, pitchSharpFactor, freq, freqOff);
  }

  private static void PitchSharpening(int pitchLagInt, float beta, float[] fixedVector, int off) {
    for (var i = pitchLagInt; i < SubfrSize; ++i)
      fixedVector[off + i] += beta * fixedVector[off + i - pitchLagInt];
  }

  private static void ConvoluteWithSparse(float[] outp, AmrFixed pulses, float[] shape,
      int shapeOff, int length) {
    Array.Clear(outp, 0, length);
    for (var i = 0; i < pulses.N; ++i)
      for (var j = pulses.X[i]; j < length; ++j)
        outp[j] += pulses.Y[i] * shape[shapeOff + j - pulses.X[i]];
  }

  // ── fixed-codebook sparse pulse decode (decode_fixed_sparse) ──────────────
  private sealed class AmrFixed {
    public int N;
    public readonly int[] X = new int[10];
    public readonly float[] Y = new float[10];
  }

  private static AmrFixed DecodeFixedSparse(short[] pulses, SiprMode mode, bool lowGain) {
    var fs = new AmrFixed();
    switch (mode) {
      case SiprMode.Mode6k5:
        for (var i = 0; i < 3; ++i) {
          fs.X[i] = 3 * (pulses[i] & 0xf) + i;
          fs.Y[i] = (pulses[i] & 0x10) != 0 ? -1 : 1;
        }
        fs.N = 3;
        break;
      case SiprMode.Mode8k5:
        for (var i = 0; i < 3; ++i) {
          fs.X[2 * i] = 3 * ((pulses[i] >> 4) & 0xf) + i;
          fs.X[2 * i + 1] = 3 * (pulses[i] & 0xf) + i;

          fs.Y[2 * i] = (pulses[i] & 0x100) != 0 ? -1.0f : 1.0f;
          fs.Y[2 * i + 1] = fs.X[2 * i + 1] < fs.X[2 * i] ? -fs.Y[2 * i] : fs.Y[2 * i];
        }
        fs.N = 6;
        break;
      case SiprMode.Mode5k0:
      default:
        if (lowGain) {
          var offset = (pulses[0] & 0x200) != 0 ? 2 : 0;
          int val = pulses[0];
          for (var i = 0; i < 3; ++i) {
            var index = (val & 0x7) * 6 + 4 - i * 2;
            fs.Y[i] = ((offset + index) & 0x3) != 0 ? -1 : 1;
            fs.X[i] = index;
            val >>= 3;
          }
          fs.N = 3;
        } else {
          var pulseSubset = (pulses[0] >> 8) & 1;
          fs.X[0] = ((pulses[0] >> 4) & 15) * 3 + pulseSubset;
          fs.X[1] = (pulses[0] & 15) * 3 + pulseSubset + 1;
          fs.Y[0] = (pulses[0] & 0x200) != 0 ? -1 : 1;
          fs.Y[1] = -fs.Y[0];
          fs.N = 2;
        }
        break;
    }
    return fs;
  }

  // ── 5k0 postfilter (postfilter_5k0) ───────────────────────────────────────
  private void Postfilter5k0(float[] lpc, int lpcOff, float[] samples, int samplesOff) {
    var buf = new float[SubfrSize + LpFilterOrder];
    const int poleOut = LpFilterOrder; // pole_out = buf + LP_FILTER_ORDER
    var lpcN = new float[LpFilterOrder];
    var lpcD = new float[LpFilterOrder];

    for (var i = 0; i < LpFilterOrder; ++i) {
      lpcD[i] = lpc[lpcOff + i] * SiprTables.Pow075[i];
      lpcN[i] = lpc[lpcOff + i] * SiprTables.Pow05[i];
    }

    Array.Copy(this._postfilterMem, 0, buf, poleOut - LpFilterOrder, LpFilterOrder);

    CelpLpSynthesisFilterf(buf, poleOut, lpcD, 0, samples, samplesOff, SubfrSize, LpFilterOrder);

    Array.Copy(buf, poleOut + SubfrSize - LpFilterOrder, this._postfilterMem, 0, LpFilterOrder);

    TiltCompensation(ref this._tiltMem, 0.4f, buf, poleOut, SubfrSize);

    Array.Copy(this._postfilterMem5k0, 0, buf, poleOut - LpFilterOrder, LpFilterOrder);
    Array.Copy(buf, poleOut + SubfrSize - LpFilterOrder, this._postfilterMem5k0, 0, LpFilterOrder);

    CelpLpZeroSynthesisFilterf(samples, samplesOff, lpcN, 0, buf, poleOut, SubfrSize, LpFilterOrder);
  }

  // ── main per-frame decode (decode_frame) ──────────────────────────────────
  private void DecodeFrame(SiprParameters parms, float[] outData, int outOff) {
    var subframeCount = this._p.SubframeCount;
    var frameSize = subframeCount * SubfrSize;
    var az = new float[LpFilterOrder * MaxSubframeCount];
    var irBuf = new float[SubfrSize + LpFilterOrder];
    var lsfNew = new float[LpFilterOrder];
    const int impulseResponse = LpFilterOrder; // ir_buf + LP_FILTER_ORDER

    // synth = synth_buf + 16; we model synth as a view into _synthBuf at offset 16, but our
    // buffer is sized LP_FILTER_ORDER + 5*SUBFR_SIZE + 6 so the reference uses 16 for alignment.
    // We keep LP_FILTER_ORDER history; offset chosen so synth - LP_FILTER_ORDER is valid.
    const int synthBase = 16;

    LsfDecodeFp(lsfNew, parms);
    SiprDecodeLp(lsfNew, this._lspHistory, az, subframeCount);
    Array.Copy(lsfNew, this._lspHistory, LpFilterOrder);

    var excIndex = PitchDelayMax + LInterpol; // excitation = ctx->excitation + PITCH_DELAY_MAX + L_INTERPOL
    var t0First = 0;

    for (var i = 0; i < subframeCount; ++i) {
      var pAz = i * LpFilterOrder;
      var fixedVector = new float[SubfrSize];

      DecodePitchLag(parms.PitchDelay[i], i, this._mode == SiprMode.Mode5k0, 6,
        ref t0First, out var t0, out var t0Frac);

      if (i == 0 || (i == 2 && this._mode == SiprMode.Mode5k0))
        t0First = t0;

      AcelpInterpolatef(this._excitation, excIndex,
        excIndex - t0 + (t0Frac <= 0 ? 1 : 0),
        SiprTables.B60Sinc, 6, 2 * ((2 + t0Frac) % 3 + 1), LpFilterOrder, SubfrSize);

      var fixedCb = DecodeFixedSparse(parms.FcIndexes[i], this._mode, this._pastPitchGain < 0.8f);

      EvalIr(az, pAz, t0, irBuf, impulseResponse, this._p.PitchSharpFactor);

      ConvoluteWithSparse(fixedVector, fixedCb, irBuf, impulseResponse, SubfrSize);

      var avgEnergy = (0.01f + ScalarProduct(fixedVector, 0, fixedVector, 0, SubfrSize)) / SubfrSize;

      this._pastPitchGain = SiprTables.GainCb[parms.GcIndex[i]][0];
      var pitchGain = this._pastPitchGain;

      var gainCode = AmrSetFixedGain(SiprTables.GainCb[parms.GcIndex[i]][1],
        avgEnergy, this._energyHistory, (float)(34 - 15.0 / (0.05 * MLn10 / MLn2)), SiprTables.Pred);

      WeightedVectorSumf(this._excitation, excIndex, this._excitation, excIndex,
        fixedVector, 0, pitchGain, gainCode, SubfrSize);

      pitchGain *= 0.5f * pitchGain;
      pitchGain = MathF.Min(pitchGain, 0.4f);

      this._gainMem = 0.7f * this._gainMem + 0.3f * pitchGain;
      this._gainMem = MathF.Min(this._gainMem, pitchGain);
      gainCode *= this._gainMem;

      for (var j = 0; j < SubfrSize; ++j)
        fixedVector[j] = this._excitation[excIndex + j] - gainCode * fixedVector[j];

      if (this._mode == SiprMode.Mode5k0) {
        Postfilter5k0(az, pAz, fixedVector, 0);

        CelpLpSynthesisFilterf(this._postfilterSyn5k0, LpFilterOrder + i * SubfrSize,
          az, pAz, this._excitation, excIndex, SubfrSize, LpFilterOrder);
      }

      CelpLpSynthesisFilterf(this._synthBuf, synthBase + i * SubfrSize, az, pAz,
        fixedVector, 0, SubfrSize, LpFilterOrder);

      excIndex += SubfrSize;
    }

    // memcpy(synth - LP_FILTER_ORDER, synth + frame_size - LP_FILTER_ORDER, LP_FILTER_ORDER)
    Array.Copy(this._synthBuf, synthBase + frameSize - LpFilterOrder,
      this._synthBuf, synthBase - LpFilterOrder, LpFilterOrder);

    if (this._mode == SiprMode.Mode5k0) {
      for (var i = 0; i < subframeCount; ++i) {
        var energy = ScalarProduct(this._postfilterSyn5k0, LpFilterOrder + i * SubfrSize,
          this._postfilterSyn5k0, LpFilterOrder + i * SubfrSize, SubfrSize);
        AdaptiveGainControl(this._synthBuf, synthBase + i * SubfrSize,
          this._synthBuf, synthBase + i * SubfrSize, energy, SubfrSize, 0.9f, ref this._postfilterAgc);
      }
      Array.Copy(this._postfilterSyn5k0, frameSize, this._postfilterSyn5k0, 0, LpFilterOrder);
    }

    // memmove(ctx->excitation, excitation - PITCH_DELAY_MAX - L_INTERPOL, PITCH_DELAY_MAX + L_INTERPOL)
    Array.Copy(this._excitation, excIndex - PitchDelayMax - LInterpol,
      this._excitation, 0, PitchDelayMax + LInterpol);

    ApplyOrder2TransferFunction(outData, outOff, this._synthBuf, synthBase,
      [-1.99997f, 1.000000000f], [-1.93307352f, 0.935891986f],
      0.939805806f, this._highpassFiltMem, frameSize);
  }

  // ── ported helpers ────────────────────────────────────────────────────────

  /// <summary>Port of <c>ff_decode_pitch_lag</c> for the SIPR resolutions (5/6).</summary>
  private static void DecodePitchLag(int pitchIndex, int subframe, bool thirdAsFirst,
      int resolution, ref int prevLagInt, out int lagInt, out int lagFrac) {
    if (subframe == 0 || (subframe == 2 && thirdAsFirst)) {
      if (pitchIndex < 197)
        pitchIndex += 59;
      else
        pitchIndex = 3 * pitchIndex - 335;
    } else {
      if (resolution == 4) {
        var searchRangeMin = Clip(prevLagInt - 5, PitchDelayMin, PitchDelayMax - 9);
        if (pitchIndex < 4)
          pitchIndex = 3 * (pitchIndex + searchRangeMin) + 1;
        else if (pitchIndex < 12)
          pitchIndex += 3 * searchRangeMin + 7;
        else
          pitchIndex = 3 * (pitchIndex + searchRangeMin - 6) + 1;
      } else {
        --pitchIndex;
        if (resolution == 5)
          pitchIndex += 3 * Clip(prevLagInt - 10, PitchDelayMin, PitchDelayMax - 19);
        else
          pitchIndex += 3 * Clip(prevLagInt - 5, PitchDelayMin, PitchDelayMax - 9);
      }
    }
    lagInt = pitchIndex * 10923 >> 15;
    lagFrac = pitchIndex - 3 * lagInt - 1;
  }

  /// <summary>Port of <c>ff_acelp_interpolatef</c>.</summary>
  private static void AcelpInterpolatef(float[] outp, int outOff, int inOff,
      float[] filterCoeffs, int precision, int fracPos, int filterLength, int length) {
    for (var n = 0; n < length; ++n) {
      var idx = 0;
      var v = 0f;
      for (var i = 0; i < filterLength;) {
        v += outp[inOff + n + i] * filterCoeffs[idx + fracPos];
        idx += precision;
        ++i;
        v += outp[inOff + n - i] * filterCoeffs[idx - fracPos];
      }
      outp[outOff + n] = v;
    }
  }

  /// <summary>Port of <c>ff_amr_set_fixed_gain</c>.</summary>
  private static float AmrSetFixedGain(float fixedGainFactor, float fixedMeanEnergy,
      float[] predictionError, float energyMean, float[] predTable) {
    var val = (float)(fixedGainFactor *
      Math.Pow(10.0, 0.05 * (ScalarProduct(predTable, 0, predictionError, 0, 4) + energyMean)) /
      Math.Sqrt(fixedMeanEnergy != 0f ? fixedMeanEnergy : 1.0));

    predictionError[0] = predictionError[1];
    predictionError[1] = predictionError[2];
    predictionError[2] = predictionError[3];
    predictionError[3] = 20.0f * MathF.Log10(fixedGainFactor);

    return val;
  }

  /// <summary>Port of <c>ff_weighted_vector_sumf</c>.</summary>
  private static void WeightedVectorSumf(float[] outp, int outOff, float[] inA, int aOff,
      float[] inB, int bOff, float weightA, float weightB, int length) {
    for (var i = 0; i < length; ++i)
      outp[outOff + i] = weightA * inA[aOff + i] + weightB * inB[bOff + i];
  }

  /// <summary>Port of <c>ff_adaptive_gain_control</c>.</summary>
  private static void AdaptiveGainControl(float[] outp, int outOff, float[] inp, int inOff,
      float speechEnerg, int size, float alpha, ref float gainMem) {
    var postfilterEnerg = ScalarProduct(inp, inOff, inp, inOff, size);
    var gainScaleFactor = 1.0f;
    var mem = gainMem;

    if (postfilterEnerg != 0f)
      gainScaleFactor = MathF.Sqrt(speechEnerg / postfilterEnerg);

    gainScaleFactor *= 1.0f - alpha;

    for (var i = 0; i < size; ++i) {
      mem = alpha * mem + gainScaleFactor;
      outp[outOff + i] = inp[inOff + i] * mem;
    }
    gainMem = mem;
  }

  /// <summary>Port of <c>ff_acelp_apply_order_2_transfer_function</c>.</summary>
  private static void ApplyOrder2TransferFunction(float[] outp, int outOff, float[] inp, int inOff,
      float[] zeroCoeffs, float[] poleCoeffs, float gain, float[] mem, int n) {
    for (var i = 0; i < n; ++i) {
      var tmp = gain * inp[inOff + i] - poleCoeffs[0] * mem[0] - poleCoeffs[1] * mem[1];
      outp[outOff + i] = tmp + zeroCoeffs[0] * mem[0] + zeroCoeffs[1] * mem[1];
      mem[1] = mem[0];
      mem[0] = tmp;
    }
  }

  /// <summary>Port of <c>ff_tilt_compensation</c>.</summary>
  private static void TiltCompensation(ref float mem, float tilt, float[] samples, int off, int size) {
    var newTiltMem = samples[off + size - 1];
    for (var i = size - 1; i > 0; --i)
      samples[off + i] -= tilt * samples[off + i - 1];
    samples[off] -= tilt * mem;
    mem = newTiltMem;
  }

  /// <summary>Port of the readable path of <c>ff_celp_lp_synthesis_filterf</c>.</summary>
  private static void CelpLpSynthesisFilterf(float[] outp, int outOff, float[] filterCoeffs,
      int fcOff, float[] inp, int inOff, int bufferLength, int filterLength) {
    for (var n = 0; n < bufferLength; ++n) {
      var acc = inp[inOff + n];
      for (var i = 1; i <= filterLength; ++i)
        acc -= filterCoeffs[fcOff + i - 1] * outp[outOff + n - i];
      outp[outOff + n] = acc;
    }
  }

  /// <summary>Port of <c>ff_celp_lp_zero_synthesis_filterf</c>.</summary>
  private static void CelpLpZeroSynthesisFilterf(float[] outp, int outOff, float[] filterCoeffs,
      int fcOff, float[] inp, int inOff, int bufferLength, int filterLength) {
    for (var n = 0; n < bufferLength; ++n) {
      var acc = inp[inOff + n];
      for (var i = 1; i <= filterLength; ++i)
        acc += filterCoeffs[fcOff + i - 1] * inp[inOff + n - i];
      outp[outOff + n] = acc;
    }
  }

  /// <summary>Port of <c>ff_sort_nearly_sorted_floats</c>.</summary>
  private static void SortNearlySortedFloats(float[] vals, int len) {
    for (var i = 0; i < len - 1; ++i)
      for (var j = i; j >= 0 && vals[j] > vals[j + 1]; --j)
        (vals[j], vals[j + 1]) = (vals[j + 1], vals[j]);
  }

  /// <summary>Port of <c>ff_set_min_dist_lsf</c>.</summary>
  private static void SetMinDistLsf(float[] lsf, double minSpacing, int size) {
    var prev = 0.0f;
    for (var i = 0; i < size; ++i)
      prev = lsf[i] = MathF.Max(lsf[i], prev + (float)minSpacing);
  }

  /// <summary>Port of <c>ff_amrwb_lsp2lpc</c> (LSP→LP, double precision).</summary>
  private static void AmrwbLsp2Lpc(double[] lsp, float[] lp, int lpOff, int lpOrder) {
    var lpHalfOrder = lpOrder >> 1;
    var buf = new double[lpHalfOrder + 2];   // qa = buf + 1, qa[-1] valid
    var pa = new double[lpHalfOrder + 1];

    Lsp2Polyf(lsp, 0, pa, lpHalfOrder);
    // qa = buf + 1; lsp2polyf(lsp+1, qa, lp_half_order-1)
    Lsp2Polyf(lsp, 1, buf, 1, lpHalfOrder - 1);
    buf[0] = 0.0; // qa[-1] = 0.0

    // qa[i] is buf[i+1]
    for (int i = 1, j = lpOrder - 1; i < lpHalfOrder; ++i, --j) {
      var paf = pa[i] * (1 + lsp[lpOrder - 1]);
      var qaf = (buf[i + 1] - buf[i - 1]) * (1 - lsp[lpOrder - 1]);
      lp[lpOff + i - 1] = (float)((paf + qaf) * 0.5);
      lp[lpOff + j - 1] = (float)((paf - qaf) * 0.5);
    }

    lp[lpOff + lpHalfOrder - 1] = (float)((1.0 + lsp[lpOrder - 1]) * pa[lpHalfOrder] * 0.5);
    lp[lpOff + lpOrder - 1] = (float)lsp[lpOrder - 1];
  }

  /// <summary>Port of <c>lsp2polyf</c> (lsp.c). <paramref name="lspOff"/> indexes into lsp.</summary>
  private static void Lsp2Polyf(double[] lsp, int lspOff, double[] f, int lpHalfOrder)
    => Lsp2Polyf(lsp, lspOff, f, 0, lpHalfOrder);

  private static void Lsp2Polyf(double[] lsp, int lspOff, double[] f, int fOff, int lpHalfOrder) {
    f[fOff + 0] = 1.0;
    f[fOff + 1] = -2 * lsp[lspOff + 0];
    // C does `lsp -= 2;` then indexes lsp[2*i]; so effectively lsp[lspOff + 2*i - 2].
    for (var i = 2; i <= lpHalfOrder; ++i) {
      var val = -2 * lsp[lspOff + 2 * i - 2];
      f[fOff + i] = val * f[fOff + i - 1] + 2 * f[fOff + i - 2];
      for (var j = i - 1; j > 1; --j)
        f[fOff + j] += f[fOff + j - 1] * val + f[fOff + j - 2];
      f[fOff + 1] += val;
    }
  }

  /// <summary>Port of <c>ff_scalarproduct_float_c</c> (sum of products).</summary>
  private static float ScalarProduct(float[] v1, int o1, float[] v2, int o2, int len) {
    var p = 0f;
    for (var i = 0; i < len; ++i)
      p += v1[o1 + i] * v2[o2 + i];
    return p;
  }

  private static int Clip(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

  // ── internal test surface (exposes pure ported helpers for unit tests) ────
  internal static class Internals {
    /// <summary>Pitch-lag decode (<c>ff_decode_pitch_lag</c>), resolution 6.</summary>
    public static (int LagInt, int LagFrac) DecodePitchLag(int pitchIndex, int subframe,
        bool thirdAsFirst, int resolution, int prevLagInt) {
      var prev = prevLagInt;
      SiprCodec.DecodePitchLag(pitchIndex, subframe, thirdAsFirst, resolution, ref prev,
        out var lagInt, out var lagFrac);
      return (lagInt, lagFrac);
    }

    /// <summary>Fixed-codebook sparse pulse decode (<c>decode_fixed_sparse</c>).</summary>
    public static (int N, int[] X, float[] Y) DecodeFixedSparse(short[] pulses, SiprMode mode,
        bool lowGain) {
      var fs = SiprCodec.DecodeFixedSparse(pulses, mode, lowGain);
      return (fs.N, (int[])fs.X.Clone(), (float[])fs.Y.Clone());
    }

    /// <summary>MA fixed-gain prediction step (<c>ff_amr_set_fixed_gain</c>).</summary>
    public static float AmrSetFixedGain(float fixedGainFactor, float fixedMeanEnergy,
        float[] predictionError, float energyMean, float[] predTable)
      => SiprCodec.AmrSetFixedGain(fixedGainFactor, fixedMeanEnergy, predictionError,
        energyMean, predTable);

    /// <summary><c>lsp2polyf</c> polynomial expansion (lsp.c).</summary>
    public static double[] Lsp2Polyf(double[] lsp, int lpHalfOrder) {
      var f = new double[lpHalfOrder + 1];
      SiprCodec.Lsp2Polyf(lsp, 0, f, lpHalfOrder);
      return f;
    }

    /// <summary><c>ff_amrwb_lsp2lpc</c> LSP→LP conversion (lsp.c).</summary>
    public static float[] AmrwbLsp2Lpc(double[] lsp, int lpOrder) {
      var lp = new float[lpOrder];
      SiprCodec.AmrwbLsp2Lpc(lsp, lp, 0, lpOrder);
      return lp;
    }

    /// <summary>Minimum-distance LSF spacing (<c>ff_set_min_dist_lsf</c>).</summary>
    public static void SetMinDistLsf(float[] lsf, double minSpacing, int size)
      => SiprCodec.SetMinDistLsf(lsf, minSpacing, size);

    /// <summary>Nearly-sorted bubble sort (<c>ff_sort_nearly_sorted_floats</c>).</summary>
    public static void SortNearlySortedFloats(float[] vals, int len)
      => SiprCodec.SortNearlySortedFloats(vals, len);
  }

  private static short ClipInt16(int v) => v switch {
    > short.MaxValue => short.MaxValue,
    < short.MinValue => short.MinValue,
    _ => (short)v,
  };
}

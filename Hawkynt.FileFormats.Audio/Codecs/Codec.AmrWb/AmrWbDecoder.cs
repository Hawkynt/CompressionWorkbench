#pragma warning disable CS1591
using System;

namespace Codec.AmrWb;

/// <summary>
/// AMR wideband decoder, a faithful float port of ffmpeg <c>libavcodec/amrwbdec.c</c> (3GPP TS
/// 26.190 / ITU-T G.722.2) plus the shared ACELP helpers. Decodes the nine active modes
/// (6.60..23.85 kbit/s) including the full high-band synthesis (white-noise excitation via the
/// seeded lagged-Fibonacci generator, ISF extrapolation for 6k60, band-pass/low-pass FIRs),
/// de-emphasis, the 31/400 Hz high-pass pair and the 5/4 upsampling chain. One instance carries the
/// inter-frame state and produces 320 samples (16 kHz, 20 ms) per frame.
/// </summary>
internal sealed class AmrWbDecoder {
  private const int Lp = AmrWbData.LpOrder;        // 16
  private const int Lp16 = AmrWbData.LpOrder16k;   // 20
  private const int Sfr = AmrWbData.SfrSize;       // 64
  private const int Sfr16 = AmrWbData.SfrSize16k;  // 80
  private const int PMax = AmrWbData.PDelayMax;    // 231
  private const int PMin = AmrWbData.PDelayMin;    // 34
  private const int UpsMem = AmrWbData.UpsMemSize; // 24
  private const int UpsFir = AmrWbData.UpsFirSize; // 12
  private const int HbFir = AmrWbData.HbFirSize;   // 30

  private const float PredFactor = (float)AmrWbData.PredFactor;
  private const float MinEnergy = AmrWbData.MinEnergy;
  private const float EnergyMean = AmrWbData.EnergyMean;
  private const float PreemphFac = AmrWbData.PreemphFac;

  private readonly AmrWbFrame _frame = new();
  private AmrWbMode _mode;

  private readonly float[] _isfCur = new float[Lp];
  private readonly float[] _isfQPast = new float[Lp];
  private readonly float[] _isfPastFinal = new float[Lp];
  private readonly double[][] _isp = CreateD(4, Lp);
  private readonly double[] _ispSub4Past = new double[Lp];
  private readonly float[][] _lpCoef = CreateF(4, Lp);

  private int _basePitchLag;
  private int _pitchLagInt;

  private const int ExcOffset = PMax + Lp + 1;
  private readonly float[] _excitationBuf = new float[PMax + Lp + 2 + Sfr];

  private readonly float[] _pitchVector = new float[Sfr];
  private readonly float[] _fixedVector = new float[Sfr];

  private readonly float[] _predictionError = new float[4];
  private readonly float[] _pitchGain = new float[6];
  private readonly float[] _fixedGain = new float[2];

  private float _tiltCoef;
  private int _prevIrFilterNr;
  private float _prevTrGain;

  private readonly float[] _samplesAz = new float[Lp + Sfr];
  private readonly float[] _samplesUp = new float[UpsMem + Sfr];
  private readonly float[] _samplesHb = new float[Lp16 + Sfr16];

  private readonly float[] _hpf31Mem = new float[2];
  private readonly float[] _hpf400Mem = new float[2];
  private readonly float[] _demphMem = new float[1];
  private readonly float[] _bpf67Mem = new float[HbFir];
  private readonly float[] _lpf7Mem = new float[HbFir];

  private readonly AmrWbLfg _prng = new(1);
  private bool _firstFrame = true;

  public AmrWbDecoder() {
    for (var i = 0; i < Lp; i++)
      this._isfPastFinal[i] = AmrWbTables.IsfInit[i] * (1.0f / (1 << 15));
    for (var i = 0; i < 4; i++)
      this._predictionError[i] = MinEnergy;
  }

  private static double[][] CreateD(int a, int b) {
    var r = new double[a][];
    for (var i = 0; i < a; i++) r[i] = new double[b];
    return r;
  }
  private static float[][] CreateF(int a, int b) {
    var r = new float[a][];
    for (var i = 0; i < a; i++) r[i] = new float[b];
    return r;
  }

  /// <summary>
  /// Decodes one frame to 320 samples (16 kHz). <paramref name="payload"/> is the frame payload
  /// after the 1-byte header. For SID/NO_DATA/SpeechLost the decoder emits silence (DTX comfort
  /// noise is not modelled).
  /// </summary>
  public void DecodeFrame(ReadOnlySpan<byte> payload, AmrWbMode mode, Span<short> output) {
    output[..AmrWbData.SamplesPerFrame].Clear();
    if (!AmrWbData.IsSpeech((int)mode))
      return;

    this._mode = mode;
    UnpackBitstream(payload, mode);

    if (mode == AmrWbMode.Mr660)
      DecodeIsfIndices36b();
    else
      DecodeIsfIndices46b();

    IsfAddMeanAndPast();
    SetMinDistLsf(this._isfCur, AmrWbData.MinIsfSpacing, Lp - 1);

    var stabFac = StabilityFactor(this._isfCur, this._isfPastFinal);

    this._isfCur[Lp - 1] *= 2.0f;
    AcelpLsf2Lspd(this._isp[3], this._isfCur, Lp);

    if (this._firstFrame) {
      this._firstFrame = false;
      Array.Copy(this._isp[3], this._ispSub4Past, Lp);
    }
    InterpolateIsp();

    for (var sub = 0; sub < 4; sub++)
      AmrwbLsp2Lpc(this._isp[sub], this._lpCoef[sub], Lp);

    var bufOut = new float[AmrWbData.SamplesPerFrame];

    for (var sub = 0; sub < 4; sub++) {
      var subBufOff = sub * Sfr16;
      var synthExc = new float[Sfr];
      var hbExc = new float[Sfr16];
      var hbSamples = new float[Sfr16];
      var spare = new float[Sfr];

      DecodePitchVector(sub);
      DecodeFixedVector(this._fixedVector, sub, mode);
      PitchSharpening(this._fixedVector);

      DecodeGains(this._frame.VqGain(sub), mode, out var fixedGainFactor, out var pitchGain);
      this._pitchGain[0] = pitchGain;

      var meanEnergy = DotProduct(this._fixedVector, 0, this._fixedVector, 0, Sfr) / Sfr;
      this._fixedGain[0] = AmrSetFixedGain(fixedGainFactor, meanEnergy, this._predictionError,
        EnergyMean, AmrWbTables.EnergyPredFac);

      var voiceFac = VoiceFactor(this._pitchVector, this._pitchGain[0], this._fixedVector, this._fixedGain[0]);
      this._tiltCoef = voiceFac * 0.25f + 0.25f;

      for (var i = 0; i < Sfr; i++) {
        this._excitationBuf[ExcOffset + i] *= this._pitchGain[0];
        this._excitationBuf[ExcOffset + i] += this._fixedGain[0] * this._fixedVector[i];
        this._excitationBuf[ExcOffset + i] = MathF.Truncate(this._excitationBuf[ExcOffset + i]);
      }

      var synthFixedGain = NoiseEnhancer(this._fixedGain[0], ref this._prevTrGain, voiceFac, stabFac);
      var synthFixedVector = AntiSparseness(this._fixedVector, spare);
      PitchEnhancer(synthFixedVector, voiceFac);

      Synthesis(this._lpCoef[sub], synthExc, synthFixedGain, synthFixedVector);

      // de-emphasis into samples_up[UpsMem..]
      DeEmphasis(this._samplesUp, UpsMem, this._samplesAz, Lp, PreemphFac, this._demphMem);

      ApplyOrder2(this._samplesUp, UpsMem, this._samplesUp, UpsMem, AmrWbTables.HpfZeros,
        AmrWbTables.Hpf31Poles, AmrWbData.Hpf31Gain, this._hpf31Mem, Sfr);

      UpsampleByFive4(bufOut, subBufOff, this._samplesUp, UpsFir, Sfr16);

      // high band 6.4-7.0 kHz
      ApplyOrder2(hbSamples, 0, this._samplesUp, UpsMem, AmrWbTables.HpfZeros,
        AmrWbTables.Hpf400Poles, AmrWbData.Hpf400Gain, this._hpf400Mem, Sfr);

      var hbGain = FindHbGain(hbSamples, this._frame.HbGain(sub), this._frame.Vad);
      ScaledHbExcitation(hbExc, synthExc, hbGain);
      HbSynthesis(sub, this._samplesHb, Lp16, hbExc, this._isfCur, this._isfPastFinal);

      HbFirFilter(hbSamples, AmrWbTables.Bpf6_7Coef, this._bpf67Mem, this._samplesHb, Lp16);
      if (mode == AmrWbMode.Mr2385)
        HbFirFilter(hbSamples, AmrWbTables.Lpf7Coef, this._lpf7Mem, hbSamples, 0);

      for (var i = 0; i < Sfr16; i++)
        bufOut[subBufOff + i] = (bufOut[subBufOff + i] + hbSamples[i]) * (1.0f / (1 << 15));

      UpdateSubState();
    }

    Array.Copy(this._isp[3], this._ispSub4Past, Lp);
    Array.Copy(this._isfCur, this._isfPastFinal, Lp);

    for (var i = 0; i < AmrWbData.SamplesPerFrame; i++) {
      var v = bufOut[i] * 32768.0f;
      output[i] = (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);
    }
  }

  // ---------------------------------------------------------------------------------------------
  private void UnpackBitstream(ReadOnlySpan<byte> data, AmrWbMode mode) {
    this._frame.Clear();
    var ord = AmrWbTables.BitOrderingsByMode[(int)mode];
    var p = 0;
    int fieldSize;
    while ((fieldSize = ord[p++]) != 0) {
      var fieldIndex = ord[p++];
      var field = 0;
      while (fieldSize-- > 0) {
        int bit = ord[p++];
        field <<= 1;
        field |= (data[bit >> 3] >> (bit & 7)) & 1;
      }
      this._frame.Words[fieldIndex] = field;
    }
  }

  // ---------------------------------------------------------------------------------------------
  // ISF decode
  // ---------------------------------------------------------------------------------------------
  private void DecodeIsfIndices36b() {
    var f = this._frame;
    var isf = this._isfCur;
    for (var i = 0; i < 9; i++)
      isf[i] = AmrWbTables.Dico1Isf[f.IspId(0)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 7; i++)
      isf[i + 9] = AmrWbTables.Dico2Isf[f.IspId(1)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 5; i++)
      isf[i] += AmrWbTables.Dico21Isf36b[f.IspId(2)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 4; i++)
      isf[i + 5] += AmrWbTables.Dico22Isf36b[f.IspId(3)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 7; i++)
      isf[i + 9] += AmrWbTables.Dico23Isf36b[f.IspId(4)][i] * (1.0f / (1 << 15));
  }

  private void DecodeIsfIndices46b() {
    var f = this._frame;
    var isf = this._isfCur;
    for (var i = 0; i < 9; i++)
      isf[i] = AmrWbTables.Dico1Isf[f.IspId(0)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 7; i++)
      isf[i + 9] = AmrWbTables.Dico2Isf[f.IspId(1)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 3; i++)
      isf[i] += AmrWbTables.Dico21Isf[f.IspId(2)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 3; i++)
      isf[i + 3] += AmrWbTables.Dico22Isf[f.IspId(3)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 3; i++)
      isf[i + 6] += AmrWbTables.Dico23Isf[f.IspId(4)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 3; i++)
      isf[i + 9] += AmrWbTables.Dico24Isf[f.IspId(5)][i] * (1.0f / (1 << 15));
    for (var i = 0; i < 4; i++)
      isf[i + 12] += AmrWbTables.Dico25Isf[f.IspId(6)][i] * (1.0f / (1 << 15));
  }

  private void IsfAddMeanAndPast() {
    for (var i = 0; i < Lp; i++) {
      var tmp = this._isfCur[i];
      this._isfCur[i] += AmrWbTables.IsfMean[i] * (1.0f / (1 << 15));
      this._isfCur[i] += PredFactor * this._isfQPast[i];
      this._isfQPast[i] = tmp;
    }
  }

  private void InterpolateIsp() {
    for (var k = 0; k < 3; k++) {
      var c = AmrWbTables.IsfpInter[k];
      for (var i = 0; i < Lp; i++)
        this._isp[k][i] = (1.0 - c) * this._ispSub4Past[i] + c * this._isp[3][i];
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Pitch
  // ---------------------------------------------------------------------------------------------
  private void DecodePitchLagHigh(out int lagInt, out int lagFrac, int idx, int subframe) {
    if (subframe is 0 or 2) {
      if (idx < 376) {
        lagInt = (idx + 137) >> 2;
        lagFrac = idx - (lagInt << 2) + 136;
      } else if (idx < 440) {
        lagInt = (idx + 257 - 376) >> 1;
        lagFrac = (idx - (lagInt << 1) + 256 - 376) * 2;
      } else {
        lagInt = idx - 280;
        lagFrac = 0;
      }
      this._basePitchLag = Clip(lagInt - 8 - (lagFrac < 0 ? 1 : 0), PMin, PMax - 15);
    } else {
      lagInt = (idx + 1) >> 2;
      lagFrac = idx - (lagInt << 2);
      lagInt += this._basePitchLag;
    }
  }

  private void DecodePitchLagLow(out int lagInt, out int lagFrac, int idx, int subframe, AmrWbMode mode) {
    if (subframe == 0 || (subframe == 2 && mode != AmrWbMode.Mr660)) {
      if (idx < 116) {
        lagInt = (idx + 69) >> 1;
        lagFrac = (idx - (lagInt << 1) + 68) * 2;
      } else {
        lagInt = idx - 24;
        lagFrac = 0;
      }
      this._basePitchLag = Clip(lagInt - 8 - (lagFrac < 0 ? 1 : 0), PMin, PMax - 15);
    } else {
      lagInt = (idx + 1) >> 1;
      lagFrac = (idx - (lagInt << 1)) * 2;
      lagInt += this._basePitchLag;
    }
  }

  private void DecodePitchVector(int subframe) {
    int lagInt, lagFrac;
    var mode = this._mode;
    if (mode <= AmrWbMode.Mr885)
      DecodePitchLagLow(out lagInt, out lagFrac, this._frame.Adap(subframe), subframe, mode);
    else
      DecodePitchLagHigh(out lagInt, out lagFrac, this._frame.Adap(subframe), subframe);

    this._pitchLagInt = lagInt;
    lagInt += lagFrac > 0 ? 1 : 0;

    AcelpInterpolateF(this._excitationBuf, ExcOffset, this._excitationBuf, ExcOffset + 1 - lagInt,
      AmrWbTables.AcInter, 4, lagFrac + (lagFrac > 0 ? 0 : 4), Lp, Sfr + 1);

    if (this._frame.Ltp(subframe) != 0) {
      Array.Copy(this._excitationBuf, ExcOffset, this._pitchVector, 0, Sfr);
    } else {
      for (var i = 0; i < Sfr; i++)
        this._pitchVector[i] = 0.18f * this._excitationBuf[ExcOffset + i - 1]
          + 0.64f * this._excitationBuf[ExcOffset + i]
          + 0.18f * this._excitationBuf[ExcOffset + i + 1];
      Array.Copy(this._pitchVector, 0, this._excitationBuf, ExcOffset, Sfr);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Algebraic codebook (5-track)
  // ---------------------------------------------------------------------------------------------
  private static int BitStr(int x, int lsb, int len) => (x >> lsb) & ((1 << len) - 1);
  private static int BitPos(int x, int p) => (x >> p) & 1;

  private static void Decode1pTrack(int[] outv, int o, int code, int m, int off) {
    var pos = BitStr(code, 0, m) + off;
    outv[o] = BitPos(code, m) != 0 ? -pos : pos;
  }
  private static void Decode2pTrack(int[] outv, int o, int code, int m, int off) {
    var pos0 = BitStr(code, m, m) + off;
    var pos1 = BitStr(code, 0, m) + off;
    outv[o] = BitPos(code, 2 * m) != 0 ? -pos0 : pos0;
    outv[o + 1] = BitPos(code, 2 * m) != 0 ? -pos1 : pos1;
    outv[o + 1] = pos0 > pos1 ? -outv[o + 1] : outv[o + 1];
  }
  private static void Decode3pTrack(int[] outv, int o, int code, int m, int off) {
    var half2p = BitPos(code, 2 * m - 1) << (m - 1);
    Decode2pTrack(outv, o, BitStr(code, 0, 2 * m - 1), m - 1, off + half2p);
    Decode1pTrack(outv, o + 2, BitStr(code, 2 * m, m + 1), m, off);
  }
  private static void Decode4pTrack(int[] outv, int o, int code, int m, int off) {
    var bOffset = 1 << (m - 1);
    switch (BitStr(code, 4 * m - 2, 2)) {
      case 0: {
        var half4p = BitPos(code, 4 * m - 3) << (m - 1);
        var subhalf2p = BitPos(code, 2 * m - 3) << (m - 2);
        Decode2pTrack(outv, o, BitStr(code, 0, 2 * m - 3), m - 2, off + half4p + subhalf2p);
        Decode2pTrack(outv, o + 2, BitStr(code, 2 * m - 2, 2 * m - 1), m - 1, off + half4p);
        break;
      }
      case 1:
        Decode1pTrack(outv, o, BitStr(code, 3 * m - 2, m), m - 1, off);
        Decode3pTrack(outv, o + 1, BitStr(code, 0, 3 * m - 2), m - 1, off + bOffset);
        break;
      case 2:
        Decode2pTrack(outv, o, BitStr(code, 2 * m - 1, 2 * m - 1), m - 1, off);
        Decode2pTrack(outv, o + 2, BitStr(code, 0, 2 * m - 1), m - 1, off + bOffset);
        break;
      case 3:
        Decode3pTrack(outv, o, BitStr(code, m, 3 * m - 2), m - 1, off);
        Decode1pTrack(outv, o + 3, BitStr(code, 0, m), m - 1, off + bOffset);
        break;
    }
  }
  private static void Decode5pTrack(int[] outv, int o, int code, int m, int off) {
    var half3p = BitPos(code, 5 * m - 1) << (m - 1);
    Decode3pTrack(outv, o, BitStr(code, 2 * m + 1, 3 * m - 2), m - 1, off + half3p);
    Decode2pTrack(outv, o + 3, BitStr(code, 0, 2 * m + 1), m, off);
  }
  private static void Decode6pTrack(int[] outv, int o, int code, int m, int off) {
    var bOffset = 1 << (m - 1);
    var halfMore = BitPos(code, 6 * m - 5) << (m - 1);
    var halfOther = bOffset - halfMore;
    switch (BitStr(code, 6 * m - 4, 2)) {
      case 0:
        Decode1pTrack(outv, o, BitStr(code, 0, m), m - 1, off + halfMore);
        Decode5pTrack(outv, o + 1, BitStr(code, m, 5 * m - 5), m - 1, off + halfMore);
        break;
      case 1:
        Decode1pTrack(outv, o, BitStr(code, 0, m), m - 1, off + halfOther);
        Decode5pTrack(outv, o + 1, BitStr(code, m, 5 * m - 5), m - 1, off + halfMore);
        break;
      case 2:
        Decode2pTrack(outv, o, BitStr(code, 0, 2 * m - 1), m - 1, off + halfOther);
        Decode4pTrack(outv, o + 2, BitStr(code, 2 * m - 1, 4 * m - 4), m - 1, off + halfMore);
        break;
      case 3:
        Decode3pTrack(outv, o, BitStr(code, 3 * m - 2, 3 * m - 2), m - 1, off);
        Decode3pTrack(outv, o + 3, BitStr(code, 0, 3 * m - 2), m - 1, off + bOffset);
        break;
    }
  }

  private void DecodeFixedVector(float[] fixedVector, int sub, AmrWbMode mode) {
    var sigPos = new int[4][];
    for (var k = 0; k < 4; k++) sigPos[k] = new int[6];
    var spacing = mode == AmrWbMode.Mr660 ? 2 : 4;
    var f = this._frame;

    switch (mode) {
      case AmrWbMode.Mr660:
        for (var i = 0; i < 2; i++) Decode1pTrack(sigPos[i], 0, f.PulIl(sub, i), 5, 1);
        break;
      case AmrWbMode.Mr885:
        for (var i = 0; i < 4; i++) Decode1pTrack(sigPos[i], 0, f.PulIl(sub, i), 4, 1);
        break;
      case AmrWbMode.Mr1265:
        for (var i = 0; i < 4; i++) Decode2pTrack(sigPos[i], 0, f.PulIl(sub, i), 4, 1);
        break;
      case AmrWbMode.Mr1425:
        for (var i = 0; i < 2; i++) Decode3pTrack(sigPos[i], 0, f.PulIl(sub, i), 4, 1);
        for (var i = 2; i < 4; i++) Decode2pTrack(sigPos[i], 0, f.PulIl(sub, i), 4, 1);
        break;
      case AmrWbMode.Mr1585:
        for (var i = 0; i < 4; i++) Decode3pTrack(sigPos[i], 0, f.PulIl(sub, i), 4, 1);
        break;
      case AmrWbMode.Mr1825:
        for (var i = 0; i < 4; i++) Decode4pTrack(sigPos[i], 0, f.PulIl(sub, i) + (f.PulIh(sub, i) << 14), 4, 1);
        break;
      case AmrWbMode.Mr1985:
        for (var i = 0; i < 2; i++) Decode5pTrack(sigPos[i], 0, f.PulIl(sub, i) + (f.PulIh(sub, i) << 10), 4, 1);
        for (var i = 2; i < 4; i++) Decode4pTrack(sigPos[i], 0, f.PulIl(sub, i) + (f.PulIh(sub, i) << 14), 4, 1);
        break;
      case AmrWbMode.Mr2305:
      case AmrWbMode.Mr2385:
        for (var i = 0; i < 4; i++) Decode6pTrack(sigPos[i], 0, f.PulIl(sub, i) + (f.PulIh(sub, i) << 11), 4, 1);
        break;
    }

    Array.Clear(fixedVector, 0, Sfr);
    for (var i = 0; i < 4; i++)
      for (var j = 0; j < AmrWbTables.PulsesNbPerModeTr[(int)mode][i]; j++) {
        var pos = (Math.Abs(sigPos[i][j]) - 1) * spacing + i;
        fixedVector[pos] += sigPos[i][j] < 0 ? -1.0f : 1.0f;
      }
  }

  private static void DecodeGains(int vqGain, AmrWbMode mode, out float fixedGainFactor, out float pitchGain) {
    var gains = mode <= AmrWbMode.Mr885 ? AmrWbTables.QuaGain6b[vqGain] : AmrWbTables.QuaGain7b[vqGain];
    pitchGain = gains[0] * (1.0f / (1 << 14));
    fixedGainFactor = gains[1] * (1.0f / (1 << 11));
  }

  private void PitchSharpening(float[] fixedVector) {
    for (var i = Sfr - 1; i != 0; i--)
      fixedVector[i] -= fixedVector[i - 1] * this._tiltCoef;
    for (var i = this._pitchLagInt; i < Sfr; i++)
      fixedVector[i] += fixedVector[i - this._pitchLagInt] * 0.85f;
  }

  private static float VoiceFactor(float[] pVec, float pGain, float[] fVec, float fGain) {
    var pEner = (double)DotProduct(pVec, 0, pVec, 0, Sfr) * pGain * pGain;
    var fEner = (double)DotProduct(fVec, 0, fVec, 0, Sfr) * fGain * fGain;
    return (float)((pEner - fEner) / (pEner + fEner + 0.01));
  }

  private float[] AntiSparseness(float[] fixedVector, float[] buf) {
    if (this._mode > AmrWbMode.Mr885)
      return fixedVector;

    int irFilterNr;
    if (this._pitchGain[0] < 0.6f) irFilterNr = 0;
    else if (this._pitchGain[0] < 0.9f) irFilterNr = 1;
    else irFilterNr = 2;

    if (this._fixedGain[0] > 3.0f * this._fixedGain[1]) {
      if (irFilterNr < 2) irFilterNr++;
    } else {
      var count = 0;
      for (var i = 0; i < 6; i++)
        if (this._pitchGain[i] < 0.6f) count++;
      if (count > 2) irFilterNr = 0;
      if (irFilterNr > this._prevIrFilterNr + 1) irFilterNr--;
    }

    this._prevIrFilterNr = irFilterNr;
    irFilterNr += this._mode == AmrWbMode.Mr885 ? 1 : 0;

    var result = fixedVector;
    if (irFilterNr < 2) {
      var coef = IrFiltersLookup[irFilterNr];
      Array.Clear(buf, 0, Sfr);
      for (var i = 0; i < Sfr; i++)
        if (fixedVector[i] != 0)
          CelpCircAddF(buf, buf, coef, i, fixedVector[i], Sfr);
      result = buf;
    }
    return result;
  }

  private static readonly float[][] IrFiltersLookup = { AmrWbTables.IrFilterStr, AmrWbTables.IrFilterMid };

  private static float StabilityFactor(float[] isf, float[] isfPast) {
    float acc = 0;
    for (var i = 0; i < Lp - 1; i++)
      acc += (isf[i] - isfPast[i]) * (isf[i] - isfPast[i]);
    return MathF.Max(0.0f, 1.25f - acc * 0.8f * 512.0f);
  }

  private static float NoiseEnhancer(float fixedGain, ref float prevTrGain, float voiceFac, float stabFac) {
    var smFac = 0.5f * (1 - voiceFac) * stabFac;
    float g0;
    if (fixedGain < prevTrGain)
      g0 = MathF.Min(prevTrGain, fixedGain + fixedGain * (6226 * (1.0f / (1 << 15))));
    else
      g0 = MathF.Max(prevTrGain, fixedGain * (27536 * (1.0f / (1 << 15))));
    prevTrGain = g0;
    return smFac * g0 + (1 - smFac) * fixedGain;
  }

  private static void PitchEnhancer(float[] fixedVector, float voiceFac) {
    var cpe = 0.125f * (1 + voiceFac);
    var last = fixedVector[0];
    fixedVector[0] -= cpe * fixedVector[1];
    for (var i = 1; i < Sfr - 1; i++) {
      var cur = fixedVector[i];
      fixedVector[i] -= cpe * (last + fixedVector[i + 1]);
      last = cur;
    }
    fixedVector[Sfr - 1] -= cpe * last;
  }

  // ---------------------------------------------------------------------------------------------
  // Synthesis + post
  // ---------------------------------------------------------------------------------------------
  private void Synthesis(float[] lpc, float[] excitation, float fixedGain, float[] fixedVector) {
    WeightedVectorSumF(excitation, this._pitchVector, fixedVector, this._pitchGain[0], fixedGain, Sfr);
    if (this._pitchGain[0] > 0.5f && this._mode <= AmrWbMode.Mr885) {
      var energy = DotProduct(excitation, 0, excitation, 0, Sfr);
      var pitchFactor = 0.25f * this._pitchGain[0] * this._pitchGain[0];
      for (var i = 0; i < Sfr; i++)
        excitation[i] += pitchFactor * this._pitchVector[i];
      ScaleVectorToGivenSumOfSquares(excitation, excitation, energy, Sfr);
    }
    CelpLpSynthesisFilterF(this._samplesAz, Lp, lpc, excitation, 0, Sfr, Lp);
  }

  private static void DeEmphasis(float[] outv, int outOff, float[] inv, int inOff, float m, float[] mem) {
    outv[outOff] = inv[inOff] + m * mem[0];
    for (var i = 1; i < Sfr; i++)
      outv[outOff + i] = inv[inOff + i] + outv[outOff + i - 1] * m;
    mem[0] = outv[outOff + Sfr - 1];
  }

  private static void UpsampleByFive4(float[] outv, int outOff, float[] inv, int inOff, int oSize) {
    // in0 = in - UPS_FIR_SIZE + 1; samples indexed from in[int_part]
    var in0 = inOff - UpsFir + 1;
    var i = 0;
    var intPart = 0;
    for (var j = 0; j < oSize / 5; j++) {
      outv[outOff + i] = inv[inOff + intPart];
      var fracPart = 4;
      i++;
      for (var k = 1; k < 5; k++) {
        outv[outOff + i] = DotProduct(inv, in0 + intPart, AmrWbTables.UpsampleFir[4 - fracPart], 0, UpsMem);
        intPart++;
        fracPart--;
        i++;
      }
    }
  }

  private float FindHbGain(float[] synth, int hbIdx, int vad) {
    if (this._mode == AmrWbMode.Mr2385)
      return AmrWbTables.QuaHbGain[hbIdx] * (1.0f / (1 << 14));
    var wsp = vad > 0 ? 1 : 0;
    var tmp = DotProduct(synth, 0, synth, 1, Sfr - 1);
    float tilt;
    if (tmp > 0)
      tilt = tmp / DotProduct(synth, 0, synth, 0, Sfr);
    else
      tilt = 0;
    return Math.Clamp((1.0f - tilt) * (1.25f - 0.25f * wsp), 0.1f, 1.0f);
  }

  private void ScaledHbExcitation(float[] hbExc, float[] synthExc, float hbGain) {
    var energy = DotProduct(synthExc, 0, synthExc, 0, Sfr);
    for (var i = 0; i < Sfr16; i++)
      hbExc[i] = 32768.0f - (ushort)this._prng.Get();
    ScaleVectorToGivenSumOfSquares(hbExc, hbExc, energy * hbGain * hbGain, Sfr16);
  }

  private static float AutoCorrelation(float[] diffIsf, float mean, int lag) {
    float sum = 0;
    for (var i = 7; i < Lp - 2; i++) {
      var prod = (diffIsf[i] - mean) * (diffIsf[i - lag] - mean);
      sum += prod * prod;
    }
    return sum;
  }

  private static void ExtrapolateIsf(float[] isf) {
    var diffIsf = new float[Lp - 2];
    isf[Lp16 - 1] = isf[Lp - 1];
    for (var i = 0; i < Lp - 2; i++)
      diffIsf[i] = isf[i + 1] - isf[i];

    float diffMean = 0;
    for (var i = 2; i < Lp - 2; i++)
      diffMean += diffIsf[i] * (1.0f / (Lp - 4));

    var corrLag = new float[3];
    var iMaxCorr = 0;
    for (var i = 0; i < 3; i++) {
      corrLag[i] = AutoCorrelation(diffIsf, diffMean, i + 2);
      if (corrLag[i] > corrLag[iMaxCorr]) iMaxCorr = i;
    }
    iMaxCorr++;

    for (var i = Lp - 1; i < Lp16 - 1; i++)
      isf[i] = isf[i - 1] + isf[i - 1 - iMaxCorr] - isf[i - 2 - iMaxCorr];

    var est = 7965 + (isf[2] - isf[3] - isf[4]) / 6.0f;
    var scale = 0.5f * (MathF.Min(est, 7600) - isf[Lp - 2]) / (isf[Lp16 - 2] - isf[Lp - 2]);

    for (int i = Lp - 1, j = 0; i < Lp16 - 1; i++, j++)
      diffIsf[j] = scale * (isf[i] - isf[i - 1]);

    for (var i = 1; i < Lp16 - Lp; i++)
      if (diffIsf[i] + diffIsf[i - 1] < 5.0f) {
        if (diffIsf[i] > diffIsf[i - 1])
          diffIsf[i - 1] = 5.0f - diffIsf[i];
        else
          diffIsf[i] = 5.0f - diffIsf[i - 1];
      }

    for (int i = Lp - 1, j = 0; i < Lp16 - 1; i++, j++)
      isf[i] = isf[i - 1] + diffIsf[j] * (1.0f / (1 << 15));

    for (var i = 0; i < Lp16 - 1; i++)
      isf[i] *= 0.8f;
  }

  private static void LpcWeighting(float[] outv, float[] lpc, float gamma, int size) {
    var fac = gamma;
    for (var i = 0; i < size; i++) {
      outv[i] = lpc[i] * fac;
      fac *= gamma;
    }
  }

  private void HbSynthesis(int subframe, float[] samples, int sampOff, float[] exc, float[] isf, float[] isfPast) {
    var hbLpc = new float[Lp16];
    if (this._mode == AmrWbMode.Mr660) {
      var eIsf = new float[Lp16];
      var eIsp = new double[Lp16];
      WeightedVectorSumF(eIsf, isfPast, isf, AmrWbTables.IsfpInter[subframe], 1.0f - AmrWbTables.IsfpInter[subframe], Lp);
      ExtrapolateIsf(eIsf);
      eIsf[Lp16 - 1] *= 2.0f;
      AcelpLsf2Lspd(eIsp, eIsf, Lp16);
      AmrwbLsp2Lpc(eIsp, hbLpc, Lp16);
      LpcWeighting(hbLpc, hbLpc, 0.9f, Lp16);
    } else {
      LpcWeighting(hbLpc, this._lpCoef[subframe], 0.6f, Lp);
    }
    CelpLpSynthesisFilterF(samples, sampOff, hbLpc, exc, 0, Sfr16, this._mode == AmrWbMode.Mr660 ? Lp16 : Lp);
  }

  private static void HbFirFilter(float[] outv, float[] firCoef, float[] mem, float[] inv, int inOff) {
    var data = new float[Sfr16 + HbFir];
    Array.Copy(mem, data, HbFir);
    Array.Copy(inv, inOff, data, HbFir, Sfr16);
    for (var i = 0; i < Sfr16; i++) {
      float acc = 0;
      for (var j = 0; j <= HbFir; j++)
        acc += data[i + j] * firCoef[j];
      outv[i] = acc;
    }
    Array.Copy(data, Sfr16, mem, 0, HbFir);
  }

  private void UpdateSubState() {
    Array.Copy(this._excitationBuf, Sfr, this._excitationBuf, 0, PMax + Lp + 1);
    Array.Copy(this._pitchGain, 0, this._pitchGain, 1, 5);
    this._fixedGain[1] = this._fixedGain[0];
    Array.Copy(this._samplesAz, Sfr, this._samplesAz, 0, Lp);
    Array.Copy(this._samplesUp, Sfr, this._samplesUp, 0, UpsMem);
    Array.Copy(this._samplesHb, Sfr16, this._samplesHb, 0, Lp16);
  }

  // ---------------------------------------------------------------------------------------------
  // Shared primitives
  // ---------------------------------------------------------------------------------------------
  private static int Clip(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

  private static void WeightedVectorSumF(float[] outv, float[] a, float[] b, float wa, float wb, int n) {
    for (var i = 0; i < n; i++)
      outv[i] = wa * a[i] + wb * b[i];
  }

  private static float DotProduct(float[] a, int ao, float[] b, int bo, int n) {
    float s = 0;
    for (var i = 0; i < n; i++)
      s += a[ao + i] * b[bo + i];
    return s;
  }

  private static void SetMinDistLsf(float[] lsf, double minSpacing, int size) {
    float prev = 0;
    for (var i = 0; i < size; i++)
      prev = lsf[i] = MathF.Max(lsf[i], prev + (float)minSpacing);
  }

  private static void AcelpLsf2Lspd(double[] lsp, float[] lsf, int order) {
    for (var i = 0; i < order; i++)
      lsp[i] = Math.Cos(2.0 * Math.PI * lsf[i]);
  }

  // ff_amrwb_lsp2lpc (lsp.c)
  private static void Lsp2Polyf(double[] lsp, int lspOff, double[] f, int halfOrder) {
    f[0] = 1.0;
    f[1] = -2.0 * lsp[lspOff];
    var lp = lspOff - 2;
    for (var i = 2; i <= halfOrder; i++) {
      var val = -2.0 * lsp[lp + 2 * i];
      f[i] = val * f[i - 1] + 2 * f[i - 2];
      for (var j = i - 1; j > 1; j--)
        f[j] += f[j - 1] * val + f[j - 2];
      f[1] += val;
    }
  }

  private static void AmrwbLsp2Lpc(double[] lsp, float[] lp, int order) {
    var half = order >> 1;
    var buf = new double[half + 1];
    var pa = new double[half + 1];
    // qa = buf + 1; qa[-1] = 0
    Lsp2Polyf(lsp, 0, pa, half);
    Lsp2PolyfQ(lsp, 1, buf, half - 1);

    for (int i = 1, j = order - 1; i < half; i++, j--) {
      var paf = pa[i] * (1 + lsp[order - 1]);
      var qaf = (buf[i + 1] - buf[i - 1]) * (1 - lsp[order - 1]);
      lp[i - 1] = (float)((paf + qaf) * 0.5);
      lp[j - 1] = (float)((paf - qaf) * 0.5);
    }
    lp[half - 1] = (float)((1.0 + lsp[order - 1]) * pa[half] * 0.5);
    lp[order - 1] = (float)lsp[order - 1];
  }

  // qa is buf+1, so qa[k] = buf[k+1]; lsp2polyf writes f[0..halfOrder]
  private static void Lsp2PolyfQ(double[] lsp, int lspOff, double[] buf, int halfOrder) {
    buf[0] = 0.0; // qa[-1]
    // qa[0]=f[0]=1 -> buf[1]; qa[i]=f[i] -> buf[i+1]
    var f = new double[halfOrder + 1];
    f[0] = 1.0;
    f[1] = -2.0 * lsp[lspOff];
    var lp = lspOff - 2;
    for (var i = 2; i <= halfOrder; i++) {
      var val = -2.0 * lsp[lp + 2 * i];
      f[i] = val * f[i - 1] + 2 * f[i - 2];
      for (var j = i - 1; j > 1; j--)
        f[j] += f[j - 1] * val + f[j - 2];
      f[1] += val;
    }
    for (var i = 0; i <= halfOrder; i++)
      buf[i + 1] = f[i];
  }

  private static void AcelpInterpolateF(float[] outv, int outOff, float[] inv, int inOff,
    float[] filter, int precision, int fracPos, int filterLength, int length) {
    for (var n = 0; n < length; n++) {
      var idx = 0;
      float v = 0;
      for (var i = 0; i < filterLength;) {
        v += inv[inOff + n + i] * filter[idx + fracPos];
        idx += precision;
        i++;
        v += inv[inOff + n - i] * filter[idx - fracPos];
      }
      outv[outOff + n] = v;
    }
  }

  private static void CelpLpSynthesisFilterF(float[] outv, int outStart, float[] coeffs, float[] inv, int inStart, int len, int order) {
    for (var n = 0; n < len; n++) {
      var acc = inv[inStart + n];
      for (var i = 1; i <= order; i++)
        acc -= coeffs[i - 1] * outv[outStart + n - i];
      outv[outStart + n] = acc;
    }
  }

  private static void CelpCircAddF(float[] outv, float[] inv, float[] lagged, int lag, float fac, int n) {
    int k;
    for (k = 0; k < lag; k++)
      outv[k] = inv[k] + fac * lagged[n + k - lag];
    for (; k < n; k++)
      outv[k] = inv[k] + fac * lagged[k - lag];
  }

  private static void ApplyOrder2(float[] outv, int outOff, float[] inv, int inOff, float[] zero, float[] pole, float gain, float[] mem, int n) {
    for (var i = 0; i < n; i++) {
      var tmp = gain * inv[inOff + i] - pole[0] * mem[0] - pole[1] * mem[1];
      outv[outOff + i] = tmp + zero[0] * mem[0] + zero[1] * mem[1];
      mem[1] = mem[0];
      mem[0] = tmp;
    }
  }

  private static void ScaleVectorToGivenSumOfSquares(float[] outv, float[] inv, float sumSq, int n) {
    var sf = DotProduct(inv, 0, inv, 0, n);
    if (sf != 0)
      sf = MathF.Sqrt(sumSq / sf);
    for (var i = 0; i < n; i++)
      outv[i] = inv[i] * sf;
  }

  private static float AmrSetFixedGain(float fixedGainFactor, float fixedMeanEnergy,
    float[] predictionError, float energyMean, float[] predTable) {
    var dot = 0.0f;
    for (var i = 0; i < 4; i++)
      dot += predTable[i] * predictionError[i];
    var val = (float)(fixedGainFactor * Math.Pow(10.0, 0.05 * (dot + energyMean))
      / Math.Sqrt(fixedMeanEnergy != 0 ? fixedMeanEnergy : 1.0));
    Array.Copy(predictionError, 1, predictionError, 0, 3);
    predictionError[3] = 20.0f * MathF.Log10(fixedGainFactor);
    return val;
  }
}

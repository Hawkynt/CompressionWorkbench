#pragma warning disable CS1591
using System;

namespace Codec.AmrNb;

/// <summary>
/// AMR narrowband floating-point decoder, a faithful port of ffmpeg <c>libavcodec/amrnbdec.c</c>
/// (3GPP TS 26.090) together with the shared ACELP helpers it pulls in from
/// <c>acelp_vectors.c</c>, <c>acelp_filters.c</c>, <c>celp_filters.c</c>,
/// <c>acelp_pitch_delay.c</c> and <c>lsp.c</c>. Like ffmpeg this uses floats, so it is not
/// bit-exact with the 3GPP fixed-point reference (the upstream file documents a PSNR of 30..80 dB),
/// but it is a structurally exact reproduction of the algorithm. One decoder instance carries the
/// inter-frame state and decodes a sequence of 20 ms frames to 16-bit PCM at 8 kHz.
/// </summary>
internal sealed class AmrNbDecoder {
  private const int Lp = AmrNbData.LpOrder;             // 10
  private const int SubSize = AmrNbData.SubframeSize;   // 40
  private const int PitchDelayMax = AmrNbData.PitchDelayMax; // 143
  private const int PitchDelayMin = AmrNbData.PitchDelayMin; // 20

  private const double LsfRFac = 8000.0 / 32768.0;
  private const double MinLsfSpacing = 50.0488 / 8000.0;
  private const int PitchLagMinMode12k2 = 18;
  private const double PredFacMode12k2 = 0.65;
  private const float MinEnergy = -14.0f;
  private const float SharpMax = 0.79449462890625f;
  private const float SampleBound = 32768.0f;
  private const float SampleScale = 2.0f / 32768.0f;
  private const int TiltResponse = 22;
  private const float TiltGammaT = 0.8f;
  private const float AgcAlpha = 0.9f;

  // highpass (ffmpeg amrnbdata.h highpass_zeros/poles/gain)
  private static readonly float[] HighpassZeros = { -2.0f, 1.0f };
  private static readonly float[] HighpassPoles = { -1.933105469f, 0.935913085f };
  private const float HighpassGain = 0.939819335f;

  private readonly AmrNbFrame _frame = new();
  private AmrNbMode _curMode;

  private readonly int[] _prevLsfR = new int[Lp];
  private readonly double[][] _lsp = Create2D(4, Lp);
  private readonly double[] _prevLspSub4 = new double[Lp];

  private readonly float[][] _lsfQ = CreateF2D(4, Lp);
  private readonly float[] _lsfAvg = new float[Lp];
  private readonly float[][] _lpc = CreateF2D(4, Lp);

  private int _pitchLagInt;

  // excitation_buf: PITCH_DELAY_MAX + LP + 1 + SubSize, with _excitation pointing into it
  private const int ExcOffset = PitchDelayMax + Lp + 1;
  private readonly float[] _excitationBuf = new float[ExcOffset + SubSize];

  private readonly float[] _pitchVector = new float[SubSize];
  private readonly float[] _fixedVector = new float[SubSize];

  private readonly float[] _predictionError = new float[4];
  private readonly float[] _pitchGain = new float[5];
  private readonly float[] _fixedGain = new float[5];

  private float _beta;
  private int _diffCount;
  private int _hangCount;

  private float _prevSparseFixedGain;
  private int _prevIrFilterNr;
  private int _irFilterOnset;

  private readonly float[] _postfilterMem = new float[Lp];
  private float _tiltMem;
  private float _postfilterAgc;
  private readonly float[] _highPassMem = new float[2];

  private readonly float[] _samplesIn = new float[Lp + SubSize];

  // ff_set_min_dist_lsf uses energy_pred_fac for gain prediction
  private static readonly float[] EnergyPredFac = AmrNbTables.EnergyPredFac;

  public AmrNbDecoder() {
    for (var i = 0; i < Lp; i++) {
      this._prevLspSub4[i] = AmrNbTables.LspSub4Init[i] * 1000.0 / (1 << 15);
      this._lsfAvg[i] = this._lsfQ[3][i] = AmrNbTables.LspAvgInit[i] / (float)(1 << 15);
    }
    for (var i = 0; i < 4; i++)
      this._predictionError[i] = MinEnergy;
  }

  private static double[][] Create2D(int a, int b) {
    var r = new double[a][];
    for (var i = 0; i < a; i++)
      r[i] = new double[b];
    return r;
  }

  private static float[][] CreateF2D(int a, int b) {
    var r = new float[a][];
    for (var i = 0; i < a; i++)
      r[i] = new float[b];
    return r;
  }

  /// <summary>
  /// Decodes one frame: <paramref name="payload"/> is the bit-reordered payload (header byte
  /// already stripped) and <paramref name="mode"/> is the 4-bit frame type. Writes exactly 160
  /// samples to <paramref name="output"/>. SID and NO_DATA frames produce 160 samples of silence
  /// (DTX comfort-noise synthesis is not reproduced here — see the metadata note in the container)
  /// and leave the synthesis state untouched.
  /// </summary>
  public void DecodeFrame(ReadOnlySpan<byte> payload, AmrNbMode mode, Span<short> output) {
    output[..AmrNbData.SamplesPerFrame].Clear();
    if (!AmrNbData.IsSpeech((int)mode))
      return;

    this._curMode = mode;
    UnpackBitstream(payload, mode);

    if (mode == AmrNbMode.Mr122)
      Lsf2Lsp5();
    else
      Lsf2Lsp3();

    for (var i = 0; i < 4; i++)
      AcelpLspd2Lpc(this._lsp[i], this._lpc[i]);

    var bufOut = new float[AmrNbData.SamplesPerFrame];

    for (var subframe = 0; subframe < 4; subframe++) {
      var sparse = new AmrFixed();

      DecodePitchVector(subframe);
      DecodeFixedSparse(sparse, subframe, mode);
      var fixedGainFactor = DecodeGains(subframe, mode);
      PitchSharpening(subframe, mode, sparse);

      SetFixedVector(this._fixedVector, sparse, 1.0f);

      var meanEnergy = DotProduct(this._fixedVector, 0, this._fixedVector, 0, SubSize) / SubSize;
      this._fixedGain[4] = AmrSetFixedGain(
        fixedGainFactor, meanEnergy, this._predictionError,
        AmrNbTables.EnergyMean[(int)mode], EnergyPredFac);

      // excitation feedback
      for (var i = 0; i < SubSize; i++)
        this._excitationBuf[ExcOffset + i] *= this._pitchGain[4];
      SetFixedVectorInto(this._excitationBuf, ExcOffset, sparse, this._fixedGain[4]);
      for (var i = 0; i < SubSize; i++)
        this._excitationBuf[ExcOffset + i] = MathF.Truncate(this._excitationBuf[ExcOffset + i]);

      var synthFixedGain = FixedGainSmooth(this._lsfQ[subframe], this._lsfAvg, mode);
      var spare = new float[SubSize];
      var synthVec = AntiSparseness(sparse, this._fixedVector, synthFixedGain, spare);

      if (Synthesis(this._lpc[subframe], synthFixedGain, synthVec, false))
        Synthesis(this._lpc[subframe], synthFixedGain, synthVec, true);

      Postfilter(this._lpc[subframe], bufOut, subframe * SubSize);

      ClearFixedVector(this._fixedVector, sparse);
      UpdateState();
    }

    // order-2 highpass + scale to PCM domain
    ApplyOrder2TransferFunction(bufOut, bufOut, HighpassZeros, HighpassPoles,
      HighpassGain * SampleScale, this._highPassMem, AmrNbData.SamplesPerFrame);

    // averaged lsf update (uses qbar(n-1))
    WeightedVectorSumF(this._lsfAvg, this._lsfAvg, this._lsfQ[3], 0.84f, 0.16f, Lp);

    for (var i = 0; i < AmrNbData.SamplesPerFrame; i++) {
      // bufOut is in [-1,1] scaled domain; back to 16-bit
      var v = bufOut[i] * 32768.0f;
      output[i] = (short)Math.Clamp((int)MathF.Round(v), short.MinValue, short.MaxValue);
    }
  }

  // ---------------------------------------------------------------------------------------------
  // Bit unpacking (ff_amr_bit_reorder)
  // ---------------------------------------------------------------------------------------------
  private void UnpackBitstream(ReadOnlySpan<byte> data, AmrNbMode mode) {
    this._frame.Clear();
    var ord = AmrNbTables.UnpackingBitmapsPerMode[(int)mode];
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
  // LSF → LSP
  // ---------------------------------------------------------------------------------------------
  private void InterpolateLsf(float[] lsfNew) {
    for (var i = 0; i < 4; i++)
      WeightedVectorSumF(this._lsfQ[i], this._lsfQ[3], lsfNew, 0.25f * (3 - i), 0.25f * (i + 1), Lp);
  }

  private void Lsf2LspForMode12k2(double[] lsp, float[] lsfNoR, int[][] q, int qOff, int sign, int update) {
    var lsfR = new int[Lp];
    var lsfQ = new float[Lp];
    for (var i = 0; i < Lp >> 1; i++) {
      lsfR[i << 1] = q[i][qOff];
      lsfR[(i << 1) + 1] = q[i][qOff + 1];
    }
    if (sign != 0) {
      lsfR[4] = -lsfR[4];
      lsfR[5] = -lsfR[5];
    }
    if (update != 0)
      Array.Copy(lsfR, this._prevLsfR, Lp);
    for (var i = 0; i < Lp; i++)
      lsfQ[i] = (float)(lsfR[i] * (LsfRFac / 8000.0) + lsfNoR[i] * (1.0 / 8000.0));
    SetMinDistLsf(lsfQ, MinLsfSpacing);
    if (update != 0)
      InterpolateLsf(lsfQ);
    AcelpLsf2Lspd(lsp, lsfQ);
  }

  private void Lsf2Lsp5() {
    var lsfParam = this._frame;
    var lsfNoR = new float[Lp];
    var q = new int[5][];
    q[0] = AmrNbTables.Lsf5_1[lsfParam.Lsf(0)];
    q[1] = AmrNbTables.Lsf5_2[lsfParam.Lsf(1)];
    q[2] = AmrNbTables.Lsf5_3[lsfParam.Lsf(2) >> 1];
    q[3] = AmrNbTables.Lsf5_4[lsfParam.Lsf(3)];
    q[4] = AmrNbTables.Lsf5_5[lsfParam.Lsf(4)];

    for (var i = 0; i < Lp; i++)
      lsfNoR[i] = (float)(this._prevLsfR[i] * LsfRFac * PredFacMode12k2 + AmrNbTables.Lsf5Mean[i]);

    var sign = lsfParam.Lsf(2) & 1;
    Lsf2LspForMode12k2(this._lsp[1], lsfNoR, q, 0, sign, 0);
    Lsf2LspForMode12k2(this._lsp[3], lsfNoR, q, 2, sign, 1);

    WeightedVectorSumD(this._lsp[0], this._prevLspSub4, this._lsp[1], 0.5, 0.5);
    WeightedVectorSumD(this._lsp[2], this._lsp[1], this._lsp[3], 0.5, 0.5);
  }

  private void Lsf2Lsp3() {
    var f = this._frame;
    var lsfR = new int[Lp];
    var lsfQ = new float[Lp];
    var mode = this._curMode;

    var t1 = (mode == AmrNbMode.Mr795 ? AmrNbTables.Lsf3_1Mode7k95 : AmrNbTables.Lsf3_1)[f.Lsf(0)];
    lsfR[0] = t1[0]; lsfR[1] = t1[1]; lsfR[2] = t1[2];

    var t2 = AmrNbTables.Lsf3_2[f.Lsf(1) << (mode <= AmrNbMode.Mr515 ? 1 : 0)];
    lsfR[3] = t2[0]; lsfR[4] = t2[1]; lsfR[5] = t2[2];

    var t3 = (mode <= AmrNbMode.Mr515 ? AmrNbTables.Lsf3_3Mode5k15 : AmrNbTables.Lsf3_3)[f.Lsf(2)];
    lsfR[6] = t3[0]; lsfR[7] = t3[1]; lsfR[8] = t3[2]; lsfR[9] = t3[3];

    for (var i = 0; i < Lp; i++)
      lsfQ[i] = (float)((lsfR[i] + this._prevLsfR[i] * AmrNbTables.PredFac[i]) * (LsfRFac / 8000.0)
                        + AmrNbTables.Lsf3Mean[i] * (1.0 / 8000.0));

    SetMinDistLsf(lsfQ, MinLsfSpacing);
    InterpolateLsf(lsfQ);
    Array.Copy(lsfR, this._prevLsfR, Lp);

    AcelpLsf2Lspd(this._lsp[3], lsfQ);

    for (var i = 1; i <= 3; i++)
      for (var j = 0; j < Lp; j++)
        this._lsp[i - 1][j] = this._prevLspSub4[j] +
          (this._lsp[3][j] - this._prevLspSub4[j]) * 0.25 * i;
  }

  // ---------------------------------------------------------------------------------------------
  // Pitch vector
  // ---------------------------------------------------------------------------------------------
  private static void DecodePitchLag16(out int lagInt, out int lagFrac, int idx, int prevLagInt, int subframe) {
    if (subframe is 0 or 2) {
      if (idx < 463) {
        lagInt = (idx + 107) * 10923 >> 16;
        lagFrac = idx - lagInt * 6 + 105;
      } else {
        lagInt = idx - 368;
        lagFrac = 0;
      }
    } else {
      lagInt = ((idx + 5) * 10923 >> 16) - 1;
      lagFrac = idx - lagInt * 6 - 3;
      lagInt += Clip(prevLagInt - 5, PitchLagMinMode12k2, PitchDelayMax - 9);
    }
  }

  private static void DecodePitchLag(out int lagInt, out int lagFrac, int idx, int prevLagInt,
    int subframe, bool thirdAsFirst, int resolution) {
    if (subframe == 0 || (subframe == 2 && thirdAsFirst)) {
      if (idx < 197)
        idx += 59;
      else
        idx = 3 * idx - 335;
    } else if (resolution == 4) {
      var min = Clip(prevLagInt - 5, PitchDelayMin, PitchDelayMax - 9);
      if (idx < 4)
        idx = 3 * (idx + min) + 1;
      else if (idx < 12)
        idx += 3 * min + 7;
      else
        idx = 3 * (idx + min - 6) + 1;
    } else {
      idx--;
      if (resolution == 5)
        idx += 3 * Clip(prevLagInt - 10, PitchDelayMin, PitchDelayMax - 19);
      else
        idx += 3 * Clip(prevLagInt - 5, PitchDelayMin, PitchDelayMax - 9);
    }
    lagInt = idx * 10923 >> 15;
    lagFrac = idx - 3 * lagInt - 1;
  }

  private void DecodePitchVector(int subframe) {
    int lagInt, lagFrac;
    var mode = this._curMode;
    if (mode == AmrNbMode.Mr122) {
      DecodePitchLag16(out lagInt, out lagFrac, this._frame.PLag(subframe), this._pitchLagInt, subframe);
    } else {
      DecodePitchLag(out lagInt, out lagFrac, this._frame.PLag(subframe), this._pitchLagInt, subframe,
        mode != AmrNbMode.Mr475 && mode != AmrNbMode.Mr515,
        mode <= AmrNbMode.Mr67 ? 4 : (mode == AmrNbMode.Mr795 ? 5 : 6));
      lagFrac *= 2;
    }

    this._pitchLagInt = lagInt;
    lagInt += lagFrac > 0 ? 1 : 0;

    AcelpInterpolateF(this._excitationBuf, ExcOffset, this._excitationBuf, ExcOffset + 1 - lagInt,
      AmrNbData.B60Sinc, 6, lagFrac + 6 - 6 * (lagFrac > 0 ? 1 : 0), 10, SubSize);

    Array.Copy(this._excitationBuf, ExcOffset, this._pitchVector, 0, SubSize);
  }

  // ---------------------------------------------------------------------------------------------
  // Fixed (algebraic) codebook
  // ---------------------------------------------------------------------------------------------
  private static void Decode10BitPulse(int code, int[] pos, int i1, int i2, int i3) {
    var p = AmrNbTables.BaseFiveTable[code >> 3];
    pos[i1] = (p[2] << 1) + (code & 1);
    pos[i2] = (p[1] << 1) + ((code >> 1) & 1);
    pos[i3] = (p[0] << 1) + ((code >> 2) & 1);
  }

  private static void Decode8Pulses31Bits(AmrNbFrame f, int sub, AmrFixed sparse) {
    var pos = new int[8];
    Decode10BitPulse(f.Pulse(sub, 4), pos, 0, 4, 1);
    Decode10BitPulse(f.Pulse(sub, 5), pos, 2, 6, 5);

    var temp = ((f.Pulse(sub, 6) >> 2) * 25 + 12) >> 5;
    pos[3] = temp % 5;
    pos[7] = temp / 5;
    if ((pos[7] & 1) != 0)
      pos[3] = 4 - pos[3];
    pos[3] = (pos[3] << 1) + (f.Pulse(sub, 6) & 1);
    pos[7] = (pos[7] << 1) + ((f.Pulse(sub, 6) >> 1) & 1);

    sparse.N = 8;
    for (var i = 0; i < 4; i++) {
      var pos1 = (pos[i] << 2) + i;
      var pos2 = (pos[i + 4] << 2) + i;
      var sign = f.Pulse(sub, i) != 0 ? -1.0f : 1.0f;
      sparse.X[i] = pos1;
      sparse.X[i + 4] = pos2;
      sparse.Y[i] = sign;
      sparse.Y[i + 4] = pos2 < pos1 ? -sign : sign;
    }
  }

  private static void Decode10Pulses35Bits(AmrNbFrame f, int sub, AmrFixed sparse) {
    // ff_decode_10_pulses_35bits(pulses, fixed_sparse, gray_decode, 5, 3)
    const int bits = 3;
    const int half = 5;
    var mask = (1 << bits) - 1;
    sparse.NoRepeatMask = 0;
    sparse.N = 2 * half;
    for (var i = 0; i < half; i++) {
      var pos1 = AmrNbTables.GrayDecode[f.Pulse(sub, 2 * i + 1) & mask] + i;
      var pos2 = AmrNbTables.GrayDecode[f.Pulse(sub, 2 * i) & mask] + i;
      var sign = (f.Pulse(sub, 2 * i + 1) & (1 << bits)) != 0 ? -1.0f : 1.0f;
      sparse.X[2 * i + 1] = pos1;
      sparse.X[2 * i] = pos2;
      sparse.Y[2 * i + 1] = sign;
      sparse.Y[2 * i] = pos2 < pos1 ? -sign : sign;
    }
  }

  private void DecodeFixedSparse(AmrFixed sparse, int subframe, AmrNbMode mode) {
    if (mode == AmrNbMode.Mr122) {
      Decode10Pulses35Bits(this._frame, subframe, sparse);
      return;
    }
    if (mode == AmrNbMode.Mr102) {
      Decode8Pulses31Bits(this._frame, subframe, sparse);
      return;
    }

    var fixedIndex = this._frame.Pulse(subframe, 0);
    var pos = sparse.X;
    if (mode <= AmrNbMode.Mr515) {
      var subset = ((fixedIndex >> 3) & 8) + (subframe << 1);
      pos[0] = (fixedIndex & 7) * 5 + AmrNbTables.TrackPosition[subset];
      pos[1] = ((fixedIndex >> 3) & 7) * 5 + AmrNbTables.TrackPosition[subset + 1];
      sparse.N = 2;
    } else if (mode == AmrNbMode.Mr59) {
      var subset = ((fixedIndex & 1) << 1) + 1;
      pos[0] = ((fixedIndex >> 1) & 7) * 5 + subset;
      subset = (fixedIndex >> 4) & 3;
      pos[1] = ((fixedIndex >> 6) & 7) * 5 + subset + (subset == 3 ? 1 : 0);
      sparse.N = pos[0] == pos[1] ? 1 : 2;
    } else if (mode == AmrNbMode.Mr67) {
      pos[0] = (fixedIndex & 7) * 5;
      var subset = (fixedIndex >> 2) & 2;
      pos[1] = ((fixedIndex >> 4) & 7) * 5 + subset + 1;
      subset = (fixedIndex >> 6) & 2;
      pos[2] = ((fixedIndex >> 8) & 7) * 5 + subset + 2;
      sparse.N = 3;
    } else { // MR74 or MR795
      pos[0] = AmrNbTables.GrayDecode[fixedIndex & 7];
      pos[1] = AmrNbTables.GrayDecode[(fixedIndex >> 3) & 7] + 1;
      pos[2] = AmrNbTables.GrayDecode[(fixedIndex >> 6) & 7] + 2;
      var subset = (fixedIndex >> 9) & 1;
      pos[3] = AmrNbTables.GrayDecode[(fixedIndex >> 10) & 7] + subset + 3;
      sparse.N = 4;
    }
    for (var i = 0; i < sparse.N; i++)
      sparse.Y[i] = ((this._frame.Pulse(subframe, 1) >> i) & 1) != 0 ? 1.0f : -1.0f;
  }

  private void PitchSharpening(int subframe, AmrNbMode mode, AmrFixed sparse) {
    if (mode == AmrNbMode.Mr122)
      this._beta = MathF.Min(this._pitchGain[4], 1.0f);
    sparse.PitchLag = this._pitchLagInt;
    sparse.PitchFac = this._beta;
    if (mode != AmrNbMode.Mr475 || (subframe & 1) != 0)
      this._beta = Math.Clamp(this._pitchGain[4], 0.0f, SharpMax);
  }

  // ---------------------------------------------------------------------------------------------
  // Gains
  // ---------------------------------------------------------------------------------------------
  private float DecodeGains(int subframe, AmrNbMode mode) {
    float fixedGainFactor;
    if (mode == AmrNbMode.Mr122 || mode == AmrNbMode.Mr795) {
      this._pitchGain[4] = AmrNbTables.QuaGainPit[this._frame.PGain(subframe)] * (1.0f / 16384.0f);
      fixedGainFactor = AmrNbTables.QuaGainCode[this._frame.FixedGain(subframe)] * (1.0f / 2048.0f);
    } else {
      int[] gains;
      if (mode >= AmrNbMode.Mr67)
        gains = AmrNbTables.GainsHigh[this._frame.PGain(subframe)];
      else if (mode >= AmrNbMode.Mr515)
        gains = AmrNbTables.GainsLow[this._frame.PGain(subframe)];
      else
        gains = AmrNbTables.GainsMode4k75[(this._frame.PGain(subframe & 2) << 1) + (subframe & 1)];
      this._pitchGain[4] = gains[0] * (1.0f / 16384.0f);
      fixedGainFactor = gains[1] * (1.0f / 4096.0f);
    }
    return fixedGainFactor;
  }

  private float FixedGainSmooth(float[] lsf, float[] lsfAvg, AmrNbMode mode) {
    float diff = 0;
    for (var i = 0; i < Lp; i++)
      diff += MathF.Abs(lsfAvg[i] - lsf[i]) / lsfAvg[i];

    this._diffCount++;
    if (diff <= 0.65f)
      this._diffCount = 0;
    if (this._diffCount > 10) {
      this._hangCount = 0;
      this._diffCount--;
    }
    if (this._hangCount < 40) {
      this._hangCount++;
    } else if (mode < AmrNbMode.Mr74 || mode == AmrNbMode.Mr102) {
      var sf = Math.Clamp(4.0f * diff - 1.6f, 0.0f, 1.0f);
      var mean = (this._fixedGain[0] + this._fixedGain[1] + this._fixedGain[2]
                  + this._fixedGain[3] + this._fixedGain[4]) * 0.2f;
      return sf * this._fixedGain[4] + (1.0f - sf) * mean;
    }
    return this._fixedGain[4];
  }

  // ---------------------------------------------------------------------------------------------
  // Anti-sparseness (phase dispersion)
  // ---------------------------------------------------------------------------------------------
  private float[] AntiSparseness(AmrFixed sparse, float[] fixedVector, float fixedGain, float[] outBuf) {
    int irFilterNr;
    if (this._pitchGain[4] < 0.6f)
      irFilterNr = 0;
    else if (this._pitchGain[4] < 0.9f)
      irFilterNr = 1;
    else
      irFilterNr = 2;

    if (fixedGain > 2.0f * this._prevSparseFixedGain)
      this._irFilterOnset = 2;
    else if (this._irFilterOnset != 0)
      this._irFilterOnset--;

    if (this._irFilterOnset == 0) {
      var count = 0;
      for (var i = 0; i < 5; i++)
        if (this._pitchGain[i] < 0.6f)
          count++;
      if (count > 2)
        irFilterNr = 0;
      if (irFilterNr > this._prevIrFilterNr + 1)
        irFilterNr--;
    } else if (irFilterNr < 2) {
      irFilterNr++;
    }

    if (fixedGain < 5.0f)
      irFilterNr = 2;

    var result = fixedVector;
    if (this._curMode != AmrNbMode.Mr74 && this._curMode < AmrNbMode.Mr102 && irFilterNr < 2) {
      var filters = this._curMode == AmrNbMode.Mr795 ? IrFiltersLookupMode7k95 : IrFiltersLookup;
      ApplyIrFilter(outBuf, sparse, filters[irFilterNr]);
      result = outBuf;
    }

    this._prevIrFilterNr = irFilterNr;
    this._prevSparseFixedGain = fixedGain;
    return result;
  }

  private static readonly float[][] IrFiltersLookup = { AmrNbTables.IrFilterStrong, AmrNbTables.IrFilterMedium };
  private static readonly float[][] IrFiltersLookupMode7k95 = { AmrNbTables.IrFilterStrongMode7k95, AmrNbTables.IrFilterMedium };

  private static void ApplyIrFilter(float[] outBuf, AmrFixed inv, float[] filter) {
    var filter1 = new float[SubSize];
    var filter2 = new float[SubSize];
    var lag = inv.PitchLag;
    var fac = inv.PitchFac;
    if (lag < SubSize) {
      CelpCircAddF(filter1, filter, filter, lag, fac, SubSize);
      if (lag < SubSize >> 1)
        CelpCircAddF(filter2, filter, filter1, lag, fac, SubSize);
    }
    Array.Clear(outBuf, 0, SubSize);
    for (var i = 0; i < inv.N; i++) {
      var x = inv.X[i];
      var y = inv.Y[i];
      float[] filterp;
      if (x >= SubSize - lag)
        filterp = filter;
      else if (x >= SubSize - (lag << 1))
        filterp = filter1;
      else
        filterp = filter2;
      CelpCircAddF(outBuf, outBuf, filterp, x, y, SubSize);
    }
  }

  private static void CelpCircAddF(float[] outv, float[] inv, float[] lagged, int lag, float fac, int n) {
    int k;
    for (k = 0; k < lag; k++)
      outv[k] = inv[k] + fac * lagged[n + k - lag];
    for (; k < n; k++)
      outv[k] = inv[k] + fac * lagged[k - lag];
  }

  // ---------------------------------------------------------------------------------------------
  // Synthesis
  // ---------------------------------------------------------------------------------------------
  private bool Synthesis(float[] lpc, float fixedGain, float[] fixedVector, bool overflow) {
    var excitation = new float[SubSize];
    if (overflow)
      for (var i = 0; i < SubSize; i++)
        this._pitchVector[i] *= 0.25f;

    WeightedVectorSumF(excitation, this._pitchVector, fixedVector, this._pitchGain[4], fixedGain, SubSize);

    if (this._pitchGain[4] > 0.5f && !overflow) {
      var energy = DotProduct(excitation, 0, excitation, 0, SubSize);
      var pitchFactor = this._pitchGain[4] *
        (this._curMode == AmrNbMode.Mr122
          ? 0.25f * MathF.Min(this._pitchGain[4], 1.0f)
          : 0.5f * MathF.Min(this._pitchGain[4], SharpMax));
      for (var i = 0; i < SubSize; i++)
        excitation[i] += pitchFactor * this._pitchVector[i];
      ScaleVectorToGivenSumOfSquares(excitation, excitation, energy, SubSize);
    }

    CelpLpSynthesisFilterFSpan(this._samplesIn, Lp, lpc, excitation, 0, SubSize, Lp);

    for (var i = 0; i < SubSize; i++)
      if (MathF.Abs(this._samplesIn[Lp + i]) > SampleBound)
        return true;
    return false;
  }

  private void UpdateState() {
    Array.Copy(this._lsp[3], this._prevLspSub4, Lp);
    Array.Copy(this._excitationBuf, SubSize, this._excitationBuf, 0, ExcOffset);
    Array.Copy(this._pitchGain, 1, this._pitchGain, 0, 4);
    Array.Copy(this._fixedGain, 1, this._fixedGain, 0, 4);
    Array.Copy(this._samplesIn, SubSize, this._samplesIn, 0, Lp);
  }

  // ---------------------------------------------------------------------------------------------
  // Postfilter
  // ---------------------------------------------------------------------------------------------
  private float TiltFactor(float[] lpcN, float[] lpcD) {
    var impulse = new float[Lp + TiltResponse];
    // hf points to impulse[Lp]
    impulse[Lp] = 1.0f;
    for (var i = 0; i < Lp; i++)
      impulse[Lp + 1 + i] = lpcN[i];
    CelpLpSynthesisFilterF(impulse, Lp, lpcD, impulse, TiltResponse, Lp, srcOffset: Lp);

    var rh0 = DotProduct(impulse, Lp, impulse, Lp, TiltResponse);
    var rh1 = DotProduct(impulse, Lp, impulse, Lp + 1, TiltResponse - 1);
    return rh1 >= 0.0f ? rh1 / rh0 * TiltGammaT : 0.0f;
  }

  private void Postfilter(float[] lpc, float[] bufOut, int outOffset) {
    var samplesStart = Lp; // samples_in + LP_FILTER_ORDER
    var speechGain = DotProduct(this._samplesIn, samplesStart, this._samplesIn, samplesStart, SubSize);

    var poleOut = new float[SubSize + Lp];
    float[] gammaN, gammaD;
    if (this._curMode == AmrNbMode.Mr122 || this._curMode == AmrNbMode.Mr102) {
      gammaN = AmrNbData.Pow07;
      gammaD = AmrNbData.Pow075;
    } else {
      gammaN = AmrNbData.Pow055;
      gammaD = AmrNbData.Pow07;
    }

    var lpcN = new float[Lp];
    var lpcD = new float[Lp];
    for (var i = 0; i < Lp; i++) {
      lpcN[i] = lpc[i] * gammaN[i];
      lpcD[i] = lpc[i] * gammaD[i];
    }

    Array.Copy(this._postfilterMem, poleOut, Lp);
    // celp_lp_synthesis_filterf(pole_out+LP, lpc_d, samples, SubSize, LP)
    CelpLpSynthesisFilterFSpan(poleOut, Lp, lpcD, this._samplesIn, samplesStart, SubSize, Lp);
    Array.Copy(poleOut, SubSize, this._postfilterMem, 0, Lp);

    var bufSlice = new float[SubSize];
    CelpLpZeroSynthesisFilterF(bufSlice, lpcN, poleOut, Lp, SubSize, Lp);

    TiltCompensation(ref this._tiltMem, TiltFactor(lpcN, lpcD), bufSlice, SubSize);
    AdaptiveGainControl(bufSlice, bufSlice, speechGain, SubSize, AgcAlpha, ref this._postfilterAgc);

    Array.Copy(bufSlice, 0, bufOut, outOffset, SubSize);
  }

  // ---------------------------------------------------------------------------------------------
  // Shared ACELP primitives (ports of the ffmpeg helpers)
  // ---------------------------------------------------------------------------------------------
  private static int Clip(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

  private static void WeightedVectorSumF(float[] outv, float[] a, float[] b, float wa, float wb, int n) {
    for (var i = 0; i < n; i++)
      outv[i] = wa * a[i] + wb * b[i];
  }

  private static void WeightedVectorSumD(double[] outv, double[] a, double[] b, double wa, double wb) {
    for (var i = 0; i < Lp; i++)
      outv[i] = wa * a[i] + wb * b[i];
  }

  private static float DotProduct(float[] a, int ao, float[] b, int bo, int n) {
    float s = 0;
    for (var i = 0; i < n; i++)
      s += a[ao + i] * b[bo + i];
    return s;
  }

  private static void SetMinDistLsf(float[] lsf, double minSpacing) {
    float prev = 0;
    for (var i = 0; i < lsf.Length; i++)
      prev = lsf[i] = MathF.Max(lsf[i], prev + (float)minSpacing);
  }

  private static void AcelpLsf2Lspd(double[] lsp, float[] lsf) {
    for (var i = 0; i < Lp; i++)
      lsp[i] = Math.Cos(2.0 * Math.PI * lsf[i]);
  }

  // lsp2polyf + ff_acelp_lspd2lpc (lp_half_order = 5)
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

  private static void AcelpLspd2Lpc(double[] lsp, float[] lpc) {
    const int half = Lp >> 1; // 5
    var pa = new double[half + 1];
    var qa = new double[half + 1];
    Lsp2Polyf(lsp, 0, pa, half);
    Lsp2Polyf(lsp, 1, qa, half);
    var lpc2Base = (half << 1) - 1; // index into lpc for lpc2[-k]
    for (var h = half - 1; h >= 0; h--) {
      var paf = pa[h + 1] + pa[h];
      var qaf = qa[h + 1] - qa[h];
      lpc[h] = (float)(0.5 * (paf + qaf));
      lpc[lpc2Base - h] = (float)(0.5 * (paf - qaf));
    }
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

  // celp_lp_synthesis_filterf with explicit history before [start].
  // buf layout: out[start-k] history, writes out[start..start+len)
  private static void CelpLpSynthesisFilterF(float[] outv, int start, float[] coeffs, float[] inv, int len, int order, int srcOffset = -1) {
    var so = srcOffset < 0 ? start : srcOffset;
    for (var n = 0; n < len; n++) {
      var acc = inv[so + n];
      for (var i = 1; i <= order; i++)
        acc -= coeffs[i - 1] * outv[start + n - i];
      outv[start + n] = acc;
    }
  }

  // variant where input lives in a separate array at inStart
  private static void CelpLpSynthesisFilterFSpan(float[] outv, int outStart, float[] coeffs, float[] inv, int inStart, int len, int order) {
    for (var n = 0; n < len; n++) {
      var acc = inv[inStart + n];
      for (var i = 1; i <= order; i++)
        acc -= coeffs[i - 1] * outv[outStart + n - i];
      outv[outStart + n] = acc;
    }
  }

  private static void CelpLpZeroSynthesisFilterF(float[] outv, float[] coeffs, float[] inv, int inStart, int len, int order) {
    for (var n = 0; n < len; n++) {
      var acc = inv[inStart + n];
      for (var i = 1; i <= order; i++)
        acc += coeffs[i - 1] * inv[inStart + n - i];
      outv[n] = acc;
    }
  }

  private static void TiltCompensation(ref float mem, float tilt, float[] samples, int size) {
    var newMem = samples[size - 1];
    for (var i = size - 1; i > 0; i--)
      samples[i] -= tilt * samples[i - 1];
    samples[0] -= tilt * mem;
    mem = newMem;
  }

  private static void AdaptiveGainControl(float[] outv, float[] inv, float speechEnerg, int size, float alpha, ref float gainMem) {
    var postEnerg = DotProduct(inv, 0, inv, 0, size);
    var scale = 1.0f;
    var mem = gainMem;
    if (postEnerg != 0)
      scale = MathF.Sqrt(speechEnerg / postEnerg);
    scale *= 1.0f - alpha;
    for (var i = 0; i < size; i++) {
      mem = alpha * mem + scale;
      outv[i] = inv[i] * mem;
    }
    gainMem = mem;
  }

  private static void ApplyOrder2TransferFunction(float[] outv, float[] inv, float[] zero, float[] pole, float gain, float[] mem, int n) {
    for (var i = 0; i < n; i++) {
      var tmp = gain * inv[i] - pole[0] * mem[0] - pole[1] * mem[1];
      outv[i] = tmp + zero[0] * mem[0] + zero[1] * mem[1];
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

  // ff_set_fixed_vector / ff_clear_fixed_vector
  private static void SetFixedVector(float[] outv, AmrFixed inv, float scale) {
    Array.Clear(outv, 0, SubSize);
    AddFixedVector(outv, 0, inv, scale, SubSize);
  }

  private static void SetFixedVectorInto(float[] buf, int offset, AmrFixed inv, float scale) {
    AddFixedVector(buf, offset, inv, scale, SubSize);
  }

  private static void AddFixedVector(float[] buf, int offset, AmrFixed inv, float scale, int size) {
    for (var i = 0; i < inv.N; i++) {
      var x = inv.X[i];
      var repeats = ((inv.NoRepeatMask >> i) & 1) == 0;
      var y = inv.Y[i] * scale;
      if (inv.PitchLag > 0) {
        do {
          buf[offset + x] += y;
          y *= inv.PitchFac;
          x += inv.PitchLag;
        } while (x < size && repeats);
      }
    }
  }

  private static void ClearFixedVector(float[] outv, AmrFixed inv) {
    for (var i = 0; i < inv.N; i++) {
      var x = inv.X[i];
      var repeats = ((inv.NoRepeatMask >> i) & 1) == 0;
      if (inv.PitchLag > 0)
        do {
          outv[x] = 0.0f;
          x += inv.PitchLag;
        } while (x < SubSize && repeats);
    }
  }
}

/// <summary>Sparse algebraic codebook vector (ffmpeg <c>AMRFixed</c>).</summary>
internal sealed class AmrFixed {
  public readonly int[] X = new int[10];
  public readonly float[] Y = new float[10];
  public int N;
  public int PitchLag;
  public float PitchFac;
  public int NoRepeatMask;
}

#pragma warning disable CS1591

namespace Codec.Speex;

/// <summary>
/// Faithful clean-room port of FFmpeg's native Speex decoder (libavcodec
/// <c>speexdec.c</c>, itself derived from the Xiph reference libspeex). Decodes the
/// narrowband (mode 0) and wideband (mode 1) / ultra-wideband (mode 2) layers to
/// floating-point PCM, scaled to signed 16-bit on the public surface.
/// <para>
/// Narrowband is the CELP core: per-subframe LSP-VQ unquantisation, 3-tap pitch
/// (long-term predictor) from the gain codebooks, split-shape innovation codebooks,
/// excitation build, the comb-filter enhancer (<c>multicomb</c>), LSP→LPC synthesis,
/// IIR filtering and the high-pass. Wideband layers (modes 1/2) add the QMF split +
/// high-band SB-CELP via <c>sb_decode</c>, reusing the narrowband core for the low band.
/// </para>
/// <para>
/// Tables (codebooks, QMF filter <c>h0</c>, gain bounds) are transcribed verbatim in
/// <see cref="SpeexTables"/>. State (excitation history, LSPs, filter memory, RNG seed)
/// is carried across frames exactly as the reference does.
/// </para>
/// </summary>
public sealed class SpeexDecoder {

  private const int QmfOrder = 64;
  private const int NbOrder = 10;
  private const int NbFrameSize = 160;
  private const int NbSubmodeBits = 4;
  private const int SbSubmodeBits = 3;
  private const int NbSubframeSize = 40;
  private const int NbNbSubframes = 4;
  private const int NbPitchStart = 17;
  private const int NbPitchEnd = 144;
  private const int NbDecBuffer = NbFrameSize + 2 * NbPitchEnd + NbSubframeSize + 12;
  private const int SpeexInbandStereo = 9;
  private const float Pi = (float)Math.PI;

  private static float LspLinear(int i) => 0.25f * i + 0.25f;
  private static float LspLinearHigh(int i) => 0.3125f * i + 0.75f;
  private static float LspDiv256(float x) => 0.00390625f * x;
  private static float LspDiv512(float x) => 0.001953125f * x;
  private static float LspDiv1024(float x) => 0.0009765625f * x;

  // ── submode / mode tables (mirror the SpeexSubmode / SpeexMode structs) ──────────

  private sealed record LtpParam(sbyte[] GainCdbk, int GainBits, int PitchBits);

  private static readonly LtpParam LtpVlbr = new(SpeexTables.GainCdbkLbr, 5, 0);
  private static readonly LtpParam LtpLbr = new(SpeexTables.GainCdbkLbr, 5, 7);
  private static readonly LtpParam LtpMed = new(SpeexTables.GainCdbkLbr, 5, 7);
  private static readonly LtpParam LtpNb = new(SpeexTables.GainCdbkNb, 7, 7);

  private sealed record SplitCb(int SubvectSize, int NbSubvect, sbyte[] ShapeCb, int ShapeBits, int HaveSign);

  private static readonly SplitCb CbNbUlbr = new(20, 2, SpeexTables.Exc2032Table, 5, 0);
  private static readonly SplitCb CbNbVlbr = new(10, 4, SpeexTables.Exc1016Table, 4, 0);
  private static readonly SplitCb CbNbLbr = new(10, 4, SpeexTables.Exc1032Table, 5, 0);
  private static readonly SplitCb CbNbMed = new(8, 5, SpeexTables.Exc8128Table, 7, 0);
  private static readonly SplitCb CbNb = new(5, 8, SpeexTables.Exc564Table, 6, 0);
  private static readonly SplitCb CbSb = new(5, 8, SpeexTables.Exc5256Table, 8, 0);
  private static readonly SplitCb CbHigh = new(8, 5, SpeexTables.HexcTable, 7, 1);
  private static readonly SplitCb CbHighLbr = new(10, 4, SpeexTables.Hexc1032Table, 5, 0);

  private enum LspKind { Lbr, Nb, High }
  private enum LtpKind { None, Forced, Tap3 }
  private enum InnovKind { None, Noise, SplitShape }

  private sealed record Submode(
    int LbrPitch, int ForcedPitchGain, int HaveSubframeGain, int DoubleCodebook,
    LspKind Lsp, LtpKind Ltp, LtpParam? LtpParam,
    InnovKind Innov, SplitCb? InnovParams, float CombGain);

  // nb_submode1..8 (index 1..8); index 0 = null/no-transmission.
  private static readonly Submode?[] NbSubmodes = {
    null,
    new(0, 1, 0, 0, LspKind.Lbr, LtpKind.Forced, null, InnovKind.Noise, null, -1f),
    new(0, 0, 0, 0, LspKind.Lbr, LtpKind.Tap3, LtpVlbr, InnovKind.SplitShape, CbNbVlbr, .6f),
    new(-1, 0, 1, 0, LspKind.Lbr, LtpKind.Tap3, LtpLbr, InnovKind.SplitShape, CbNbLbr, .55f),
    new(-1, 0, 1, 0, LspKind.Lbr, LtpKind.Tap3, LtpMed, InnovKind.SplitShape, CbNbMed, .45f),
    new(-1, 0, 3, 0, LspKind.Nb, LtpKind.Tap3, LtpNb, InnovKind.SplitShape, CbNb, .25f),
    new(-1, 0, 3, 0, LspKind.Nb, LtpKind.Tap3, LtpNb, InnovKind.SplitShape, CbSb, .15f),
    new(-1, 0, 3, 1, LspKind.Nb, LtpKind.Tap3, LtpNb, InnovKind.SplitShape, CbNb, 0.05f),
    new(0, 1, 0, 0, LspKind.Lbr, LtpKind.Forced, null, InnovKind.SplitShape, CbNbUlbr, .5f),
  };

  // wb_submode1..4 (index 1..4); index 0 = null.
  private static readonly Submode?[] WbSubmodes = {
    null,
    new(0, 0, 1, 0, LspKind.High, LtpKind.None, null, InnovKind.None, null, -1f),
    new(0, 0, 1, 0, LspKind.High, LtpKind.None, null, InnovKind.SplitShape, CbHighLbr, -1f),
    new(0, 0, 1, 0, LspKind.High, LtpKind.None, null, InnovKind.SplitShape, CbHigh, -1f),
    new(0, 0, 1, 1, LspKind.High, LtpKind.None, null, InnovKind.SplitShape, CbHigh, -1f),
  };

  private sealed class ModeInfo {
    public int ModeId;
    public bool IsWideband;     // sb_decode vs nb_decode
    public int FrameSize;
    public int SubframeSize;
    public int LpcSize;
    public float FoldingGain;
    public Submode?[] Submodes = null!;
    public int DefaultSubmode;
  }

  private static readonly ModeInfo[] Modes = {
    new() { ModeId = 0, IsWideband = false, FrameSize = NbFrameSize, SubframeSize = NbSubframeSize,
            LpcSize = NbOrder, FoldingGain = 0f, Submodes = NbSubmodes, DefaultSubmode = 5 },
    new() { ModeId = 1, IsWideband = true, FrameSize = NbFrameSize, SubframeSize = NbSubframeSize,
            LpcSize = 8, FoldingGain = 0.9f, Submodes = WbSubmodes, DefaultSubmode = 3 },
    new() { ModeId = 2, IsWideband = true, FrameSize = 320, SubframeSize = 80,
            LpcSize = 8, FoldingGain = 0.7f, Submodes = WbSubmodes, DefaultSubmode = 1 },
  };

  // ── per-layer decoder state (mirrors DecoderState) ───────────────────────────────

  private sealed class State {
    public ModeInfo Mode = null!;
    public int ModeId;
    public bool First = true;
    public int FullFrameSize;
    public bool IsWideband;
    public int CountLost;
    public int FrameSize;
    public int SubframeSize;
    public int NbSubframes;
    public int LpcSize;
    public float[]? InnovSave; // alias into the output buffer (null if not saving)
    public int InnovSaveOffset;

    public int LastPitch = 40;
    public float LastPitchGain;
    public uint Seed = 1000;

    public bool EncodeSubmode = true;
    public Submode?[] Submodes = null!;
    public int SubmodeId;
    public bool LpcEnhEnabled = true;

    public float VocM1, VocM2, VocMean;
    public int VocOffset;
    public bool DtxEnabled;
    public bool HighpassEnabled;

    // exc points into ExcBuf at this offset.
    public int ExcOffset;
    public readonly float[] MemHp = new float[2];
    public readonly float[] ExcBuf = new float[NbDecBuffer];
    public readonly float[] OldQlsp = new float[NbOrder];
    public readonly float[] InterpQlpc = new float[NbOrder];
    public readonly float[] MemSp = new float[NbOrder];
    public readonly float[] G0Mem = new float[QmfOrder];
    public readonly float[] G1Mem = new float[QmfOrder];
    public readonly float[] PiGain = new float[NbNbSubframes];
    public readonly float[] ExcRms = new float[NbNbSubframes];

    public Submode CurrentSubmode => this.Submodes[this.SubmodeId]!;
  }

  private sealed class StereoState {
    public float Balance = 1f;
    public float ERatio = .5f;
    public float SmoothLeft = 1f;
    public float SmoothRight = 1f;
  }

  private readonly SpeexHeader _header;
  private readonly int _mode;
  private readonly int _frameSize;
  private readonly int _channels;
  private readonly State[] _st = new State[3];
  private readonly StereoState _stereo = new();

  /// <summary>Output frame size in samples per channel (the full-band size).</summary>
  public int FrameSize => this._frameSize;

  /// <summary>Channel count (1 or 2).</summary>
  public int Channels => this._channels;

  /// <summary>Sample rate declared in the header.</summary>
  public int SampleRate => this._header.Rate;

  /// <summary>Frames carried per Ogg packet.</summary>
  public int FramesPerPacket => this._header.FramesPerPacket;

  public SpeexDecoder(SpeexHeader header) {
    ArgumentNullException.ThrowIfNull(header);
    this._header = header;
    this._mode = header.Mode;
    this._channels = header.NbChannels;
    this._frameSize = NbFrameSize << this._mode;

    for (var m = 0; m <= this._mode; ++m)
      this._st[m] = InitState(Modes[m]);
  }

  private static State InitState(ModeInfo mode) {
    var st = new State {
      Mode = mode,
      ModeId = mode.ModeId,
      First = true,
      EncodeSubmode = true,
      IsWideband = mode.ModeId > 0,
      InnovSave = null,
      Submodes = mode.Submodes,
      SubmodeId = mode.DefaultSubmode,
      SubframeSize = mode.SubframeSize,
      LpcSize = mode.LpcSize,
      FullFrameSize = (1 + (mode.ModeId > 0 ? 1 : 0)) * mode.FrameSize,
      NbSubframes = mode.FrameSize / mode.SubframeSize,
      FrameSize = mode.FrameSize,
      LpcEnhEnabled = true,
      LastPitch = 40,
      CountLost = 0,
      Seed = 1000,
      VocM1 = 0, VocM2 = 0, VocMean = 0,
      VocOffset = 0,
      DtxEnabled = false,
      HighpassEnabled = mode.ModeId == 0,
    };
    return st;
  }

  /// <summary>
  /// Decodes one Speex packet (which carries <see cref="FramesPerPacket"/> frames)
  /// into interleaved signed 16-bit PCM. Truncated / terminator-flagged packets are
  /// tolerated: fewer frames are produced and the remainder is silence-padded so the
  /// returned length is always <c>FramesPerPacket * FrameSize * Channels</c>.
  /// </summary>
  public short[] DecodePacket(ReadOnlySpan<byte> packet) {
    var framesPerPacket = this._header.FramesPerPacket;
    var outFloat = new float[this._frameSize * (this._mode > 0 ? 2 : 1) + this._frameSize];
    var result = new short[framesPerPacket * this._frameSize * this._channels];

    var buf = packet.ToArray();
    var gb = new SpeexBitReader(buf, buf.Length);

    var produced = 0;
    for (var i = 0; i < framesPerPacket; ++i) {
      Array.Clear(outFloat);
      var ok = this.DecodeFrame(gb, outFloat, framesPerPacket - i);
      if (!ok) break;

      if (this._channels == 2)
        DecodeStereo(outFloat, this._frameSize, this._stereo);

      // Interleave + scale to int16.
      for (var n = 0; n < this._frameSize; ++n) {
        if (this._channels == 1) {
          result[(produced + n)] = ToInt16(outFloat[n]);
        } else {
          result[(produced + n) * 2] = ToInt16(outFloat[2 * n]);
          result[(produced + n) * 2 + 1] = ToInt16(outFloat[2 * n + 1]);
        }
      }
      produced += this._frameSize;

      // Terminator / out-of-bits → stop after this frame (samples already zeroed).
      if (gb.BitsLeft < 5 || gb.ShowBits(5) == 15)
        break;
    }

    return result;
  }

  /// <summary>
  /// Decodes one full-band frame into <paramref name="outBuf"/> (mono float, scaled to
  /// the ±32768 range). Returns false if the bitstream is exhausted / invalid.
  /// </summary>
  internal bool DecodeFrame(SpeexBitReader gb, float[] outBuf, int packetsLeft) {
    var st = this._st[this._mode];
    return st.Mode.IsWideband
      ? this.SbDecode(st, gb, outBuf, packetsLeft) >= 0
      : NbDecode(st, gb, outBuf, this._stereo) >= 0;
  }

  private static short ToInt16(float scaledTo32768) {
    var v = (int)MathF.Round(scaledTo32768);
    return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
  }

  // ── LSP unquantisers ─────────────────────────────────────────────────────────────

  private static void LspUnquant(LspKind kind, float[] lsp, int order, SpeexBitReader gb) {
    switch (kind) {
      case LspKind.Lbr: LspUnquantLbr(lsp, order, gb); break;
      case LspKind.Nb: LspUnquantNb(lsp, order, gb); break;
      default: LspUnquantHigh(lsp, order, gb); break;
    }
  }

  private static void LspUnquantLbr(float[] lsp, int order, SpeexBitReader gb) {
    for (var i = 0; i < order; ++i) lsp[i] = LspLinear(i);
    var id = gb.GetBits(6);
    for (var i = 0; i < 10; ++i) lsp[i] += LspDiv256(SpeexTables.CdbkNb[id * 10 + i]);
    id = gb.GetBits(6);
    for (var i = 0; i < 5; ++i) lsp[i] += LspDiv512(SpeexTables.CdbkNbLow1[id * 5 + i]);
    id = gb.GetBits(6);
    for (var i = 0; i < 5; ++i) lsp[i + 5] += LspDiv512(SpeexTables.CdbkNbHigh1[id * 5 + i]);
  }

  private static void LspUnquantNb(float[] lsp, int order, SpeexBitReader gb) {
    for (var i = 0; i < order; ++i) lsp[i] = LspLinear(i);
    var id = gb.GetBits(6);
    for (var i = 0; i < 10; ++i) lsp[i] += LspDiv256(SpeexTables.CdbkNb[id * 10 + i]);
    id = gb.GetBits(6);
    for (var i = 0; i < 5; ++i) lsp[i] += LspDiv512(SpeexTables.CdbkNbLow1[id * 5 + i]);
    id = gb.GetBits(6);
    for (var i = 0; i < 5; ++i) lsp[i] += LspDiv1024(SpeexTables.CdbkNbLow2[id * 5 + i]);
    id = gb.GetBits(6);
    for (var i = 0; i < 5; ++i) lsp[i + 5] += LspDiv512(SpeexTables.CdbkNbHigh1[id * 5 + i]);
    id = gb.GetBits(6);
    for (var i = 0; i < 5; ++i) lsp[i + 5] += LspDiv1024(SpeexTables.CdbkNbHigh2[id * 5 + i]);
  }

  private static void LspUnquantHigh(float[] lsp, int order, SpeexBitReader gb) {
    for (var i = 0; i < order; ++i) lsp[i] = LspLinearHigh(i);
    var id = gb.GetBits(6);
    for (var i = 0; i < order; ++i) lsp[i] += LspDiv256(SpeexTables.HighLspCdbk[id * order + i]);
    id = gb.GetBits(6);
    for (var i = 0; i < order; ++i) lsp[i] += LspDiv512(SpeexTables.HighLspCdbk2[id * order + i]);
  }

  // ── innovation / pitch unquantisers ──────────────────────────────────────────────

  private static float SpeexRand(float std, ref uint seed) {
    const uint jflone = 0x3f800000;
    const uint jflmsk = 0x007fffff;
    seed = 1664525 * seed + 1013904223;
    var ran = jflone | (jflmsk & seed);
    var fran = BitConverter.UInt32BitsToSingle(ran);
    fran -= 1.5f;
    fran *= std;
    return fran;
  }

  private static void NoiseCodebookUnquant(float[] exc, int excOff, int nsf, ref uint seed) {
    for (var i = 0; i < nsf; ++i)
      exc[excOff + i] = SpeexRand(1f, ref seed);
  }

  private static void SplitCbShapeSignUnquant(float[] exc, int excOff, SplitCb p, int nsf, SpeexBitReader gb) {
    var signs = new int[10];
    var ind = new int[10];
    for (var i = 0; i < p.NbSubvect; ++i) {
      signs[i] = p.HaveSign != 0 ? gb.GetBits1() : 0;
      ind[i] = gb.GetBitsZ(p.ShapeBits);
    }
    for (var i = 0; i < p.NbSubvect; ++i) {
      var s = signs[i] != 0 ? -1f : 1f;
      for (var j = 0; j < p.SubvectSize; ++j)
        exc[excOff + p.SubvectSize * i + j] += s * 0.03125f * p.ShapeCb[ind[i] * p.SubvectSize + j];
    }
  }

  private static void InnovationUnquant(InnovKind kind, SplitCb? p, float[] exc, int excOff,
    int nsf, SpeexBitReader gb, ref uint seed) {
    switch (kind) {
      case InnovKind.Noise: NoiseCodebookUnquant(exc, excOff, nsf, ref seed); break;
      case InnovKind.SplitShape: SplitCbShapeSignUnquant(exc, excOff, p!, nsf, gb); break;
      default: break; // None
    }
  }

  private static float Gain3tapTo1tap(float[] g) =>
    Math.Abs(g[1]) + (g[0] > 0f ? g[0] : -.5f * g[0]) + (g[2] > 0f ? g[2] : -.5f * g[2]);

  private static void ForcedPitchUnquant(float[] exc, int excOff, float[] excOut, int start,
    float pitchCoef, int nsf, int[] pitchVal, float[] gainVal) {
    pitchCoef = MathF.Min(pitchCoef, .99f);
    for (var i = 0; i < nsf; ++i) {
      excOut[i] = exc[excOff + i - start] * pitchCoef;
      exc[excOff + i] = excOut[i];
    }
    pitchVal[0] = start;
    gainVal[0] = gainVal[2] = 0f;
    gainVal[1] = pitchCoef;
  }

  private static void PitchUnquant3tap(float[] exc, int excOff, float[] excOut, int start,
    LtpParam par, int nsf, int[] pitchVal, float[] gainVal, SpeexBitReader gb,
    int countLost, int subframeOffset, float lastPitchGain, int cdbkOffset) {
    var gainCdbkSize = 1 << par.GainBits;
    var gainBase = 4 * gainCdbkSize * cdbkOffset;
    var gainCdbk = par.GainCdbk;
    var gain = new float[3];

    var pitch = gb.GetBitsZ(par.PitchBits);
    pitch += start;
    var gainIndex = gb.GetBitsZ(par.GainBits);
    gain[0] = 0.015625f * gainCdbk[gainBase + gainIndex * 4] + .5f;
    gain[1] = 0.015625f * gainCdbk[gainBase + gainIndex * 4 + 1] + .5f;
    gain[2] = 0.015625f * gainCdbk[gainBase + gainIndex * 4 + 2] + .5f;

    if (countLost != 0 && pitch > subframeOffset) {
      var tmp = countLost < 4 ? lastPitchGain : 0.5f * lastPitchGain;
      tmp = MathF.Min(tmp, .95f);
      var gainSum = Gain3tapTo1tap(gain);
      if (gainSum > tmp && gainSum > 0f) {
        var fact = tmp / gainSum;
        for (var i = 0; i < 3; ++i) gain[i] *= fact;
      }
    }

    pitchVal[0] = pitch;
    gainVal[0] = gain[0];
    gainVal[1] = gain[1];
    gainVal[2] = gain[2];
    Array.Clear(excOut, 0, nsf);

    for (var i = 0; i < 3; ++i) {
      var pp = pitch + 1 - i;
      var tmp1 = Math.Min(nsf, pp);
      for (var j = 0; j < tmp1; ++j)
        excOut[j] += gain[2 - i] * exc[excOff + j - pp];
      var tmp3 = Math.Min(nsf, pp + pitch);
      for (var j = tmp1; j < tmp3; ++j)
        excOut[j] += gain[2 - i] * exc[excOff + j - pp - pitch];
    }
  }

  // ── DSP primitives ───────────────────────────────────────────────────────────────

  private static float ComputeRms(float[] x, int off, int len) {
    var sum = 0f;
    for (var i = 0; i < len; ++i) sum += x[off + i] * x[off + i];
    return MathF.Sqrt(.1f + sum / len);
  }

  private static void BwLpc(float gamma, float[] lpcIn, float[] lpcOut, int order) {
    var tmp = gamma;
    for (var i = 0; i < order; ++i) {
      lpcOut[i] = tmp * lpcIn[i];
      tmp *= gamma;
    }
  }

  private static void IirMem(float[] x, int xOff, float[] den, float[] y, int yOff, int n, int ord, float[] mem) {
    for (var i = 0; i < n; ++i) {
      var yi = x[xOff + i] + mem[0];
      var nyi = -yi;
      for (var j = 0; j < ord - 1; ++j)
        mem[j] = mem[j + 1] + den[j] * nyi;
      mem[ord - 1] = den[ord - 1] * nyi;
      y[yOff + i] = yi;
    }
  }

  private static readonly float[][] HighpassPcoef = {
    new[] { 1.00000f, -1.92683f, 0.93071f }, new[] { 1.00000f, -1.97226f, 0.97332f },
  };
  private static readonly float[][] HighpassZcoef = {
    new[] { 0.96446f, -1.92879f, 0.96446f }, new[] { 0.98645f, -1.97277f, 0.98645f },
  };

  private static void Highpass(float[] x, float[] y, int len, float[] mem, int wide) {
    var den = HighpassPcoef[wide];
    var num = HighpassZcoef[wide];
    for (var i = 0; i < len; ++i) {
      var yi = num[0] * x[i] + mem[0];
      mem[0] = mem[1] + num[1] * x[i] + -den[1] * yi;
      mem[1] = num[2] * x[i] + -den[2] * yi;
      y[i] = yi;
    }
  }

  private static void SanitizeValues(float[] vec, float minVal, float maxVal, int len) {
    for (var i = 0; i < len; ++i) {
      if (!IsNormal(vec[i]) || Math.Abs(vec[i]) < 1e-8f)
        vec[i] = 0f;
      else
        vec[i] = Math.Clamp(vec[i], minVal, maxVal);
    }
  }

  private static bool IsNormal(float v) =>
    !float.IsNaN(v) && !float.IsInfinity(v) && v != 0f && Math.Abs(v) >= float.Epsilon * (1 << 23);

  private static void SignalMul(float[] x, int xOff, float[] y, int yOff, float scale, int len) {
    for (var i = 0; i < len; ++i) y[yOff + i] = scale * x[xOff + i];
  }

  private static float InnerProd(float[] x, int xOff, float[] y, int yOff, int len) {
    var sum = 0f;
    for (var i = 0; i < len; ++i)
      sum += x[xOff + i] * y[yOff + i];
    return sum;
  }

  private static int InterpPitch(float[] exc, int excOff, float[] interp, int interpOff, int pitch, int len) {
    var corr = new float[4][];
    for (var i = 0; i < 4; ++i) corr[i] = new float[7];

    for (var i = 0; i < 7; ++i)
      corr[0][i] = InnerProd(exc, excOff, exc, excOff - pitch - 3 + i, len);
    for (var i = 0; i < 3; ++i) {
      for (var j = 0; j < 7; ++j) {
        var i1 = Math.Max(3 - j, 0);
        var i2 = Math.Min(10 - j, 7);
        var tmp = 0f;
        for (var k = i1; k < i2; ++k)
          tmp += SpeexTables.ShiftFilt[i][k] * corr[0][j + k - 3];
        corr[i + 1][j] = tmp;
      }
    }
    var maxi = 0;
    var maxj = 0;
    var maxcorr = corr[0][0];
    for (var i = 0; i < 4; ++i)
      for (var j = 0; j < 7; ++j)
        if (corr[i][j] > maxcorr) { maxcorr = corr[i][j]; maxi = i; maxj = j; }

    for (var i = 0; i < len; ++i) {
      var tmp = 0f;
      if (maxi > 0) {
        for (var k = 0; k < 7; ++k)
          tmp += exc[excOff + i - (pitch - maxj + 3) + k - 3] * SpeexTables.ShiftFilt[maxi - 1][k];
      } else {
        tmp = exc[excOff + i - (pitch - maxj + 3)];
      }
      interp[interpOff + i] = tmp;
    }
    return pitch - maxj + 3;
  }

  private static void Multicomb(float[] exc, int excOff, float[] newExc, int newOff, int nsf,
    int pitch, int maxPitch, float combGain) {
    var iexc = new float[4 * NbSubframeSize];
    var corrPitch = pitch;

    InterpPitch(exc, excOff, iexc, 0, corrPitch, 80);
    if (corrPitch > maxPitch)
      InterpPitch(exc, excOff, iexc, nsf, 2 * corrPitch, 80);
    else
      InterpPitch(exc, excOff, iexc, nsf, -corrPitch, 80);

    var iexc0Mag = MathF.Sqrt(1000f + InnerProd(iexc, 0, iexc, 0, nsf));
    var iexc1Mag = MathF.Sqrt(1000f + InnerProd(iexc, nsf, iexc, nsf, nsf));
    var excMag = MathF.Sqrt(1f + InnerProd(exc, excOff, exc, excOff, nsf));
    var corr0 = InnerProd(iexc, 0, exc, excOff, nsf);
    var corr1 = InnerProd(iexc, nsf, exc, excOff, nsf);
    var pgain1 = corr0 > iexc0Mag * excMag ? 1f : (corr0 / excMag) / iexc0Mag;
    var pgain2 = corr1 > iexc1Mag * excMag ? 1f : (corr1 / excMag) / iexc1Mag;
    var gg1 = excMag / iexc0Mag;
    var gg2 = excMag / iexc1Mag;
    float c1, c2;
    if (combGain > 0f) {
      c1 = .4f * combGain + .07f;
      c2 = .5f + 1.72f * (c1 - .07f);
    } else {
      c1 = c2 = 0f;
    }
    var g1 = 1f - c2 * pgain1 * pgain1;
    var g2 = 1f - c2 * pgain2 * pgain2;
    g1 = MathF.Max(g1, c1);
    g2 = MathF.Max(g2, c1);
    g1 = c1 / g1;
    g2 = c1 / g2;

    float gain0, gain1;
    if (corrPitch > maxPitch) {
      gain0 = .7f * g1 * gg1;
      gain1 = .3f * g2 * gg2;
    } else {
      gain0 = .6f * g1 * gg1;
      gain1 = .6f * g2 * gg2;
    }
    for (var i = 0; i < nsf; ++i)
      newExc[newOff + i] = exc[excOff + i] + gain0 * iexc[i] + gain1 * iexc[nsf + i];
    var newEner = ComputeRms(newExc, newOff, nsf);
    var oldEner = ComputeRms(exc, excOff, nsf);
    oldEner = MathF.Max(oldEner, 1f);
    newEner = MathF.Max(newEner, 1f);
    oldEner = MathF.Min(oldEner, newEner);
    var ngain = oldEner / newEner;
    for (var i = 0; i < nsf; ++i)
      newExc[newOff + i] *= ngain;
  }

  private static void LspInterpolate(float[] oldLsp, float[] newLsp, float[] lsp, int len,
    int subframe, int nbSubframes, float margin) {
    var tmp = (1f + subframe) / nbSubframes;
    for (var i = 0; i < len; ++i) {
      lsp[i] = (1f - tmp) * oldLsp[i] + tmp * newLsp[i];
      lsp[i] = Math.Clamp(lsp[i], margin, Pi - margin);
    }
    for (var i = 1; i < len - 1; ++i) {
      lsp[i] = MathF.Max(lsp[i], lsp[i - 1] + margin);
      if (lsp[i] > lsp[i + 1] - margin)
        lsp[i] = .5f * (lsp[i] + lsp[i + 1] - margin);
    }
  }

  private static void LspToLpc(float[] freq, float[] ak, int lpcrdr) {
    var wp = new float[4 * NbOrder + 2];
    var xFreq = new float[NbOrder];
    var m = lpcrdr >> 1;

    var xin1 = 1f;
    var xin2 = 1f;
    for (var i = 0; i < lpcrdr; ++i) xFreq[i] = -MathF.Cos(freq[i]);

    var lastN0 = 0;
    for (var j = 0; j <= lpcrdr; ++j) {
      var i2 = 0;
      var n0 = 0;
      for (var i = 0; i < m; ++i, i2 += 2) {
        n0 = i * 4;
        var xout1 = xin1 + 2f * xFreq[i2] * wp[n0] + wp[n0 + 1];
        var xout2 = xin2 + 2f * xFreq[i2 + 1] * wp[n0 + 2] + wp[n0 + 3];
        wp[n0 + 1] = wp[n0];
        wp[n0 + 3] = wp[n0 + 2];
        wp[n0] = xin1;
        wp[n0 + 2] = xin2;
        xin1 = xout1;
        xin2 = xout2;
      }
      lastN0 = n0;
      var fout1 = xin1 + wp[lastN0 + 4];
      var fout2 = xin2 - wp[lastN0 + 5];
      if (j > 0) ak[j - 1] = (fout1 + fout2) * 0.5f;
      wp[lastN0 + 4] = xin1;
      wp[lastN0 + 5] = xin2;
      xin1 = 0f;
      xin2 = 0f;
    }
  }

  // ── in-band / stereo handlers ────────────────────────────────────────────────────

  private static void SpeexStdStereo(SpeexBitReader gb, StereoState stereo) {
    var sign = gb.GetBits1() != 0 ? -1f : 1f;
    stereo.Balance = MathF.Exp(sign * .25f * gb.GetBits(5));
    stereo.ERatio = SpeexTables.ERatioQuant[gb.GetBits(2)];
  }

  private static void SpeexInbandHandler(SpeexBitReader gb, StereoState stereo) {
    var id = gb.GetBits(4);
    if (id == SpeexInbandStereo) {
      SpeexStdStereo(gb, stereo);
    } else {
      var adv = id < 2 ? 1 : id < 8 ? 4 : id < 10 ? 8 : id < 12 ? 16 : id < 14 ? 32 : 64;
      gb.SkipBits(adv);
    }
  }

  private static void SpeexDefaultUserHandler(SpeexBitReader gb) {
    var reqSize = gb.GetBits(4);
    gb.SkipBits(5 + 8 * reqSize);
  }

  private static void DecodeStereo(float[] data, int frameSize, StereoState stereo) {
    var balance = stereo.Balance;
    var eRatio = stereo.ERatio;
    var eRight = 1f / MathF.Sqrt(eRatio * (1f + balance));
    var eLeft = MathF.Sqrt(balance) * eRight;
    for (var i = frameSize - 1; i >= 0; --i) {
      var tmp = data[i];
      stereo.SmoothLeft = stereo.SmoothLeft * 0.98f + eLeft * 0.02f;
      stereo.SmoothRight = stereo.SmoothRight * 0.98f + eRight * 0.02f;
      data[2 * i] = stereo.SmoothLeft * tmp;
      data[2 * i + 1] = stereo.SmoothRight * tmp;
    }
  }

  // ── narrowband decode (nb_decode) ────────────────────────────────────────────────

  private static int NbDecode(State st, SpeexBitReader gb, float[] outBuf, StereoState stereo) {
    var olGain = 0f;
    var olPitchCoef = 0f;
    var bestPitchGain = 0f;
    var pitchAverage = 0f;
    var olPitch = 0;
    var bestPitch = 40;
    var m = 0;

    var innov = new float[NbSubframeSize];
    var exc32 = new float[NbSubframeSize];
    var interpQlsp = new float[NbOrder];
    var qlsp = new float[NbOrder];
    var ak = new float[NbOrder];
    var pitchGain = new float[3];
    var pitchVal = new int[1];

    var seed = st.Seed;
    st.ExcOffset = 2 * NbPitchEnd + NbSubframeSize + 6;

    if (st.EncodeSubmode) {
      do {
        if (gb.BitsLeft < 5) return -1;
        var wideband = gb.GetBits1();
        if (wideband != 0) {
          var submode = gb.GetBits(SbSubmodeBits);
          var advance = SpeexTables.WbSkipTable[submode] - (SbSubmodeBits + 1);
          if (advance < 0) return -1;
          gb.SkipBits(advance);
          if (gb.BitsLeft < 5) return -1;
          wideband = gb.GetBits1();
          if (wideband != 0) {
            submode = gb.GetBits(SbSubmodeBits);
            advance = SpeexTables.WbSkipTable[submode] - (SbSubmodeBits + 1);
            if (advance < 0) return -1;
            gb.SkipBits(advance);
            wideband = gb.GetBits1();
            if (wideband != 0) return -1; // more than two wideband layers
          }
        }
        if (gb.BitsLeft < 4) return -1;
        m = gb.GetBits(NbSubmodeBits);
        if (m == 15) return -1;              // terminator
        if (m == 14) SpeexInbandHandler(gb, stereo);
        else if (m == 13) SpeexDefaultUserHandler(gb);
        else if (m > 8) return -1;           // invalid mode
      } while (m > 8);
      st.SubmodeId = m;
    }

    // Shift all buffers by one frame.
    Array.Copy(st.ExcBuf, NbFrameSize, st.ExcBuf, 0, 2 * NbPitchEnd + NbSubframeSize + 12);

    if (st.Submodes[st.SubmodeId] == null) {
      // Null mode: comfort noise.
      var lpc = new float[NbOrder];
      BwLpc(0.93f, st.InterpQlpc, lpc, NbOrder);
      var innovGain = ComputeRms(st.ExcBuf, st.ExcOffset, NbFrameSize);
      for (var i = 0; i < NbFrameSize; ++i)
        st.ExcBuf[st.ExcOffset + i] = SpeexRand(innovGain, ref seed);
      IirMem(st.ExcBuf, st.ExcOffset, lpc, outBuf, 0, NbFrameSize, NbOrder, st.MemSp);
      st.CountLost = 0;
      st.Seed = seed;
      return 0;
    }

    var sub = st.CurrentSubmode;

    LspUnquant(sub.Lsp, qlsp, NbOrder, gb);

    if (st.CountLost != 0) {
      var lspDist = 0f;
      for (var i = 0; i < NbOrder; ++i) lspDist += Math.Abs(st.OldQlsp[i] - qlsp[i]);
      var fact = .6f * MathF.Exp(-.2f * lspDist);
      for (var i = 0; i < NbOrder; ++i) st.MemSp[i] *= fact;
    }

    if (st.First || st.CountLost != 0)
      Array.Copy(qlsp, st.OldQlsp, NbOrder);

    if (sub.LbrPitch != -1)
      olPitch = NbPitchStart + gb.GetBits(7);

    if (sub.ForcedPitchGain != 0)
      olPitchCoef = 0.066667f * gb.GetBits(4);

    olGain = MathF.Exp(gb.GetBits(5) / 3.5f);

    if (st.SubmodeId == 1)
      st.DtxEnabled = gb.GetBits(4) == 15;
    if (st.SubmodeId > 1)
      st.DtxEnabled = false;

    for (var subi = 0; subi < NbNbSubframes; ++subi) {
      var offset = NbSubframeSize * subi;
      var excOff = st.ExcOffset + offset;

      Array.Clear(st.ExcBuf, excOff, NbSubframeSize);

      int pitMin, pitMax;
      if (sub.LbrPitch != -1) {
        var margin = sub.LbrPitch;
        if (margin != 0) {
          pitMin = Math.Max(olPitch - margin + 1, NbPitchStart);
          pitMax = Math.Min(olPitch + margin, NbPitchStart);
        } else {
          pitMin = pitMax = olPitch;
        }
      } else {
        pitMin = NbPitchStart;
        pitMax = NbPitchEnd;
      }
      _ = pitMax;

      if (sub.Ltp == LtpKind.Forced)
        ForcedPitchUnquant(st.ExcBuf, excOff, exc32, pitMin, olPitchCoef, NbSubframeSize, pitchVal, pitchGain);
      else
        PitchUnquant3tap(st.ExcBuf, excOff, exc32, pitMin, sub.LtpParam!, NbSubframeSize,
          pitchVal, pitchGain, gb, st.CountLost, offset, st.LastPitchGain, 0);

      SanitizeValues(exc32, -32000, 32000, NbSubframeSize);

      var tmp = Gain3tapTo1tap(pitchGain);
      var pitch = pitchVal[0];
      pitchAverage += tmp;
      if ((tmp > bestPitchGain &&
           Math.Abs(2 * bestPitch - pitch) >= 3 &&
           Math.Abs(3 * bestPitch - pitch) >= 4 &&
           Math.Abs(4 * bestPitch - pitch) >= 5) ||
          (tmp > .6f * bestPitchGain &&
           (Math.Abs(bestPitch - 2 * pitch) < 3 ||
            Math.Abs(bestPitch - 3 * pitch) < 4 ||
            Math.Abs(bestPitch - 4 * pitch) < 5)) ||
          (.67f * tmp > bestPitchGain &&
           (Math.Abs(2 * bestPitch - pitch) < 3 ||
            Math.Abs(3 * bestPitch - pitch) < 4 ||
            Math.Abs(4 * bestPitch - pitch) < 5))) {
        bestPitch = pitch;
        if (tmp > bestPitchGain) bestPitchGain = tmp;
      }

      Array.Clear(innov, 0, NbSubframeSize);

      float ener;
      if (sub.HaveSubframeGain == 3) {
        var qEnergy = gb.GetBits(3);
        ener = SpeexTables.ExcGainQuantScal3[qEnergy] * olGain;
      } else if (sub.HaveSubframeGain == 1) {
        var qEnergy = gb.GetBits1();
        ener = SpeexTables.ExcGainQuantScal1[qEnergy] * olGain;
      } else {
        ener = olGain;
      }

      InnovationUnquant(sub.Innov, sub.InnovParams, innov, 0, NbSubframeSize, gb, ref seed);
      SignalMul(innov, 0, innov, 0, ener, NbSubframeSize);

      if (sub.DoubleCodebook != 0) {
        var innov2 = new float[NbSubframeSize];
        InnovationUnquant(sub.Innov, sub.InnovParams, innov2, 0, NbSubframeSize, gb, ref seed);
        SignalMul(innov2, 0, innov2, 0, 0.454545f * ener, NbSubframeSize);
        for (var i = 0; i < NbSubframeSize; ++i) innov[i] += innov2[i];
      }

      for (var i = 0; i < NbSubframeSize; ++i)
        st.ExcBuf[excOff + i] = exc32[i] + innov[i];

      if (st.SubmodeId == 1) {
        var g = olPitchCoef;
        g = Math.Clamp(1.5f * (g - .2f), 0f, 1f);
        Array.Clear(st.ExcBuf, excOff, NbSubframeSize);
        while (st.VocOffset < NbSubframeSize) {
          if (st.VocOffset >= 0)
            st.ExcBuf[excOff + st.VocOffset] = MathF.Sqrt(2f * olPitch) * (g * olGain);
          st.VocOffset += olPitch;
        }
        st.VocOffset -= NbSubframeSize;
        for (var i = 0; i < NbSubframeSize; ++i) {
          var exci = st.ExcBuf[excOff + i];
          st.ExcBuf[excOff + i] = .7f * exci + .3f * st.VocM1 + (1f - .85f * g) * innov[i] - .15f * g * st.VocM2;
          st.VocM1 = exci;
          st.VocM2 = innov[i];
          st.VocMean = .8f * st.VocMean + .2f * st.ExcBuf[excOff + i];
          st.ExcBuf[excOff + i] -= st.VocMean;
        }
      }
    }

    if (st.LpcEnhEnabled && sub.CombGain > 0 && st.CountLost == 0) {
      Multicomb(st.ExcBuf, st.ExcOffset - NbSubframeSize, outBuf, 0,
        2 * NbSubframeSize, bestPitch, 40, sub.CombGain);
      Multicomb(st.ExcBuf, st.ExcOffset + NbSubframeSize, outBuf, 2 * NbSubframeSize,
        2 * NbSubframeSize, bestPitch, 40, sub.CombGain);
    } else {
      Array.Copy(st.ExcBuf, st.ExcOffset - NbSubframeSize, outBuf, 0, NbFrameSize);
    }

    if (st.CountLost != 0) {
      var excEner = ComputeRms(st.ExcBuf, st.ExcOffset, NbFrameSize);
      var gain = MathF.Min(olGain / (excEner + 1f), 2f);
      for (var i = 0; i < NbFrameSize; ++i) {
        st.ExcBuf[st.ExcOffset + i] *= gain;
        outBuf[i] = st.ExcBuf[st.ExcOffset + i - NbSubframeSize];
      }
    }

    for (var subi = 0; subi < NbNbSubframes; ++subi) {
      var offset = NbSubframeSize * subi;
      var piG = 1f;
      LspInterpolate(st.OldQlsp, qlsp, interpQlsp, NbOrder, subi, NbNbSubframes, 0.002f);
      LspToLpc(interpQlsp, ak, NbOrder);
      for (var i = 0; i < NbOrder; i += 2) piG += ak[i + 1] - ak[i];
      st.PiGain[subi] = piG;
      st.ExcRms[subi] = ComputeRms(st.ExcBuf, st.ExcOffset + offset, NbSubframeSize);
      IirMem(outBuf, offset, st.InterpQlpc, outBuf, offset, NbSubframeSize, NbOrder, st.MemSp);
      Array.Copy(ak, st.InterpQlpc, NbOrder);
    }

    if (st.HighpassEnabled)
      Highpass(outBuf, outBuf, NbFrameSize, st.MemHp, st.IsWideband ? 1 : 0);

    Array.Copy(qlsp, st.OldQlsp, NbOrder);
    st.CountLost = 0;
    st.LastPitch = bestPitch;
    st.LastPitchGain = .25f * pitchAverage;
    st.First = false;
    st.Seed = seed;
    return 0;
  }

  // ── wideband decode (sb_decode) ──────────────────────────────────────────────────

  private static void QmfSynth(float[] x1, float[] x2, int x2Off, float[] a, float[] y, int n, int mTaps,
    float[] mem1, float[] mem2) {
    var m2 = mTaps >> 1;
    var n2 = n >> 1;
    var xx1 = new float[352];
    var xx2 = new float[352];

    for (var i = 0; i < n2; ++i) xx1[i] = x1[n2 - 1 - i];
    for (var i = 0; i < m2; ++i) xx1[n2 + i] = mem1[2 * i + 1];
    for (var i = 0; i < n2; ++i) xx2[i] = x2[x2Off + n2 - 1 - i];
    for (var i = 0; i < m2; ++i) xx2[n2 + i] = mem2[2 * i + 1];

    for (var i = 0; i < n2; i += 2) {
      float y0 = 0, y1 = 0, y2 = 0, y3 = 0;
      var x10 = xx1[n2 - 2 - i];
      var x20 = xx2[n2 - 2 - i];
      for (var j = 0; j < m2; j += 2) {
        var a0 = a[2 * j];
        var a1 = a[2 * j + 1];
        var x11 = xx1[n2 - 1 + j - i];
        var x21 = xx2[n2 - 1 + j - i];
        y0 += a0 * (x11 - x21);
        y1 += a1 * (x11 + x21);
        y2 += a0 * (x10 - x20);
        y3 += a1 * (x10 + x20);
        a0 = a[2 * j + 2];
        a1 = a[2 * j + 3];
        x10 = xx1[n2 + j - i];
        x20 = xx2[n2 + j - i];
        y0 += a0 * (x10 - x20);
        y1 += a1 * (x10 + x20);
        y2 += a0 * (x11 - x21);
        y3 += a1 * (x11 + x21);
      }
      y[2 * i] = 2f * y0;
      y[2 * i + 1] = 2f * y1;
      y[2 * i + 2] = 2f * y2;
      y[2 * i + 3] = 2f * y3;
    }

    for (var i = 0; i < m2; ++i) mem1[2 * i + 1] = xx1[i];
    for (var i = 0; i < m2; ++i) mem2[2 * i + 1] = xx2[i];
  }

  private int SbDecode(State st, SpeexBitReader gb, float[] outBuf, int packetsLeft) {
    var mode = st.Mode;

    var lowInnovAlias = st.FrameSize; // offset within outBuf where low band saves innovation

    if (st.ModeId > 0) {
      if (packetsLeft * this._frameSize < 2 * st.FrameSize) return -1;
      var low = this._st[st.ModeId - 1];
      low.InnovSave = outBuf;
      low.InnovSaveOffset = lowInnovAlias;
      int ret;
      if (low.Mode.IsWideband)
        ret = this.SbDecode(low, gb, outBuf, packetsLeft);
      else
        ret = NbDecode(low, gb, outBuf, this._stereo);
      if (ret < 0) return ret;
    }

    int wideband;
    if (st.EncodeSubmode) {
      wideband = gb.BitsLeft > 0 ? gb.ShowBits1() : 0;
      if (wideband != 0) {
        wideband = gb.GetBits1();
        st.SubmodeId = gb.GetBits(SbSubmodeBits);
      } else {
        st.SubmodeId = 0;
      }
      if (st.SubmodeId != 0 && st.Submodes[st.SubmodeId] == null) return -1;
    }

    if (st.Submodes[st.SubmodeId] == null) {
      for (var i = 0; i < st.FrameSize; ++i)
        outBuf[st.FrameSize + i] = 1e-15f;
      st.First = true;
      IirMem(outBuf, st.FrameSize, st.InterpQlpc, outBuf, st.FrameSize, st.FrameSize, st.LpcSize, st.MemSp);
      QmfSynth(outBuf, outBuf, st.FrameSize, SpeexTables.H0, outBuf, st.FullFrameSize, QmfOrder, st.G0Mem, st.G1Mem);
      return 0;
    }

    var lowState = this._st[st.ModeId - 1];
    var lowPiGain = (float[])lowState.PiGain.Clone();
    var lowExcRms = (float[])lowState.ExcRms.Clone();

    var sub = st.CurrentSubmode;
    var qlsp = new float[NbOrder];
    var interpQlsp = new float[NbOrder];
    var ak = new float[NbOrder];
    var exc = new float[80];
    var seed = st.Seed;

    LspUnquant(sub.Lsp, qlsp, st.LpcSize, gb);
    if (st.First)
      Array.Copy(qlsp, st.OldQlsp, st.LpcSize);

    for (var subi = 0; subi < st.NbSubframes; ++subi) {
      var offset = st.SubframeSize * subi;
      var spOff = st.FrameSize + offset;

      float[]? innovSave = null;
      var innovSaveOff = 0;
      if (st.InnovSave != null) {
        innovSave = st.InnovSave;
        innovSaveOff = st.InnovSaveOffset + 2 * offset;
        Array.Clear(innovSave, innovSaveOff, 2 * st.SubframeSize);
      }

      LspInterpolate(st.OldQlsp, qlsp, interpQlsp, st.LpcSize, subi, st.NbSubframes, 0.05f);
      LspToLpc(interpQlsp, ak, st.LpcSize);

      var rh = 1f;
      st.PiGain[subi] = 1f;
      for (var i = 0; i < st.LpcSize; i += 2) {
        rh += ak[i + 1] - ak[i];
        st.PiGain[subi] += ak[i] + ak[i + 1];
      }
      var rl = lowPiGain[subi];
      var filterRatio = (rl + .01f) / (rh + .01f);

      Array.Clear(exc, 0, st.SubframeSize);
      if (sub.Innov == InnovKind.None) {
        var x = gb.GetBits(5);
        var g = MathF.Exp(.125f * (x - 10)) / filterRatio;
        for (var i = 0; i < st.SubframeSize; i += 2) {
          exc[i] = mode.FoldingGain * outBuf[lowInnovAlias + offset + i] * g;
          exc[i + 1] = -mode.FoldingGain * outBuf[lowInnovAlias + offset + i + 1] * g;
        }
      } else {
        var el = lowExcRms[subi];
        var gc = 0.87360f * SpeexTables.GcQuantBound[gb.GetBits(4)];
        if (st.SubframeSize == 80) gc *= (float)Math.Sqrt(2.0);
        var scale = gc * el / filterRatio;
        InnovationUnquant(sub.Innov, sub.InnovParams, exc, 0, st.SubframeSize, gb, ref seed);
        SignalMul(exc, 0, exc, 0, scale, st.SubframeSize);
        if (sub.DoubleCodebook != 0) {
          var innov2 = new float[80];
          InnovationUnquant(sub.Innov, sub.InnovParams, innov2, 0, st.SubframeSize, gb, ref seed);
          SignalMul(innov2, 0, innov2, 0, 0.4f * scale, st.SubframeSize);
          for (var i = 0; i < st.SubframeSize; ++i) exc[i] += innov2[i];
        }
      }

      if (innovSave != null)
        for (var i = 0; i < st.SubframeSize; ++i)
          innovSave[innovSaveOff + 2 * i] = exc[i];

      IirMem(st.ExcBuf, 0, st.InterpQlpc, outBuf, spOff, st.SubframeSize, st.LpcSize, st.MemSp);
      Array.Copy(exc, st.ExcBuf, exc.Length);
      Array.Copy(ak, st.InterpQlpc, st.LpcSize);
      st.ExcRms[subi] = ComputeRms(st.ExcBuf, 0, st.SubframeSize);
    }

    QmfSynth(outBuf, outBuf, st.FrameSize, SpeexTables.H0, outBuf, st.FullFrameSize, QmfOrder, st.G0Mem, st.G1Mem);
    Array.Copy(qlsp, st.OldQlsp, st.LpcSize);
    st.First = false;
    st.Seed = seed;
    return 0;
  }
}

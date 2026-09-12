#pragma warning disable CS1591
namespace Codec.G7231;

using static G7231Tables;

/// <summary>
/// Stateful G.723.1 channel decoder — a faithful fixed-point port of FFmpeg's
/// <c>G723_1_ChannelContext</c> plus the <c>g723_1dec.c</c> / <c>g723_1.c</c> functions that
/// operate on it. One instance decodes a single mono channel; persistent state (LSP history,
/// excitation history, synthesis/postfilter memories, comfort-noise seeds) carries across frames
/// exactly as the reference context does.
/// </summary>
internal sealed class G7231Decoder {

  // ── Frame types / rates (mirror FFmpeg enums) ──────────────────────────────────────
  private const int ActiveFrame = 0;
  private const int SidFrame = 1;
  private const int UntransmittedFrame = 2;
  private const int Rate6300 = 0;
  private const int Rate5300 = 1;

  /// <summary>One unpacked subframe (FFmpeg <c>G723_1_Subframe</c>).</summary>
  private struct Subframe {
    public int AdCbLag;
    public int AdCbGain;
    public int DiracTrain;
    public int PulseSign;
    public int GridIndex;
    public int AmpIndex;
    public int PulsePos;
  }

  /// <summary>Pitch-postfilter parameters (FFmpeg <c>PPFParam</c>).</summary>
  private struct PpfParam {
    public int Index;
    public short OptGain;
    public short ScGain;
  }

  private readonly bool _postfilter;

  private readonly Subframe[] _subframe = new Subframe[4];
  private int _curFrameType;
  private int _pastFrameType;
  private int _curRate;
  private readonly int[] _lspIndex = new int[LspBands];
  private readonly int[] _pitchLag = new int[2];
  private int _erasedFrames;

  private readonly short[] _prevLsp = new short[LpcOrder];
  private readonly short[] _sidLsp = new short[LpcOrder];
  private readonly short[] _prevExcitation = new short[PitchMax];
  private readonly short[] _excitation = new short[PitchMax + FrameLen + 4];
  private readonly short[] _synthMem = new short[LpcOrder];
  private readonly short[] _firMem = new short[LpcOrder];
  private readonly int[] _iirMem = new int[LpcOrder];

  private int _randomSeed;
  private int _cngRandomSeed;
  private int _interpIndex;
  private int _interpGain;
  private int _sidGain;
  private int _curGain;
  private int _reflectionCoef;
  private int _pfGain;

  private readonly short[] _audio = new short[FrameLen + LpcOrder + PitchMax + 4];

  public G7231Decoder(bool postfilter) {
    this._postfilter = postfilter;
    this._pfGain = 1 << 12;
    DcLsp.AsSpan().CopyTo(this._prevLsp);
    DcLsp.AsSpan().CopyTo(this._sidLsp);
    this._cngRandomSeed = CngRandomSeed;
    this._pastFrameType = SidFrame;
  }

  /// <summary>Decodes one frame into <paramref name="outSamples"/> (exactly 240 samples).</summary>
  public void DecodeFrame(ReadOnlySpan<byte> buf, Span<short> outSamples) {
    var audioPtr = 0; // offset into _audio that the synthesis filter reads from (the postfilter
                      // path uses _audio directly; the no-postfilter path retargets it)
    var useAudioBuffer = true;

    var badFrame = !UnpackBitstream(buf);
    if (badFrame) {
      if (this._pastFrameType == ActiveFrame)
        this._curFrameType = ActiveFrame;
      else
        this._curFrameType = UntransmittedFrame;
    }

    var lpc = new short[Subframes * LpcOrder];

    if (this._curFrameType == ActiveFrame) {
      if (!badFrame)
        this._erasedFrames = 0;
      else if (this._erasedFrames != 3)
        ++this._erasedFrames;

      var curLsp = new short[LpcOrder];
      InverseQuant(curLsp, this._prevLsp, this._lspIndex, badFrame);
      LspInterpolate(lpc, curLsp, this._prevLsp);
      curLsp.AsSpan().CopyTo(this._prevLsp);

      // Generate the excitation for the frame.
      this._prevExcitation.AsSpan(0, PitchMax).CopyTo(this._excitation);

      if (this._erasedFrames == 0) {
        var vectorPtr = PitchMax; // index into _excitation

        this._interpGain = FixedCbGain[(this._subframe[2].AmpIndex + this._subframe[3].AmpIndex) >> 1];
        var acbVector = new short[SubframeLen];
        for (var i = 0; i < Subframes; ++i) {
          GenFcbExcitation(this._excitation, vectorPtr, ref this._subframe[i], this._curRate,
                           this._pitchLag[i >> 1], i);
          GenAcbExcitation(acbVector, 0, this._excitation, SubframeLen * i,
                           this._pitchLag[i >> 1], ref this._subframe[i], this._curRate);
          for (var j = 0; j < SubframeLen; ++j) {
            var v = AvClipInt16(this._excitation[vectorPtr + j] * 2);
            this._excitation[vectorPtr + j] = AvClipInt16(v + acbVector[j]);
          }
          vectorPtr += SubframeLen;
        }

        vectorPtr = PitchMax;
        this._interpIndex = CompInterpIndex(this._pitchLag[1], ref this._sidGain, ref this._curGain);

        if (this._postfilter) {
          var ppf = new PpfParam[Subframes];
          for (int i = PitchMax, j = 0; j < Subframes; i += SubframeLen, ++j)
            CompPpfCoeff(i, this._pitchLag[j >> 1], ref ppf[j], this._curRate);

          for (int i = 0, j = 0; j < Subframes; i += SubframeLen, ++j)
            AcelpWeightedVectorSum(this._audio, LpcOrder + i,
                                   this._excitation, vectorPtr + i,
                                   this._excitation, vectorPtr + i + ppf[j].Index,
                                   ppf[j].ScGain, ppf[j].OptGain, 1 << 14, 15, SubframeLen);
        } else {
          // audio = vector_ptr - LPC_ORDER → read synthesis input straight from _excitation.
          useAudioBuffer = false;
          audioPtr = vectorPtr - LpcOrder;
        }

        // Save the excitation for the next frame.
        this._excitation.AsSpan(FrameLen, PitchMax).CopyTo(this._prevExcitation);
      } else {
        this._interpGain = (this._interpGain * 3 + 2) >> 2;
        if (this._erasedFrames == 3) {
          // Mute output.
          Array.Clear(this._excitation, 0, FrameLen + PitchMax);
          Array.Clear(this._prevExcitation, 0, PitchMax);
          Array.Clear(this._audio, 0, FrameLen + LpcOrder);
        } else {
          // Regenerate frame via residual interpolation (into _audio + LPC_ORDER).
          ResidualInterp(this._excitation, this._audio, LpcOrder, this._interpIndex,
                         this._interpGain, ref this._randomSeed);
          // prev_excitation = buf + (FRAME_LEN - PITCH_MAX), buf = _audio + LPC_ORDER.
          this._audio.AsSpan(LpcOrder + (FrameLen - PitchMax), PitchMax).CopyTo(this._prevExcitation);
        }
      }

      this._cngRandomSeed = CngRandomSeed;
    } else {
      if (this._curFrameType == SidFrame) {
        this._sidGain = SidGainToLspIndex(this._subframe[0].AmpIndex);
        InverseQuant(this._sidLsp, this._prevLsp, this._lspIndex, false);
      } else if (this._pastFrameType == ActiveFrame) {
        this._sidGain = EstimateSidGain();
      }

      if (this._pastFrameType == ActiveFrame)
        this._curGain = this._sidGain;
      else
        this._curGain = (this._curGain * 7 + this._sidGain) >> 3;

      GenerateNoise();
      LspInterpolate(lpc, this._sidLsp, this._prevLsp);
      this._sidLsp.AsSpan().CopyTo(this._prevLsp);
    }

    this._pastFrameType = this._curFrameType;

    // LP synthesis filter: prepend the saved synthesis memory, filter each subframe.
    this._synthMem.AsSpan().CopyTo(this._audio);
    for (int i = LpcOrder, j = 0; j < Subframes; i += SubframeLen, ++j) {
      if (useAudioBuffer)
        CelpLpSynthesisFilter(this._audio, i, lpc, j * LpcOrder, this._audio, i, SubframeLen);
      else
        CelpLpSynthesisFilter(this._audio, i, lpc, j * LpcOrder, this._excitation, audioPtr + i, SubframeLen);
    }
    this._audio.AsSpan(FrameLen, LpcOrder).CopyTo(this._synthMem);

    if (this._postfilter) {
      FormantPostfilter(lpc, this._audio, outSamples);
    } else {
      for (var i = 0; i < FrameLen; ++i)
        outSamples[i] = AvClipInt16(2 * this._audio[LpcOrder + i]);
    }
  }

  // ── Bitstream unpacking (g723_1dec.c unpack_bitstream) ─────────────────────────────

  /// <summary>Unpacks <paramref name="buf"/> into subframe parameters. Returns false on a forbidden code.</summary>
  private bool UnpackBitstream(ReadOnlySpan<byte> buf) {
    var gb = new BitReaderLe(buf);

    var infoBits = gb.Get(2);
    if (infoBits == 3) {
      this._curFrameType = UntransmittedFrame;
      return true;
    }

    // 24-bit LSP indices, 8 bits per band (high band first on the wire).
    this._lspIndex[2] = gb.Get(8);
    this._lspIndex[1] = gb.Get(8);
    this._lspIndex[0] = gb.Get(8);

    if (infoBits == 2) {
      this._curFrameType = SidFrame;
      this._subframe[0].AmpIndex = gb.Get(6);
      return true;
    }

    this._curRate = infoBits != 0 ? Rate5300 : Rate6300;
    this._curFrameType = ActiveFrame;

    this._pitchLag[0] = gb.Get(7);
    if (this._pitchLag[0] > 123)
      return false; // forbidden code
    this._pitchLag[0] += PitchMin;
    this._subframe[1].AdCbLag = gb.Get(2);

    this._pitchLag[1] = gb.Get(7);
    if (this._pitchLag[1] > 123)
      return false;
    this._pitchLag[1] += PitchMin;
    this._subframe[3].AdCbLag = gb.Get(2);
    this._subframe[0].AdCbLag = 1;
    this._subframe[2].AdCbLag = 1;

    for (var i = 0; i < Subframes; ++i) {
      var temp = gb.Get(12);
      var adCbLen = 170;
      this._subframe[i].DiracTrain = 0;
      if (this._curRate == Rate6300 && this._pitchLag[i >> 1] < SubframeLen - 2) {
        this._subframe[i].DiracTrain = temp >> 11;
        temp &= 0x7FF;
        adCbLen = 85;
      }
      this._subframe[i].AdCbGain = temp / GainLevels;
      if (this._subframe[i].AdCbGain < adCbLen)
        this._subframe[i].AmpIndex = temp - this._subframe[i].AdCbGain * GainLevels;
      else
        return false;
    }

    this._subframe[0].GridIndex = gb.Get(1);
    this._subframe[1].GridIndex = gb.Get(1);
    this._subframe[2].GridIndex = gb.Get(1);
    this._subframe[3].GridIndex = gb.Get(1);

    if (this._curRate == Rate6300) {
      gb.Skip(1); // reserved bit

      var temp = gb.Get(13);
      this._subframe[0].PulsePos = temp / 810;
      temp -= this._subframe[0].PulsePos * 810;
      this._subframe[1].PulsePos = temp / 90;
      temp -= this._subframe[1].PulsePos * 90;
      this._subframe[2].PulsePos = temp / 9;
      this._subframe[3].PulsePos = temp - this._subframe[2].PulsePos * 9;

      this._subframe[0].PulsePos = (this._subframe[0].PulsePos << 16) + gb.Get(16);
      this._subframe[1].PulsePos = (this._subframe[1].PulsePos << 14) + gb.Get(14);
      this._subframe[2].PulsePos = (this._subframe[2].PulsePos << 16) + gb.Get(16);
      this._subframe[3].PulsePos = (this._subframe[3].PulsePos << 14) + gb.Get(14);

      this._subframe[0].PulseSign = gb.Get(6);
      this._subframe[1].PulseSign = gb.Get(5);
      this._subframe[2].PulseSign = gb.Get(6);
      this._subframe[3].PulseSign = gb.Get(5);
    } else {
      this._subframe[0].PulsePos = gb.Get(12);
      this._subframe[1].PulsePos = gb.Get(12);
      this._subframe[2].PulsePos = gb.Get(12);
      this._subframe[3].PulsePos = gb.Get(12);

      this._subframe[0].PulseSign = gb.Get(4);
      this._subframe[1].PulseSign = gb.Get(4);
      this._subframe[2].PulseSign = gb.Get(4);
      this._subframe[3].PulseSign = gb.Get(4);
    }

    return true;
  }

  // ── Test hooks (exposed to the test assembly via InternalsVisibleTo) ───────────────

  /// <summary>Hand-walk hook: inverse-quantize the three LSP VQ indices into 10 LSP values.</summary>
  internal static short[] TestInverseQuant(int band0, int band1, int band2, bool badFrame) {
    var prev = (short[])DcLsp.Clone();
    var cur = new short[LpcOrder];
    InverseQuant(cur, prev, [band0, band1, band2], badFrame);
    return cur;
  }

  /// <summary>Hand-walk hook: decode a 6.3k MP-MLQ subframe excitation from raw pulse fields.</summary>
  internal static short[] TestMpMlqExcitation(int pulsePos, int pulseSign, int gridIndex,
                                              int ampIndex, int index, int diracTrain, int pitchLag) {
    var sf = new Subframe {
      PulsePos = pulsePos, PulseSign = pulseSign, GridIndex = gridIndex,
      AmpIndex = ampIndex, DiracTrain = diracTrain,
    };
    var vec = new short[SubframeLen];
    GenFcbExcitation(vec, 0, ref sf, Rate6300, pitchLag, index);
    return vec;
  }

  /// <summary>Hand-walk hook: decode a 5.3k ACELP algebraic-codebook subframe excitation.</summary>
  internal static short[] TestAcelpExcitation(int pulsePos, int pulseSign, int gridIndex,
                                              int ampIndex, int adCbGain, int adCbLag, int pitchLag) {
    var sf = new Subframe {
      PulsePos = pulsePos, PulseSign = pulseSign, GridIndex = gridIndex,
      AmpIndex = ampIndex, AdCbGain = adCbGain, AdCbLag = adCbLag,
    };
    var vec = new short[SubframeLen];
    GenFcbExcitation(vec, 0, ref sf, Rate5300, pitchLag, 0);
    return vec;
  }

  // ── Fixed codebook excitation (g723_1dec.c gen_fcb_excitation) ─────────────────────

  private static void GenFcbExcitation(short[] vec, int vecOff, ref Subframe subfrm, int curRate,
                                       int pitchLag, int index) {
    Array.Clear(vec, vecOff, SubframeLen);

    if (curRate == Rate6300) {
      if (subfrm.PulsePos >= MaxPos[index])
        return;

      var j = PulseMax - Pulses[index];
      var temp = subfrm.PulsePos;
      for (var i = 0; i < SubframeLen / GridSize; ++i) {
        temp -= Combinatorial[j * 30 + i];
        if (temp >= 0)
          continue;
        temp += Combinatorial[j++ * 30 + i];
        if ((subfrm.PulseSign & (1 << (PulseMax - j))) != 0)
          vec[vecOff + subfrm.GridIndex + GridSize * i] = (short)-FixedCbGain[subfrm.AmpIndex];
        else
          vec[vecOff + subfrm.GridIndex + GridSize * i] = FixedCbGain[subfrm.AmpIndex];
        if (j == PulseMax)
          break;
      }
      if (subfrm.DiracTrain == 1)
        GenDiracTrain(vec, vecOff, pitchLag);
    } else {
      int cbGain = FixedCbGain[subfrm.AmpIndex];
      var cbShift = subfrm.GridIndex;
      var cbSign = subfrm.PulseSign;
      var cbPos = subfrm.PulsePos;

      for (var i = 0; i < 8; i += 2) {
        var offset = ((cbPos & 7) << 3) + cbShift + i;
        vec[vecOff + offset] = (short)((cbSign & 1) != 0 ? cbGain : -cbGain);
        cbPos >>= 3;
        cbSign >>= 1;
      }

      // Enhance harmonic components.
      var lag = PitchContrib[subfrm.AdCbGain << 1] + pitchLag + subfrm.AdCbLag - 1;
      var beta = PitchContrib[(subfrm.AdCbGain << 1) + 1];

      if (lag < SubframeLen - 2)
        for (var i = lag; i < SubframeLen; ++i)
          vec[vecOff + i] += (short)(beta * vec[vecOff + i - lag] >> 15);
    }
  }

  // ── Dirac train (g723_1.c ff_g723_1_gen_dirac_train) ───────────────────────────────

  private static void GenDiracTrain(short[] buf, int bufOff, int pitchLag) {
    var vector = new short[SubframeLen];
    buf.AsSpan(bufOff, SubframeLen).CopyTo(vector);
    for (var i = pitchLag; i < SubframeLen; i += pitchLag)
      for (var j = 0; j < SubframeLen - i; ++j)
        buf[bufOff + i + j] += vector[j];
  }

  // ── Adaptive codebook excitation (g723_1.c) ────────────────────────────────────────

  private static void GenAcbExcitation(short[] vec, int vecOff, short[] prevExc, int prevOff,
                                       int pitchLag, ref Subframe subfrm, int curRate) {
    var residual = new short[SubframeLen + PitchOrder - 1];
    var lag = pitchLag + subfrm.AdCbLag - 1;

    GetResidual(residual, prevExc, prevOff, lag);

    short[] cbTable;
    if (curRate == Rate6300 && pitchLag < SubframeLen - 2)
      cbTable = AdaptiveCbGain85;
    else
      cbTable = AdaptiveCbGain170;

    var cbPtr = subfrm.AdCbGain * 20;
    for (var i = 0; i < SubframeLen; ++i) {
      var sum = DotProductRaw(residual, i, cbTable, cbPtr, PitchOrder);
      vec[vecOff + i] = (short)(AvSatDadd32(1 << 15, (int)AvSatAdd32((int)sum, (int)sum)) >> 16);
    }
  }

  // ── Residual fetch with pitch wrap (g723_1.c ff_g723_1_get_residual) ───────────────

  private static void GetResidual(short[] residual, short[] prevExc, int prevOff, int lag) {
    var offset = PitchMax - PitchOrder / 2 - lag;

    residual[0] = prevExc[prevOff + offset];
    residual[1] = prevExc[prevOff + offset + 1];

    offset += 2;
    for (var i = 2; i < SubframeLen + PitchOrder - 1; ++i)
      residual[i] = prevExc[prevOff + offset + (i - 2) % lag];
  }

  // ── LSP inverse quantization (g723_1.c ff_g723_1_inverse_quant) ────────────────────

  private static void InverseQuant(short[] curLsp, short[] prevLsp, int[] lspIndex, bool badFrame) {
    int minDist, pred;
    if (!badFrame) {
      minDist = 0x100;
      pred = 12288;
    } else {
      minDist = 0x200;
      pred = 23552;
      lspIndex[0] = lspIndex[1] = lspIndex[2] = 0;
    }

    curLsp[0] = LspBand0[lspIndex[0] * 3 + 0];
    curLsp[1] = LspBand0[lspIndex[0] * 3 + 1];
    curLsp[2] = LspBand0[lspIndex[0] * 3 + 2];
    curLsp[3] = LspBand1[lspIndex[1] * 3 + 0];
    curLsp[4] = LspBand1[lspIndex[1] * 3 + 1];
    curLsp[5] = LspBand1[lspIndex[1] * 3 + 2];
    curLsp[6] = LspBand2[lspIndex[2] * 4 + 0];
    curLsp[7] = LspBand2[lspIndex[2] * 4 + 1];
    curLsp[8] = LspBand2[lspIndex[2] * 4 + 2];
    curLsp[9] = LspBand2[lspIndex[2] * 4 + 3];

    for (var i = 0; i < LpcOrder; ++i) {
      var temp = ((prevLsp[i] - DcLsp[i]) * pred + (1 << 14)) >> 15;
      curLsp[i] += (short)(DcLsp[i] + temp);
    }

    var stable = false;
    for (var i = 0; i < LpcOrder; ++i) {
      curLsp[0] = (short)Math.Max((int)curLsp[0], 0x180);
      curLsp[LpcOrder - 1] = (short)Math.Min((int)curLsp[LpcOrder - 1], 0x7e00);

      for (var j = 1; j < LpcOrder; ++j) {
        var temp = minDist + curLsp[j - 1] - curLsp[j];
        if (temp > 0) {
          temp >>= 1;
          curLsp[j - 1] -= (short)temp;
          curLsp[j] += (short)temp;
        }
      }

      stable = true;
      for (var j = 1; j < LpcOrder; ++j) {
        var temp = curLsp[j - 1] + minDist - curLsp[j] - 4;
        if (temp > 0) {
          stable = false;
          break;
        }
      }
      if (stable)
        break;
    }
    if (!stable)
      prevLsp.AsSpan(0, LpcOrder).CopyTo(curLsp);
  }

  // ── LSP interpolation → LPC (g723_1.c ff_g723_1_lsp_interpolate + lsp2lpc) ─────────

  private static void LspInterpolate(short[] lpc, short[] curLsp, short[] prevLsp) {
    AcelpWeightedVectorSum(lpc, 0, curLsp, 0, prevLsp, 0, 4096, 12288, 1 << 13, 14, LpcOrder);
    AcelpWeightedVectorSum(lpc, LpcOrder, curLsp, 0, prevLsp, 0, 8192, 8192, 1 << 13, 14, LpcOrder);
    AcelpWeightedVectorSum(lpc, 2 * LpcOrder, curLsp, 0, prevLsp, 0, 12288, 4096, 1 << 13, 14, LpcOrder);
    curLsp.AsSpan(0, LpcOrder).CopyTo(lpc.AsSpan(3 * LpcOrder, LpcOrder));

    for (var i = 0; i < Subframes; ++i)
      Lsp2Lpc(lpc, i * LpcOrder);
  }

  private static void Lsp2Lpc(short[] lpc, int off) {
    var f1 = new int[LpcOrder / 2 + 1];
    var f2 = new int[LpcOrder / 2 + 1];

    for (var j = 0; j < LpcOrder; ++j) {
      var index = (lpc[off + j] >> 7) & 0x1FF;
      var offset = lpc[off + j] & 0x7f;
      var temp1 = CosTab[index] * (1 << 16);
      var temp2 = (CosTab[index + 1] - CosTab[index]) * (((offset << 8) + 0x80) << 1);
      lpc[off + j] = (short)-(AvSatDadd32(1 << 15, temp1 + temp2) >> 16);
    }

    f1[0] = 1 << 28;
    f1[1] = (lpc[off + 0] + lpc[off + 2]) * (1 << 14);
    f1[2] = lpc[off + 0] * lpc[off + 2] + (2 << 28);

    f2[0] = 1 << 28;
    f2[1] = (lpc[off + 1] + lpc[off + 3]) * (1 << 14);
    f2[2] = lpc[off + 1] * lpc[off + 3] + (2 << 28);

    for (var i = 2; i < LpcOrder / 2; ++i) {
      f1[i + 1] = AvClipl_Int32(f1[i - 1] + (long)Mull2(f1[i], lpc[off + 2 * i]));
      f2[i + 1] = AvClipl_Int32(f2[i - 1] + (long)Mull2(f2[i], lpc[off + 2 * i + 1]));

      for (var j = i; j >= 2; --j) {
        f1[j] = Mull2(f1[j - 1], lpc[off + 2 * i]) + (f1[j] >> 1) + (f1[j - 2] >> 1);
        f2[j] = Mull2(f2[j - 1], lpc[off + 2 * i + 1]) + (f2[j] >> 1) + (f2[j - 2] >> 1);
      }

      f1[0] >>= 1;
      f2[0] >>= 1;
      f1[1] = ((lpc[off + 2 * i] * 65536 >> i) + f1[1]) >> 1;
      f2[1] = ((lpc[off + 2 * i + 1] * 65536 >> i) + f2[1]) >> 1;
    }

    for (var i = 0; i < LpcOrder / 2; ++i) {
      long ff1 = (long)f1[i + 1] + f1[i];
      long ff2 = (long)f2[i + 1] - f2[i];

      lpc[off + i] = (short)(AvClipl_Int32((ff1 + ff2) * 8 + (1 << 15)) >> 16);
      lpc[off + LpcOrder - i - 1] = (short)(AvClipl_Int32((ff1 - ff2) * 8 + (1 << 15)) >> 16);
    }
  }

  // ── Voicing classification (g723_1dec.c comp_interp_index) ─────────────────────────

  private int CompInterpIndex(int pitchLag, ref int excEng, ref int scale) {
    var offset = PitchMax + 2 * SubframeLen;
    // buf = p->audio + LPC_ORDER (we scale into _audio + LPC_ORDER from _excitation).
    scale = ScaleVector(this._audio, LpcOrder, this._excitation, 0, FrameLen + PitchMax);
    var bufOff = LpcOrder + offset;

    var ccr = 0;
    var index = AutocorrMax(this._audio, bufOff, offset, ref ccr, pitchLag, SubframeLen * 2, -1);
    ccr = (int)(AvSatAdd32(ccr, 1 << 15) >> 16);

    var tgtEng = (int)DotProduct(this._audio, bufOff, this._audio, bufOff, SubframeLen * 2);
    excEng = (int)(AvSatAdd32(tgtEng, 1 << 15) >> 16);

    if (ccr <= 0)
      return 0;

    var bestEng = (int)DotProduct(this._audio, bufOff - index, this._audio, bufOff - index, SubframeLen * 2);
    bestEng = (int)(AvSatAdd32(bestEng, 1 << 15) >> 16);

    var temp = (int)((long)bestEng * excEng >> 3);
    if (temp < ccr * ccr)
      return index;
    return 0;
  }

  // ── Residual interpolation for erasure concealment (g723_1dec.c residual_interp) ───

  private static void ResidualInterp(short[] buf, short[] outBuf, int outOff, int lag, int gain,
                                     ref int rseed) {
    if (lag != 0) { // Voiced
      var vectorPtr = PitchMax; // buf + PITCH_MAX
      for (var i = 0; i < lag; ++i)
        outBuf[outOff + i] = (short)(buf[vectorPtr + i - lag] * 3 >> 2);
      // av_memcpy_backptr: propagate the last `lag` samples forward over the rest of the frame.
      for (var i = lag; i < FrameLen; ++i)
        outBuf[outOff + i] = outBuf[outOff + i - lag];
    } else { // Unvoiced
      for (var i = 0; i < FrameLen; ++i) {
        rseed = (short)(rseed * 521 + 259);
        outBuf[outOff + i] = (short)(gain * rseed >> 15);
      }
      Array.Clear(buf, 0, FrameLen + PitchMax);
    }
  }

  // ── Pitch postfilter (g723_1dec.c comp_ppf_coeff / comp_ppf_gains / autocorr_max) ──

  private int AutocorrMax(short[] buf, int bufOff, int offset, ref int ccrMax, int pitchLag,
                          int length, int dir) {
    var lag = 0;
    pitchLag = Math.Min(PitchMax - 3, pitchLag);
    int limit;
    if (dir > 0)
      limit = Math.Min(FrameLen + PitchMax - offset - length, pitchLag + 3);
    else
      limit = pitchLag + 3;

    for (var i = pitchLag - 3; i <= limit; ++i) {
      var ccr = (int)DotProduct(buf, bufOff, buf, bufOff + dir * i, length);
      if (ccr > ccrMax) {
        ccrMax = ccr;
        lag = i;
      }
    }
    return lag;
  }

  private static void CompPpfGains(int lag, ref PpfParam ppf, int curRate, int tgtEng, int ccr,
                                   int resEng) {
    ppf.Index = lag;

    var temp1 = (int)((long)tgtEng * resEng >> 1);
    var temp2 = (int)((long)ccr * ccr << 1);

    if (temp2 > temp1) {
      if (ccr >= resEng)
        ppf.OptGain = PpfGainWeight[curRate];
      else
        ppf.OptGain = (short)((long)((ccr << 15) / resEng) * PpfGainWeight[curRate] >> 15);

      temp1 = (int)(((long)tgtEng << 15) + ((long)ccr * ppf.OptGain << 1));
      temp2 = (int)((ppf.OptGain * ppf.OptGain >> 15) * resEng);
      var pfResidual = (int)(AvSatAdd32(temp1, temp2 + (1 << 15)) >> 16);

      if (tgtEng >= pfResidual << 1)
        temp1 = 0x7fff;
      else
        temp1 = ((int)((long)tgtEng << 14)) / pfResidual;

      ppf.ScGain = SquareRoot((uint)((long)temp1 << 16));
    } else {
      ppf.OptGain = 0;
      ppf.ScGain = 0x7fff;
    }

    ppf.OptGain = AvClipInt16(ppf.OptGain * ppf.ScGain >> 15);
  }

  private void CompPpfCoeff(int offset, int pitchLag, ref PpfParam ppf, int curRate) {
    var energy = new int[5];
    var bufOff = LpcOrder + offset; // p->audio + LPC_ORDER + offset

    var fwdLag = AutocorrMax(this._audio, bufOff, offset, ref energy[1], pitchLag, SubframeLen, 1);
    var backLag = AutocorrMax(this._audio, bufOff, offset, ref energy[3], pitchLag, SubframeLen, -1);

    ppf.Index = 0;
    ppf.OptGain = 0;
    ppf.ScGain = 0x7fff;

    if (backLag == 0 && fwdLag == 0)
      return;

    energy[0] = (int)DotProduct(this._audio, bufOff, this._audio, bufOff, SubframeLen);

    if (fwdLag != 0)
      energy[2] = (int)DotProduct(this._audio, bufOff + fwdLag, this._audio, bufOff + fwdLag, SubframeLen);
    if (backLag != 0)
      energy[4] = (int)DotProduct(this._audio, bufOff - backLag, this._audio, bufOff - backLag, SubframeLen);

    var temp1 = 0;
    for (var i = 0; i < 5; ++i)
      temp1 = Math.Max(energy[i], temp1);

    var scale = NormalizeBits(temp1, 31);
    for (var i = 0; i < 5; ++i)
      energy[i] = (energy[i] << scale) >> 16;

    if (fwdLag != 0 && backLag == 0) {
      CompPpfGains(fwdLag, ref ppf, curRate, energy[0], energy[1], energy[2]);
    } else if (fwdLag == 0) {
      CompPpfGains(-backLag, ref ppf, curRate, energy[0], energy[3], energy[4]);
    } else {
      var t1 = energy[4] * ((energy[1] * energy[1] + (1 << 14)) >> 15);
      var t2 = energy[2] * ((energy[3] * energy[3] + (1 << 14)) >> 15);
      if (t1 >= t2)
        CompPpfGains(fwdLag, ref ppf, curRate, energy[0], energy[1], energy[2]);
      else
        CompPpfGains(-backLag, ref ppf, curRate, energy[0], energy[3], energy[4]);
    }
  }

  // ── Formant postfilter + gain control (g723_1dec.c formant_postfilter / gain_scale) ─

  private void FormantPostfilter(short[] lpc, short[] buf, Span<short> dst) {
    var filterCoef = new short[2 * LpcOrder];
    var filterSignal = new int[LpcOrder + FrameLen];

    // buf[0..LPC_ORDER) = fir_mem; filter_signal[0..LPC_ORDER) = iir_mem.
    this._firMem.AsSpan().CopyTo(buf);
    this._iirMem.AsSpan().CopyTo(filterSignal);

    var lpcPtr = 0;
    for (int i = LpcOrder, j = 0; j < Subframes; i += SubframeLen, ++j) {
      for (var k = 0; k < LpcOrder; ++k) {
        filterCoef[k] = (short)((-lpc[lpcPtr + k] * PostfilterTbl[k] + (1 << 14)) >> 15);
        filterCoef[LpcOrder + k] = (short)((-lpc[lpcPtr + k] * PostfilterTbl[LpcOrder + k] + (1 << 14)) >> 15);
      }
      IirFilter(filterCoef, 0, filterCoef, LpcOrder, buf, i, filterSignal, i);
      lpcPtr += LpcOrder;
    }

    buf.AsSpan(FrameLen, LpcOrder).CopyTo(this._firMem);
    filterSignal.AsSpan(FrameLen, LpcOrder).CopyTo(this._iirMem);

    // FFmpeg uses the (contiguous) output buffer `dst` as both scratch and result; the
    // compensation filter reads filter_signal[j-1] (not dst[j-1]), so a single FRAME_LEN buffer
    // written subframe-by-subframe reproduces it exactly.
    var dstBuf = new short[FrameLen];

    var bufPtr = LpcOrder;     // buf += LPC_ORDER
    var signalPtr = LpcOrder;  // signal_ptr = filter_signal + LPC_ORDER
    var dstOff = 0;
    for (var sf = 0; sf < Subframes; ++sf) {
      var scale = ScaleVector(dstBuf, dstOff, buf, bufPtr, SubframeLen);

      var autoCorr0 = (int)DotProduct(dstBuf, dstOff, dstBuf, dstOff + 1, SubframeLen - 1);
      var autoCorr1 = (int)DotProduct(dstBuf, dstOff, dstBuf, dstOff, SubframeLen);

      var temp = autoCorr1 >> 16;
      if (temp != 0)
        temp = (autoCorr0 >> 2) / temp;
      this._reflectionCoef = (3 * this._reflectionCoef + temp + 2) >> 2;
      temp = -this._reflectionCoef >> 1 & ~3;

      for (var j = 0; j < SubframeLen; ++j)
        dstBuf[dstOff + j] = (short)(AvSatDadd32(filterSignal[signalPtr + j],
                                     (filterSignal[signalPtr + j - 1] >> 16) * temp) >> 16);

      int energy;
      temp = 2 * scale + 4;
      if (temp < 0)
        energy = AvClipl_Int32((long)autoCorr1 << -temp);
      else
        energy = autoCorr1 >> temp;

      GainScale(dstBuf, dstOff, energy);

      bufPtr += SubframeLen;
      signalPtr += SubframeLen;
      dstOff += SubframeLen;
    }

    dstBuf.AsSpan().CopyTo(dst);
  }

  private void GainScale(short[] buf, int bufOff, int energy) {
    int num = energy;
    var denom = 0;
    for (var i = 0; i < SubframeLen; ++i) {
      int temp = buf[bufOff + i] >> 2;
      temp *= temp;
      denom = (int)AvSatDadd32(denom, temp);
    }

    int gain;
    if (num != 0 && denom != 0) {
      var bits1 = NormalizeBits(num, 31);
      var bits2 = NormalizeBits(denom, 31);
      num = num << bits1 >> 1;
      denom <<= bits2;

      bits2 = 5 + bits1 - bits2;
      bits2 = AvClipUintp2(bits2, 5);

      gain = (num >> 1) / (denom >> 16);
      gain = SquareRoot((uint)(gain << 16 >> bits2));
    } else {
      gain = 1 << 12;
    }

    for (var i = 0; i < SubframeLen; ++i) {
      this._pfGain = (15 * this._pfGain + gain + (1 << 3)) >> 4;
      buf[bufOff + i] = AvClipInt16((buf[bufOff + i] * (this._pfGain + (this._pfGain >> 4)) + (1 << 10)) >> 11);
    }
  }

  // IIR filter macro from g723_1dec.c, instantiated for width=1 (32-bit output).
  private static void IirFilter(short[] firCoef, int firOff, short[] iirCoef, int iirOff,
                                short[] src, int srcOff, int[] dest, int destOff) {
    const int width = 1;
    var resShift = 16 & ~-width; // 0
    var inShift = 16 - resShift; // 16

    for (var m = 0; m < SubframeLen; ++m) {
      long filter = 0;
      for (var n = 1; n <= LpcOrder; ++n)
        filter -= (long)firCoef[firOff + n - 1] * src[srcOff + m - n]
                  - (long)iirCoef[iirOff + n - 1] * (dest[destOff + m - n] >> inShift);

      dest[destOff + m] = (int)(AvClipl_Int32((long)src[srcOff + m] * 65536 + filter * 8 + (1 << 15)) >> resShift);
    }
  }

  // ── Comfort noise generation (g723_1dec.c) ─────────────────────────────────────────

  private static int SidGainToLspIndex(int gain) {
    if (gain < 0x10)
      return gain << 6;
    if (gain < 0x20)
      return gain - 8 << 7;
    return gain - 20 << 8;
  }

  private static int CngRand(ref int state, int baseVal) {
    state = (state * 521 + 259) & 0xFFFF;
    return (state & 0x7FFF) * baseVal >> 15;
  }

  private int EstimateSidGain() {
    int t;
    var shift = 16 - this._curGain * 2;
    if (shift > 0) {
      if (this._sidGain == 0)
        t = 0;
      else if (shift >= 31 || (int)((uint)this._sidGain << shift) >> shift != this._sidGain)
        t = this._sidGain < 0 ? int.MinValue : int.MaxValue;
      else
        t = this._sidGain * (1 << shift);
    } else if (shift < -31) {
      t = this._sidGain < 0 ? -1 : 0;
    } else {
      t = this._sidGain >> -shift;
    }

    var x = AvClipl_Int32((long)t * CngFilt[0] >> 16);

    if (x >= CngBseg[2])
      return 0x3F;

    int seg;
    if (x >= CngBseg[1]) {
      shift = 4;
      seg = 3;
    } else {
      shift = 3;
      seg = x >= CngBseg[0] ? 1 : 0;
    }
    var seg2 = Math.Min(seg, 3);

    var val = 1 << shift;
    var valAdd = val >> 1;
    for (var i = 0; i < shift; ++i) {
      t = seg * 32 + (val << seg2);
      t *= t;
      if (x >= t)
        val += valAdd;
      else
        val -= valAdd;
      valAdd >>= 1;
    }

    t = seg * 32 + (val << seg2);
    var y = t * t - x;
    if (y <= 0) {
      t = seg * 32 + (val + 1 << seg2);
      t = t * t - x;
      val = (seg2 - 1) * 16 + val;
      if (t >= y)
        ++val;
    } else {
      t = seg * 32 + (val - 1 << seg2);
      t = t * t - x;
      val = (seg2 - 1) * 16 + val;
      if (t >= y)
        --val;
    }

    return val;
  }

  private void GenerateNoise() {
    var off = new int[Subframes];
    var signs = new int[Subframes / 2 * 11];
    var pos = new int[Subframes / 2 * 11];
    var tmp = new int[SubframeLen * 2];

    this._pitchLag[0] = CngRand(ref this._cngRandomSeed, 21) + 123;
    this._pitchLag[1] = CngRand(ref this._cngRandomSeed, 19) + 123;

    for (var i = 0; i < Subframes; ++i) {
      this._subframe[i].AdCbGain = CngRand(ref this._cngRandomSeed, 50) + 1;
      this._subframe[i].AdCbLag = CngAdaptiveCbLag[i];
    }

    for (var i = 0; i < Subframes / 2; ++i) {
      var t = CngRand(ref this._cngRandomSeed, 1 << 13);
      off[i * 2] = t & 1;
      off[i * 2 + 1] = ((t >> 1) & 1) + SubframeLen;
      t >>= 2;
      for (var j = 0; j < 11; ++j) {
        signs[i * 11 + j] = ((t & 1) * 2 - 1) * (1 << 14);
        t >>= 1;
      }
    }

    var idx = 0;
    for (var i = 0; i < Subframes; ++i) {
      for (var j = 0; j < SubframeLen / 2; ++j)
        tmp[j] = j;
      var t = SubframeLen / 2;
      for (var j = 0; j < Pulses[i]; ++j, ++idx) {
        var idx2 = CngRand(ref this._cngRandomSeed, t);
        pos[idx] = tmp[idx2] * 2 + off[i];
        tmp[idx2] = tmp[--t];
      }
    }

    var vectorPtr = LpcOrder; // p->audio + LPC_ORDER
    this._prevExcitation.AsSpan(0, PitchMax).CopyTo(this._audio.AsSpan(vectorPtr, PitchMax));

    for (var i = 0; i < Subframes; i += 2) {
      GenAcbExcitation(this._audio, vectorPtr, this._audio, vectorPtr,
                       this._pitchLag[i >> 1], ref this._subframe[i], this._curRate);
      GenAcbExcitation(this._audio, vectorPtr + SubframeLen, this._audio, vectorPtr + SubframeLen,
                       this._pitchLag[i >> 1], ref this._subframe[i + 1], this._curRate);

      var t = 0;
      for (var j = 0; j < SubframeLen * 2; ++j)
        t |= Math.Abs((int)this._audio[vectorPtr + j]);
      t = Math.Min(t, 0x7FFF);
      int shift;
      if (t == 0)
        shift = 0;
      else {
        shift = -10 + AvLog2((uint)t);
        if (shift < -2)
          shift = -2;
      }

      long sum = 0;
      if (shift < 0) {
        for (var j = 0; j < SubframeLen * 2; ++j) {
          var v = this._audio[vectorPtr + j] * (1 << -shift);
          sum += (long)v * v;
          tmp[j] = v;
        }
      } else {
        for (var j = 0; j < SubframeLen * 2; ++j) {
          var v = this._audio[vectorPtr + j] >> shift;
          sum += (long)v * v;
          tmp[j] = v;
        }
      }

      long b0 = 0;
      for (var j = 0; j < 11; ++j)
        b0 += (long)tmp[pos[(i / 2) * 11 + j]] * signs[(i / 2) * 11 + j];
      b0 = b0 * 2 * 2979L + (1 << 29) >> 30; // approximated division by 11

      long c = (long)this._curGain * (this._curGain * SubframeLen >> 5);
      if (shift * 2 + 3 >= 0)
        c >>= shift * 2 + 3;
      else
        c <<= -(shift * 2 + 3);
      c = (AvClipl_Int32(sum << 1) - c) * 2979L >> 15;

      var delta = b0 * b0 * 2 - c;
      long x;
      if (delta <= 0) {
        x = -b0;
      } else {
        var d = SquareRoot((uint)delta);
        x = d - b0;
        var tt = d + b0;
        if (Math.Abs(tt) < Math.Abs(x))
          x = -tt;
      }
      ++shift;
      if (shift < 0)
        x >>= -shift;
      else
        x *= 1 << shift;
      x = Math.Clamp(x, -10000, 10000);

      for (var j = 0; j < 11; ++j) {
        idx = (i / 2) * 11 + j;
        this._audio[vectorPtr + pos[idx]] = AvClipInt16(this._audio[vectorPtr + pos[idx]] + (int)(x * signs[idx] >> 15));
      }

      // Copy decoded data as history for the next decoded subframes.
      this._audio.AsSpan(vectorPtr, SubframeLen * 2).CopyTo(this._audio.AsSpan(vectorPtr + PitchMax, SubframeLen * 2));
      vectorPtr += SubframeLen * 2;
    }

    this._audio.AsSpan(LpcOrder + FrameLen, PitchMax).CopyTo(this._prevExcitation);
  }

  // ── Fixed-point primitives (g723_1.c / celp_*.c / libavutil) ───────────────────────

  /// <summary>g723_1.c ff_g723_1_dot_product: 2× saturated sum of products.</summary>
  private static long DotProduct(short[] a, int aOff, short[] b, int bOff, int length) {
    var sum = DotProductRaw(a, aOff, b, bOff, length);
    return AvSatAdd32((int)sum, (int)sum);
  }

  /// <summary>celp_math.c ff_dot_product: plain 64-bit sum of int16 products.</summary>
  private static long DotProductRaw(short[] a, int aOff, short[] b, int bOff, int length) {
    long sum = 0;
    for (var i = 0; i < length; ++i)
      sum += a[aOff + i] * b[bOff + i];
    return sum;
  }

  /// <summary>g723_1.c ff_g723_1_scale_vector.</summary>
  private static int ScaleVector(short[] dst, int dstOff, short[] vector, int vecOff, int length) {
    var max = 0;
    for (var i = 0; i < length; ++i)
      max |= Math.Abs((int)vector[vecOff + i]);

    var bits = 14 - AvLog2_16bit((uint)max);
    bits = Math.Max(bits, 0);

    for (var i = 0; i < length; ++i)
      dst[dstOff + i] = (short)(vector[vecOff + i] * (1 << bits) >> 3);

    return bits - 3;
  }

  /// <summary>g723_1.c ff_g723_1_normalize_bits.</summary>
  private static int NormalizeBits(int num, int width) => width - AvLog2((uint)num) - 1;

  /// <summary>acelp_vectors.c ff_acelp_weighted_vector_sum (clipped to int16).</summary>
  private static void AcelpWeightedVectorSum(short[] outBuf, int outOff, short[] inA, int aOff,
                                             short[] inB, int bOff, int weightA, int weightB,
                                             int rounder, int shift, int length) {
    for (var i = 0; i < length; ++i)
      outBuf[outOff + i] = AvClipInt16((inA[aOff + i] * weightA + inB[bOff + i] * weightB + rounder) >> shift);
  }

  /// <summary>celp_filters.c ff_celp_lp_synthesis_filter (shift=1, rounder=1&lt;&lt;12, no overflow stop).</summary>
  private static void CelpLpSynthesisFilter(short[] outBuf, int outOff, short[] filterCoeffs,
                                            int coefOff, short[] inBuf, int inOff, int bufferLength) {
    const int shift = 1;
    const int rounder = 1 << 12;
    for (var n = 0; n < bufferLength; ++n) {
      var sum = rounder;
      for (var i = 1; i <= LpcOrder; ++i)
        sum -= filterCoeffs[coefOff + i - 1] * outBuf[outOff + n - i];

      var sum1 = ((sum >> 12) + inBuf[inOff + n]) >> shift;
      outBuf[outOff + n] = AvClipInt16(sum1);
    }
  }

  /// <summary>g723_1dec.c square_root: bit-exact sqrt(val/2).</summary>
  private static short SquareRoot(uint val) => (short)((FfSqrt(val << 1) >> 1) & ~1);

  /// <summary>g723_1.h MULL2: 2ab scaled by 1/2^16, bit-exact.</summary>
  private static int Mull2(int a, int b) => ((a >> 16) * b * 2) + ((a & 0xffff) * b >> 15);

  // libavutil saturation / clipping helpers (bit-exact).
  private static short AvClipInt16(int a) => ((a + 0x8000) & ~0xFFFF) != 0 ? (short)((a >> 31) ^ 0x7FFF) : (short)a;

  private static int AvClipl_Int32(long a) =>
    (ulong)(a + 0x80000000L) > 0xFFFFFFFFUL ? (int)((a >> 63) ^ 0x7FFFFFFF) : (int)a;

  private static int AvClipUintp2(int a, int p) =>
    (a & ~((1 << p) - 1)) != 0 ? (~a >> 31) & ((1 << p) - 1) : a;

  private static long AvSatAdd32(int a, int b) => AvClipl_Int32((long)a + b);

  private static long AvSatDadd32(int a, int b) => AvSatAdd32(a, (int)AvSatAdd32(b, b));

  /// <summary>floor(log2(v)); matches FFmpeg av_log2 for v &gt; 0, and returns 0 for v == 0.</summary>
  private static int AvLog2(uint v) => v == 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount(v);

  private static int AvLog2_16bit(uint v) => AvLog2(v);

  /// <summary>mathops.h ff_sqrt: integer square root via <see cref="G7231Tables.SqrtTab"/>.</summary>
  private static uint FfSqrt(uint a) {
    uint b;
    if (a < 255)
      return (uint)((SqrtTab[a + 1] - 1) >> 4);
    if (a < (1 << 12))
      b = (uint)(SqrtTab[a >> 4] >> 2);
    else if (a < (1 << 14))
      b = (uint)(SqrtTab[a >> 6] >> 1);
    else if (a < (1 << 16))
      b = SqrtTab[a >> 8];
    else {
      var s = AvLog2_16bit(a >> 16) >> 1;
      var c = a >> (s + 2);
      b = SqrtTab[c >> (s + 8)];
      b = c / b + (b << s);
    }

    return b - (a < b * b ? 1u : 0u);
  }
}

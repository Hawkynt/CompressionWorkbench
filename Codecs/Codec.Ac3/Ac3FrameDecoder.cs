#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Decodes a single AC-3 (ATSC A/52) sync frame to interleaved 16-bit PCM. One instance is reused
/// across the whole stream so the IMDCT overlap-add memory persists between frames (the spec relies
/// on block-to-block overlap). The decode follows A/52 §6.2: read syncinfo + BSI, then six audio
/// blocks. Each block parses coupling strategy, exponent strategy + exponents, bit-allocation
/// parameters + snroffset + delta, dequantizes mantissas, reconstructs coupling and rematrixed
/// channels, applies exponents, then runs the per-channel IMDCT and overlap-add.
/// </summary>
internal sealed class FrameDecoder {

  private const int MaxChannels = 6;     // 5 full-bandwidth + LFE
  private const int CplChannel = MaxChannels; // pseudo coupling channel slot
  private const int MaxBins = 256;

  // Persistent IMDCT overlap memory per output channel (full-bandwidth + LFE).
  private readonly float[][] _delay;

  public FrameDecoder() {
    this._delay = new float[MaxChannels + 1][];
    for (var c = 0; c <= MaxChannels; ++c)
      this._delay[c] = new float[MaxBins];
  }

  // Per-frame state.
  private int _nfchans;                  // number of full-bandwidth channels
  private bool _lfeon;
  private int _fscod;
  private int _acmod;

  // Per-channel state carried across blocks within a frame (exponents reused, coupling, etc.).
  private readonly byte[][] _exp = NewByteMatrix(MaxChannels + 1, MaxBins);
  private readonly byte[][] _bap = NewByteMatrix(MaxChannels + 1, MaxBins);
  private readonly float[][] _coeffs = NewFloatMatrix(MaxChannels + 1, MaxBins);
  private readonly Ac3Exponents.Strategy[] _expstr = new Ac3Exponents.Strategy[MaxChannels + 1];
  private readonly int[] _endmant = new int[MaxChannels + 1];

  // Coupling carried across blocks.
  private bool _cplInUse;
  private int _cplStrtMant, _cplEndMant;
  private int _cplBegF, _cplEndF, _ncplbnd, _ncplsubnd;
  private readonly int[] _cplBndStruct = new int[18];
  private readonly float[][] _cplCo = NewFloatMatrix(MaxChannels, 18);

  /// <summary>
  /// Decodes the frame whose sync word is at <paramref name="offset"/> in <paramref name="data"/>.
  /// Returns interleaved little-endian 16-bit PCM (6 blocks × 256 samples × channel count), or
  /// <see langword="null"/> if the frame can't be decoded.
  /// </summary>
  public byte[]? DecodeFrame(byte[] data, int offset, Ac3FrameHeader header) {
    try {
      var r = new Ac3BitReader(data, offset, Math.Min(header.FrameSize, data.Length - offset));
      this._fscod = header.FsCod;
      this._acmod = header.Acmod;
      this._nfchans = Ac3FrameHeader.AcmodChannelCount(header.Acmod);
      this._lfeon = header.LowFrequencyEffects;

      SkipBsi(r, header.Acmod);

      var totalChannels = this._nfchans + (this._lfeon ? 1 : 0);
      var pcm = new short[Ac3Codec_BlocksPerFrame * Ac3Codec_SamplesPerBlock * totalChannels];

      for (var blk = 0; blk < Ac3Codec_BlocksPerFrame; ++blk) {
        if (!this.DecodeAudioBlock(r, blk, pcm))
          return null;
      }

      var bytes = new byte[pcm.Length * 2];
      for (var i = 0; i < pcm.Length; ++i) {
        bytes[2 * i] = (byte)(pcm[i] & 0xFF);
        bytes[2 * i + 1] = (byte)((pcm[i] >> 8) & 0xFF);
      }
      return bytes;
    } catch (InvalidDataException) {
      return null;
    } catch (IndexOutOfRangeException) {
      return null;
    }
  }

  private const int Ac3Codec_BlocksPerFrame = 6;
  private const int Ac3Codec_SamplesPerBlock = 256;

  // Skip syncinfo CRC + the entire BSI so the reader sits at the first audblk (A/52 §5.3).
  private void SkipBsi(Ac3BitReader r, int acmod) {
    r.SkipBits(16 + 16);           // sync word (16) + crc1 (16)
    r.SkipBits(2 + 6);             // fscod + frmsizecod
    r.SkipBits(5);                 // bsid
    r.SkipBits(3);                 // bsmod
    r.SkipBits(3);                 // acmod
    if ((acmod & 0x1) != 0 && acmod != 1) r.SkipBits(2);   // cmixlev
    if ((acmod & 0x4) != 0) r.SkipBits(2);                 // surmixlev
    if (acmod == 2) r.SkipBits(2);                         // dsurmod
    r.SkipBits(1);                 // lfeon
    r.SkipBits(5);                 // dialnorm

    void OneSet() {
      if (r.ReadFlag()) r.SkipBits(8);  // compre → compr
      if (r.ReadFlag()) r.SkipBits(8);  // langcode → langcod
      if (r.ReadFlag()) r.SkipBits(7);  // audprodie → mixlevel(5)+roomtyp(2)
    }
    OneSet();
    if (acmod == 0) OneSet();      // 1+1 dual-mono second set
    r.SkipBits(1);                 // copyrightb
    r.SkipBits(1);                 // origbs
    if (r.ReadFlag()) r.SkipBits(14);  // timecod1e → timecod1
    if (r.ReadFlag()) r.SkipBits(14);  // timecod2e → timecod2
    if (r.ReadFlag()) {                // addbsie
      var addbsil = (int)r.ReadBits(6);
      r.SkipBits((addbsil + 1) * 8);
    }
  }

  private bool DecodeAudioBlock(Ac3BitReader r, int blk, short[] pcm) {
    var nfchans = this._nfchans;
    var blksw = new bool[MaxChannels];
    var dithflag = new bool[MaxChannels];

    for (var ch = 0; ch < nfchans; ++ch) blksw[ch] = r.ReadFlag();
    for (var ch = 0; ch < nfchans; ++ch) dithflag[ch] = r.ReadFlag();

    if (r.ReadFlag())              // dynrnge
      r.SkipBits(8);               // dynrng
    if (this._acmod == 0 && r.ReadFlag())  // dynrng2e (dual mono)
      r.SkipBits(8);

    // ── Coupling strategy ────────────────────────────────────────────────────
    var cplstre = r.ReadFlag();
    if (cplstre) {
      this._cplInUse = r.ReadFlag();
      if (this._cplInUse) {
        var chincpl = new bool[MaxChannels];
        for (var ch = 0; ch < nfchans; ++ch)
          chincpl[ch] = r.ReadFlag();
        if (this._acmod == 2)        // phsflginu present only for 2/0
          r.SkipBits(1);
        this._cplBegF = (int)r.ReadBits(4);
        this._cplEndF = (int)r.ReadBits(4);
        this._ncplsubnd = (this._cplEndF + 3) - this._cplBegF; // 3 + cplendf - cplbegf
        this._cplStrtMant = this._cplBegF * 12 + 37;
        this._cplEndMant = this._cplEndF * 12 + 73;
        // Coupling band structure (cplbndstrc): bit per sub-band boundary.
        var ncplbnd = this._ncplsubnd;
        this._cplBndStruct[0] = 0;
        for (var sb = 1; sb < this._ncplsubnd; ++sb) {
          var bnd = r.ReadFlag();
          this._cplBndStruct[sb] = bnd ? 1 : 0;
          if (bnd) --ncplbnd;
        }
        this._ncplbnd = ncplbnd;
        this._chInCpl = chincpl;
      }
    }

    // ── Coupling coordinates ─────────────────────────────────────────────────
    if (this._cplInUse) {
      var cplcoe = false;
      for (var ch = 0; ch < nfchans; ++ch) {
        if (this._chInCpl is { } cic && cic[ch]) {
          if (r.ReadFlag()) {        // cplcoe[ch]
            cplcoe = true;
            var mstrcplco = (int)r.ReadBits(2);   // master coupling coordinate
            for (var bnd = 0; bnd < this._ncplbnd; ++bnd) {
              var cplcoexp = (int)r.ReadBits(4);
              var cplcomant = (int)r.ReadBits(4);
              float mant = cplcoexp == 15 ? cplcomant / 16f : (cplcomant + 16) / 32f;
              var scale = (float)Math.Pow(2.0, -(cplcoexp + 3 * mstrcplco));
              this._cplCo[ch][bnd] = mant * scale * 8f; // A/52 coupling-coordinate scaling
            }
          }
        }
      }
      _ = cplcoe;
      if (this._acmod == 2 && r.ReadFlag()) {     // phsflginu → phsflg per band
        for (var bnd = 0; bnd < this._ncplbnd; ++bnd)
          r.SkipBits(1);
      }
    }

    // ── Rematrixing (2/0 mode only) ──────────────────────────────────────────
    var rematflg = new bool[4];
    if (this._acmod == 2) {
      if (r.ReadFlag()) {            // rematstr
        var nrematbnd = this._cplInUse
          ? (this._cplBegF > 2 ? 3 : this._cplBegF > 0 ? 2 : 1)
          : 4;
        for (var bnd = 0; bnd < nrematbnd; ++bnd)
          rematflg[bnd] = r.ReadFlag();
      }
    }

    // ── Exponent strategy ────────────────────────────────────────────────────
    var cplexpstr = Ac3Exponents.Strategy.Reuse;
    if (this._cplInUse)
      cplexpstr = (Ac3Exponents.Strategy)r.ReadBits(2);
    var chexpstr = new Ac3Exponents.Strategy[MaxChannels];
    for (var ch = 0; ch < nfchans; ++ch)
      chexpstr[ch] = (Ac3Exponents.Strategy)r.ReadBits(2);
    var lfeexpstr = Ac3Exponents.Strategy.Reuse;
    if (this._lfeon)
      lfeexpstr = (Ac3Exponents.Strategy)(r.ReadFlag() ? 1 : 0);

    // Channel bandwidth (chbwcod) → endmant per channel, only when this block sets exponents.
    for (var ch = 0; ch < nfchans; ++ch) {
      if (chexpstr[ch] != Ac3Exponents.Strategy.Reuse) {
        if (this._chInCpl is { } cic && this._cplInUse && cic[ch]) {
          this._endmant[ch] = this._cplStrtMant;
        } else {
          var chbwcod = (int)r.ReadBits(6);
          this._endmant[ch] = (chbwcod + 12) * 3 + 37;
        }
      }
    }

    // ── Exponent decode ──────────────────────────────────────────────────────
    if (this._cplInUse && cplexpstr != Ac3Exponents.Strategy.Reuse) {
      var nmant = this._cplEndMant - this._cplStrtMant;
      var ngrp = (nmant) / (3 * Ac3Exponents.GroupSize(cplexpstr));
      var absExp = (int)r.ReadBits(4);
      // Coupling exponents start at cplStrtMant; absolute exp is the first group exponent.
      DecodeCouplingExponents(r, absExp, ngrp, cplexpstr);
      this._expstr[CplChannel] = cplexpstr;
    }
    for (var ch = 0; ch < nfchans; ++ch) {
      if (chexpstr[ch] != Ac3Exponents.Strategy.Reuse) {
        var nmant = this._endmant[ch];
        var ngrp = Ac3Exponents.GroupCount(nmant, chexpstr[ch]);
        var absExp = (int)r.ReadBits(4);
        Ac3Exponents.Decode(r, this._exp[ch], 0, absExp, ngrp, chexpstr[ch]);
        this._expstr[ch] = chexpstr[ch];
        r.SkipBits(2);             // gainrng (exponent group trailing 2-bit gain range)
      }
    }
    if (this._lfeon && lfeexpstr != Ac3Exponents.Strategy.Reuse) {
      this._endmant[LfeIndex()] = 7;
      var absExp = (int)r.ReadBits(4);
      var ngrp = Ac3Exponents.GroupCount(7, lfeexpstr);
      Ac3Exponents.Decode(r, this._exp[LfeIndex()], 0, absExp, ngrp, lfeexpstr);
      this._expstr[LfeIndex()] = lfeexpstr;
    }

    // ── Bit-allocation parametric info ───────────────────────────────────────
    var baie = r.ReadFlag();
    if (baie) {
      this._sdcycod = (int)r.ReadBits(2);
      this._fdcycod = (int)r.ReadBits(2);
      this._sgaincod = (int)r.ReadBits(2);
      this._dbpbcod = (int)r.ReadBits(2);
      this._floorcod = (int)r.ReadBits(3);
    }
    var allocParams = Ac3BitAllocation.Resolve(this._sdcycod, this._fdcycod, this._sgaincod, this._dbpbcod, this._floorcod);

    // snroffset (csnroffst shared + fsnroffst/fgaincod per channel).
    var snrInUse = r.ReadFlag();   // snroffste
    if (snrInUse) {
      this._csnroffst = (int)r.ReadBits(6);
      if (this._cplInUse) {
        this._cplFSnrOffst = (int)r.ReadBits(4);
        this._cplFGainCod = (int)r.ReadBits(3);
      }
      for (var ch = 0; ch < nfchans; ++ch) {
        this._fSnrOffst[ch] = (int)r.ReadBits(4);
        this._fGainCod[ch] = (int)r.ReadBits(3);
      }
      if (this._lfeon) {
        this._lfeFSnrOffst = (int)r.ReadBits(4);
        this._lfeFGainCod = (int)r.ReadBits(3);
      }
    }

    // Coupling leak (cplleake) — coupling-channel masking-curve init.
    var cplFastLeak = 0;
    var cplSlowLeak = 0;
    if (this._cplInUse) {
      if (r.ReadFlag()) {          // cplleake
        cplFastLeak = (int)r.ReadBits(3);
        cplSlowLeak = (int)r.ReadBits(3);
      }
    }

    // Delta bit allocation (deltbaie + per-channel deltbae/deltba).
    (int Length, int Delta)[]?[] deltas = new (int, int)[]?[MaxChannels + 1];
    if (r.ReadFlag()) {            // deltbaie
      if (this._cplInUse)
        deltas[CplChannel] = ReadDeltaBa(r);
      for (var ch = 0; ch < nfchans; ++ch)
        deltas[ch] = ReadDeltaBa(r);
    }

    // skipfld
    if (r.ReadFlag()) {            // skiple
      var skipl = (int)r.ReadBits(9);
      r.SkipBits(skipl * 8);
    }

    // ── Bit allocation → bap, then mantissa dequant ──────────────────────────
    this.ComputeAllBap(allocParams, deltas, cplFastLeak, cplSlowLeak);

    var m = new Ac3Mantissas(r);
    this.DecodeMantissas(m, dithflag);

    // ── Coupling reconstruction ──────────────────────────────────────────────
    this.ApplyCoupling();

    // ── Rematrixing undo (2/0) ───────────────────────────────────────────────
    this.ApplyRematrix(rematflg);

    // ── Exponent application → transform coefficients already scaled in DecodeMantissas ──

    // ── IMDCT + overlap-add → PCM ────────────────────────────────────────────
    var totalChannels = nfchans + (this._lfeon ? 1 : 0);
    for (var ch = 0; ch < nfchans; ++ch)
      this.TransformChannel(ch, blksw[ch], blk, ch, totalChannels, pcm);
    if (this._lfeon)
      this.TransformChannel(LfeIndex(), blockSwitch: false, blk, nfchans, totalChannels, pcm);

    return true;
  }

  // Bit-allocation parameter state (persists across blocks until baie sets it again).
  private int _sdcycod, _fdcycod, _sgaincod, _dbpbcod, _floorcod;
  private int _csnroffst;
  private readonly int[] _fSnrOffst = new int[MaxChannels];
  private readonly int[] _fGainCod = new int[MaxChannels];
  private int _cplFSnrOffst, _cplFGainCod;
  private int _lfeFSnrOffst, _lfeFGainCod;
  private bool[]? _chInCpl;

  private int LfeIndex() => 5;   // LFE always uses channel slot 5 internally.

  private static (int Length, int Delta)[]? ReadDeltaBa(Ac3BitReader r) {
    var deltbae = (int)r.ReadBits(2);
    // 0 = reuse, 1 = new info, 2 = no delta. We only carry "new info".
    if (deltbae != 1)
      return null;
    var nseg = (int)r.ReadBits(3) + 1;
    var result = new (int, int)[nseg];
    for (var s = 0; s < nseg; ++s) {
      var offset = (int)r.ReadBits(5);
      var length = (int)r.ReadBits(4);
      var delta = (int)r.ReadBits(3);
      // Encode offset into a leading zero-length run so ComputeBap can advance bands.
      result[s] = (offset + length, delta);
    }
    return result;
  }

  private void DecodeCouplingExponents(Ac3BitReader r, int absExp, int ngrp, Ac3Exponents.Strategy strategy) {
    var step = Ac3Exponents.GroupSize(strategy);
    var prev = absExp;
    var bin = this._cplStrtMant;
    var cplExp = this._exp[CplChannel];
    for (var i = 0; i < step; ++i)
      if (bin < MaxBins) cplExp[bin++] = (byte)prev;
    for (var g = 0; g < ngrp; ++g) {
      var word = (int)r.ReadBits(7);
      foreach (var code in stackalloc[] { word / 25, (word / 5) % 5, word % 5 }) {
        prev = Math.Clamp(prev + code - 2, 0, 24);
        for (var i = 0; i < step; ++i)
          if (bin < MaxBins) cplExp[bin++] = (byte)prev;
      }
    }
  }

  private void ComputeAllBap(Ac3BitAllocation.AllocParams p, (int Length, int Delta)[]?[] deltas,
                             int cplFastLeak, int cplSlowLeak) {
    var nfchans = this._nfchans;
    for (var ch = 0; ch < nfchans; ++ch) {
      var snr = (((this._csnroffst - 15) << 4) + this._fSnrOffst[ch]) << 2;
      var fgain = Ac3Tables.FastGain[this._fGainCod[ch] & 7];
      var start = 0;
      var end = this._endmant[ch];
      Ac3BitAllocation.ComputeBap(this._exp[ch], this._bap[ch], start, end, p, fgain, snr, this._fscod,
        isCoupling: false, 0, 0, deltas[ch]);
    }
    if (this._cplInUse) {
      var snr = (((this._csnroffst - 15) << 4) + this._cplFSnrOffst) << 2;
      var fgain = Ac3Tables.FastGain[this._cplFGainCod & 7];
      Ac3BitAllocation.ComputeBap(this._exp[CplChannel], this._bap[CplChannel], this._cplStrtMant, this._cplEndMant,
        p, fgain, snr, this._fscod, isCoupling: true, cplFastLeak << 8, cplSlowLeak << 8, deltas[CplChannel]);
    }
    if (this._lfeon) {
      var snr = (((this._csnroffst - 15) << 4) + this._lfeFSnrOffst) << 2;
      var fgain = Ac3Tables.FastGain[this._lfeFGainCod & 7];
      Ac3BitAllocation.ComputeBap(this._exp[LfeIndex()], this._bap[LfeIndex()], 0, 7, p, fgain, snr, this._fscod,
        isCoupling: false, 0, 0, deltas[LfeIndex()]);
    }
  }

  private void DecodeMantissas(Ac3Mantissas m, bool[] dithflag) {
    var nfchans = this._nfchans;

    // The A/52 mantissa order is interleaved: per bin, each channel reads its mantissa, then the
    // coupling channel's coupled bins are read once. For simplicity and correctness on the common
    // (no-coupling) path we read per channel sequentially; coupling channel bins are read after the
    // full-bandwidth channels up to the coupling start.
    for (var ch = 0; ch < nfchans; ++ch) {
      var end = this._endmant[ch];
      var coeff = this._coeffs[ch];
      var bap = this._bap[ch];
      var exp = this._exp[ch];
      for (var bin = 0; bin < end; ++bin) {
        var mant = m.Next(bap[bin], dithflag[ch]);
        coeff[bin] = mant * Exp2(-exp[bin]);
      }
      for (var bin = end; bin < MaxBins; ++bin)
        coeff[bin] = 0f;
    }

    if (this._cplInUse) {
      var coeff = this._coeffs[CplChannel];
      var bap = this._bap[CplChannel];
      var exp = this._exp[CplChannel];
      Array.Clear(coeff, 0, coeff.Length);
      for (var bin = this._cplStrtMant; bin < this._cplEndMant; ++bin) {
        var mant = m.Next(bap[bin], dither: false);
        coeff[bin] = mant * Exp2(-exp[bin]);
      }
    }

    if (this._lfeon) {
      var coeff = this._coeffs[LfeIndex()];
      var bap = this._bap[LfeIndex()];
      var exp = this._exp[LfeIndex()];
      Array.Clear(coeff, 0, coeff.Length);
      for (var bin = 0; bin < 7; ++bin) {
        var mant = m.Next(bap[bin], dither: false);
        coeff[bin] = mant * Exp2(-exp[bin]);
      }
    }
  }

  private void ApplyCoupling() {
    if (!this._cplInUse || this._chInCpl is not { } chincpl)
      return;
    var cplCoeff = this._coeffs[CplChannel];
    for (var ch = 0; ch < this._nfchans; ++ch) {
      if (!chincpl[ch])
        continue;
      var coeff = this._coeffs[ch];
      for (var bin = this._cplStrtMant; bin < this._cplEndMant; ++bin) {
        var band = CouplingBandOf(bin);
        coeff[bin] = cplCoeff[bin] * this._cplCo[ch][band];
      }
    }
  }

  private int CouplingBandOf(int bin) {
    // Map a coupling-region bin to its coupling band index via cplbndstrc.
    var subband = (bin - this._cplStrtMant) / 12;
    var band = 0;
    for (var sb = 1; sb <= subband && sb < this._ncplsubnd; ++sb)
      if (this._cplBndStruct[sb] == 0)
        ++band;
    return Math.Min(band, 17);
  }

  private void ApplyRematrix(bool[] rematflg) {
    if (this._acmod != 2)
      return;
    // Rematrix band boundaries (A/52 Table 7.4): bins [13,25), [25,37), [37,61), [61,end).
    int[] bounds = [13, 25, 37, 61, this._endmant[0]];
    var coeff0 = this._coeffs[0];
    var coeff1 = this._coeffs[1];
    for (var bnd = 0; bnd < 4; ++bnd) {
      if (!rematflg[bnd])
        continue;
      var s = bounds[bnd];
      var e = Math.Min(bounds[bnd + 1], this._endmant[0]);
      for (var bin = s; bin < e; ++bin) {
        var sum = coeff0[bin] + coeff1[bin];
        var diff = coeff0[bin] - coeff1[bin];
        coeff0[bin] = sum;
        coeff1[bin] = diff;
      }
    }
  }

  private void TransformChannel(int ch, bool blockSwitch, int blk, int outChannel, int totalChannels, short[] pcm) {
    var coeff = this._coeffs[ch];
    var output = new float[MaxBins];
    if (blockSwitch)
      Ac3Imdct.Short(coeff, this._delay[ch], output);
    else
      Ac3Imdct.Long(coeff, this._delay[ch], output);

    var baseOffset = blk * Ac3Codec_SamplesPerBlock * totalChannels + outChannel;
    for (var n = 0; n < Ac3Codec_SamplesPerBlock; ++n) {
      var v = output[n] * 32768f;
      var s = (int)Math.Round(v);
      pcm[baseOffset + n * totalChannels] = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
    }
  }

  private static float Exp2(int e) => (float)Math.Pow(2.0, e);

  private static byte[][] NewByteMatrix(int rows, int cols) {
    var m = new byte[rows][];
    for (var i = 0; i < rows; ++i) m[i] = new byte[cols];
    return m;
  }

  private static float[][] NewFloatMatrix(int rows, int cols) {
    var m = new float[rows][];
    for (var i = 0; i < rows; ++i) m[i] = new float[cols];
    return m;
  }
}

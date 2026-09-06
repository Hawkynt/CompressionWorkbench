#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Decodes a single AC-3 (ATSC A/52) sync frame to interleaved 16-bit PCM. One instance is reused
/// across the whole stream so that the IMDCT overlap-add memory and the block-to-block reuse state
/// persist between frames. The parse follows A/52 Table 5.3 field for field: block switch and dither
/// flags, dynamic range, coupling strategy and coordinates, rematrixing, exponent strategies and
/// exponents, bit-allocation parameters, SNR offsets, coupling leaks, delta bit allocation, the skip
/// field and finally the mantissas. Almost every one of those fields may be omitted in a block and
/// inherited from the previous one, so the decoder state is deliberately long-lived.
/// </summary>
internal sealed class FrameDecoder {

  private const int MaxChannels = 6;          // 5 full-bandwidth + LFE
  private const int CplChannel = MaxChannels; // pseudo coupling channel slot
  private const int LfeChannel = 5;           // LFE always uses channel slot 5 internally
  private const int MaxBins = 256;
  private const int BlocksPerFrame = 6;
  private const int SamplesPerBlock = 256;

  // Rematrixing band boundaries (A/52 Tables 7.25 - 7.28); the last boundary is clamped to the
  // narrower of the two channels' bandwidths, which is where coupling starts when coupling is on.
  private static readonly int[] RematrixBands = [13, 25, 37, 61, 253];

  // Persistent IMDCT overlap memory per output channel (full-bandwidth + LFE).
  private readonly float[][] _delay;

  public FrameDecoder() {
    this._delay = new float[MaxChannels + 1][];
    for (var c = 0; c <= MaxChannels; ++c)
      this._delay[c] = new float[MaxBins];
  }

  // Per-frame state.
  private int _nfchans;
  private bool _lfeon;
  private int _fscod;
  private int _acmod;

  // Per-channel state carried across blocks within a frame.
  private readonly byte[][] _exp = NewByteMatrix(MaxChannels + 1, MaxBins);
  private readonly byte[][] _bap = NewByteMatrix(MaxChannels + 1, MaxBins);
  private readonly float[][] _coeffs = NewFloatMatrix(MaxChannels + 1, MaxBins);
  private readonly int[] _endmant = new int[MaxChannels + 1];

  // Coupling state carried across blocks.
  private bool _cplInUse;
  private bool _phsflgInUse;
  private int _cplStrtMant, _cplEndMant;
  private int _cplBegF, _ncplbnd, _ncplsubnd;
  private readonly int[] _cplBndStruct = new int[18];
  private readonly int[] _cplBandSize = new int[18];
  private readonly bool[] _phsflg = new bool[18];
  private readonly float[][] _cplCo = NewFloatMatrix(MaxChannels, 18);
  private readonly bool[] _chInCpl = new bool[MaxChannels];

  // Bit-allocation state (persists across blocks until the matching enable bit sets it again).
  private int _sdcycod, _fdcycod, _sgaincod, _dbpbcod, _floorcod;
  private int _csnroffst;
  private readonly int[] _fSnrOffst = new int[MaxChannels];
  private readonly int[] _fGainCod = new int[MaxChannels];
  private int _cplFSnrOffst, _cplFGainCod;
  private int _lfeFSnrOffst, _lfeFGainCod;
  private int _cplFastLeak, _cplSlowLeak;
  private readonly Ac3BitAllocation.DeltaSegment[]?[] _deltas =
    new Ac3BitAllocation.DeltaSegment[]?[MaxChannels + 1];

  // Rematrixing and dynamic range state.
  private int[] _channelMap = [];
  private readonly bool[] _rematflg = new bool[4];
  private int _nrematbnd;
  private readonly float[] _dynrng = [1f, 1f];

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
      this._channelMap = Ac3FrameHeader.ChannelMap(header.Acmod, header.LowFrequencyEffects);

      SkipBsi(r, header.Acmod);

      // A/52 §7.2.2.6: the delta bit allocation segments are cleared at the start of every sync
      // frame, so a frame that carries no dba information leaves the parametric allocation alone.
      Array.Clear(this._deltas);

      var totalChannels = this._nfchans + (this._lfeon ? 1 : 0);
      var pcm = new short[BlocksPerFrame * SamplesPerBlock * totalChannels];

      for (var blk = 0; blk < BlocksPerFrame; ++blk)
        if (!this.DecodeAudioBlock(r, blk, pcm))
          return null;

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

  // Skip syncinfo CRC + the entire BSI so the reader sits at the first audblk (A/52 §5.3).
  private static void SkipBsi(Ac3BitReader r, int acmod) {
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

    // ── Dynamic range control (A/52 §7.7.1) ──────────────────────────────────
    if (r.ReadFlag())
      this._dynrng[0] = DecodeDynamicRange((int)r.ReadBits(8));
    else if (blk == 0)
      this._dynrng[0] = 1f;
    if (this._acmod == 0) {
      if (r.ReadFlag())
        this._dynrng[1] = DecodeDynamicRange((int)r.ReadBits(8));
      else if (blk == 0)
        this._dynrng[1] = 1f;
    }

    // ── Coupling strategy ────────────────────────────────────────────────────
    if (r.ReadFlag()) {            // cplstre
      this._cplInUse = r.ReadFlag();
      if (this._cplInUse) {
        for (var ch = 0; ch < nfchans; ++ch)
          this._chInCpl[ch] = r.ReadFlag();
        this._phsflgInUse = this._acmod == 2 && r.ReadFlag();
        this._cplBegF = (int)r.ReadBits(4);
        var cplEndF = (int)r.ReadBits(4);
        this._ncplsubnd = 3 + cplEndF - this._cplBegF;
        if (this._ncplsubnd <= 0 || this._ncplsubnd > 18)
          throw new InvalidDataException("AC-3 coupling sub-band count out of range.");
        this._cplStrtMant = this._cplBegF * 12 + 37;
        this._cplEndMant = (cplEndF + 3) * 12 + 37;

        // cplbndstrc: a set bit folds the sub-band into the previous coupling band.
        this._cplBndStruct[0] = 0;
        for (var sb = 1; sb < this._ncplsubnd; ++sb)
          this._cplBndStruct[sb] = r.ReadFlag() ? 1 : 0;

        var bands = 0;
        for (var sb = 0; sb < this._ncplsubnd; ++sb) {
          if (sb == 0 || this._cplBndStruct[sb] == 0)
            this._cplBandSize[bands++] = 12;
          else
            this._cplBandSize[bands - 1] += 12;
        }
        this._ncplbnd = bands;
      } else {
        Array.Clear(this._chInCpl);
      }
    }

    // ── Coupling coordinates and phase flags ─────────────────────────────────
    if (this._cplInUse) {
      var anyCoords = false;
      for (var ch = 0; ch < nfchans; ++ch) {
        if (!this._chInCpl[ch])
          continue;
        if (!r.ReadFlag())         // cplcoe[ch]
          continue;
        anyCoords = true;
        var mstrcplco = (int)r.ReadBits(2);
        for (var bnd = 0; bnd < this._ncplbnd; ++bnd) {
          var cplcoexp = (int)r.ReadBits(4);
          var cplcomant = (int)r.ReadBits(4);
          var mant = cplcoexp == 15 ? cplcomant / 16f : (cplcomant + 16) / 32f;
          // A/52 §7.4.3: the coordinate is scaled by 8 when the coupled bins are reconstructed;
          // folding that constant in here keeps the reconstruction a single multiply.
          this._cplCo[ch][bnd] = mant * (float)Math.Pow(2.0, -(cplcoexp + 3 * mstrcplco)) * 8f;
        }
      }
      if (this._acmod == 2 && this._phsflgInUse && anyCoords)
        for (var bnd = 0; bnd < this._ncplbnd; ++bnd)
          this._phsflg[bnd] = r.ReadFlag();
    }

    // ── Rematrixing (2/0 mode only) ──────────────────────────────────────────
    if (this._acmod == 2 && r.ReadFlag()) {   // rematstr
      // A/52 §7.5.2: four bands, minus the ones coupling has taken over.
      this._nrematbnd = 4;
      if (this._cplInUse && this._cplStrtMant <= 61)
        this._nrematbnd -= 1 + (this._cplStrtMant == 37 ? 1 : 0);
      Array.Clear(this._rematflg);
      for (var bnd = 0; bnd < this._nrematbnd; ++bnd)
        this._rematflg[bnd] = r.ReadFlag();
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
      lfeexpstr = r.ReadFlag() ? Ac3Exponents.Strategy.D15 : Ac3Exponents.Strategy.Reuse;

    // Channel bandwidth: only sent for a channel that carries new exponents and is not coupled.
    for (var ch = 0; ch < nfchans; ++ch) {
      if (chexpstr[ch] == Ac3Exponents.Strategy.Reuse)
        continue;
      if (this._cplInUse && this._chInCpl[ch]) {
        this._endmant[ch] = this._cplStrtMant;
      } else {
        var chbwcod = (int)r.ReadBits(6);
        if (chbwcod > 60)
          throw new InvalidDataException("AC-3 channel bandwidth code out of range.");
        this._endmant[ch] = chbwcod * 3 + 73;
      }
    }

    // ── Exponents ────────────────────────────────────────────────────────────
    if (this._cplInUse && cplexpstr != Ac3Exponents.Strategy.Reuse) {
      var nmant = this._cplEndMant - this._cplStrtMant;
      var ngrp = nmant / (3 * Ac3Exponents.GroupSize(cplexpstr));
      // A/52 §7.1.3: cplabsexp is transmitted as half the 5-bit reference exponent.
      var absExp = (int)r.ReadBits(4) << 1;
      Ac3Exponents.DecodeCoupling(r, this._exp[CplChannel], this._cplStrtMant, absExp, ngrp, cplexpstr);
    }
    for (var ch = 0; ch < nfchans; ++ch) {
      if (chexpstr[ch] == Ac3Exponents.Strategy.Reuse)
        continue;
      var ngrp = Ac3Exponents.GroupCount(this._endmant[ch], chexpstr[ch]);
      var absExp = (int)r.ReadBits(4);
      Ac3Exponents.Decode(r, this._exp[ch], 0, absExp, ngrp, chexpstr[ch]);
      r.SkipBits(2);               // gainrng
    }
    if (this._lfeon && lfeexpstr != Ac3Exponents.Strategy.Reuse) {
      this._endmant[LfeChannel] = 7;
      var absExp = (int)r.ReadBits(4);
      Ac3Exponents.Decode(r, this._exp[LfeChannel], 0, absExp, Ac3Exponents.GroupCount(7, lfeexpstr), lfeexpstr);
    }

    // ── Bit-allocation parametric info ───────────────────────────────────────
    if (r.ReadFlag()) {            // baie
      this._sdcycod = (int)r.ReadBits(2);
      this._fdcycod = (int)r.ReadBits(2);
      this._sgaincod = (int)r.ReadBits(2);
      this._dbpbcod = (int)r.ReadBits(2);
      this._floorcod = (int)r.ReadBits(3);
    }
    var allocParams = Ac3BitAllocation.Resolve(
      this._sdcycod, this._fdcycod, this._sgaincod, this._dbpbcod, this._floorcod);

    // ── SNR offsets ──────────────────────────────────────────────────────────
    if (r.ReadFlag()) {            // snroffste
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

    // ── Coupling leak initialization ─────────────────────────────────────────
    if (this._cplInUse && r.ReadFlag()) {     // cplleake
      this._cplFastLeak = (int)r.ReadBits(3);
      this._cplSlowLeak = (int)r.ReadBits(3);
    }

    // ── Delta bit allocation ─────────────────────────────────────────────────
    // A/52 Table 5.3 sends every deltbae code first and only then the segment lists, so the two
    // loops cannot be merged: doing so desynchronises the reader as soon as one channel carries
    // new delta information and another does not.
    if (r.ReadFlag()) {            // deltbaie
      var cplMode = 0;
      var chMode = new int[MaxChannels];
      if (this._cplInUse)
        cplMode = (int)r.ReadBits(2);
      for (var ch = 0; ch < nfchans; ++ch)
        chMode[ch] = (int)r.ReadBits(2);
      if (this._cplInUse && cplMode == 1)
        this._deltas[CplChannel] = ReadDeltaSegments(r);
      else if (cplMode == 2)
        this._deltas[CplChannel] = null;
      for (var ch = 0; ch < nfchans; ++ch) {
        if (chMode[ch] == 1)
          this._deltas[ch] = ReadDeltaSegments(r);
        else if (chMode[ch] == 2)
          this._deltas[ch] = null;
      }
    }

    // ── Skip field ───────────────────────────────────────────────────────────
    if (r.ReadFlag()) {            // skiple
      var skipl = (int)r.ReadBits(9);
      r.SkipBits(skipl * 8);
    }

    this.ComputeAllBap(allocParams);

    var m = new Ac3Mantissas(r);
    this.DecodeMantissas(m, dithflag);
    this.ApplyRematrix();

    var totalChannels = nfchans + (this._lfeon ? 1 : 0);
    for (var ch = 0; ch < nfchans; ++ch) {
      // In 1+1 mode the two channels carry independent dynamic range words.
      var gain = this._acmod == 0 && ch == 1 ? this._dynrng[1] : this._dynrng[0];
      this.TransformChannel(ch, blksw[ch], gain, blk, this._channelMap[ch], totalChannels, pcm);
    }
    if (this._lfeon)
      this.TransformChannel(LfeChannel, blockSwitch: false, this._dynrng[0], blk,
        this._channelMap[nfchans], totalChannels, pcm);

    return true;
  }

  // A/52 §7.7.1.2: the 3 msb are a signed exponent (gain = 2^(X+1)), the 5 lsb a fractional
  // mantissa with an implied leading 1 (0.1YYYYY binary). '0000 0000' is unity gain.
  private static float DecodeDynamicRange(int dynrng) {
    var x = ((dynrng >> 5) ^ 4) - 4;      // 3-bit two's complement, -4..3
    var y = dynrng & 0x1F;
    return (32 + y) / 64f * (float)Math.Pow(2.0, x + 1);
  }

  private static Ac3BitAllocation.DeltaSegment[] ReadDeltaSegments(Ac3BitReader r) {
    var nseg = (int)r.ReadBits(3) + 1;
    var result = new Ac3BitAllocation.DeltaSegment[nseg];
    for (var s = 0; s < nseg; ++s) {
      var offset = (int)r.ReadBits(5);
      var length = (int)r.ReadBits(4);
      var value = (int)r.ReadBits(3);
      result[s] = new Ac3BitAllocation.DeltaSegment(offset, length, value);
    }
    return result;
  }

  private void ComputeAllBap(Ac3BitAllocation.AllocParams p) {
    for (var ch = 0; ch < this._nfchans; ++ch) {
      var snr = (((this._csnroffst - 15) << 4) + this._fSnrOffst[ch]) << 2;
      Ac3BitAllocation.ComputeBap(this._exp[ch], this._bap[ch], 0, this._endmant[ch], p,
        Ac3Tables.FastGain[this._fGainCod[ch] & 7], snr, this._fscod,
        isCoupling: false, 0, 0, this._deltas[ch]);
    }
    if (this._cplInUse) {
      var snr = (((this._csnroffst - 15) << 4) + this._cplFSnrOffst) << 2;
      Ac3BitAllocation.ComputeBap(this._exp[CplChannel], this._bap[CplChannel],
        this._cplStrtMant, this._cplEndMant, p,
        Ac3Tables.FastGain[this._cplFGainCod & 7], snr, this._fscod,
        isCoupling: true, this._cplFastLeak, this._cplSlowLeak, this._deltas[CplChannel]);
    }
    if (this._lfeon) {
      var snr = (((this._csnroffst - 15) << 4) + this._lfeFSnrOffst) << 2;
      Ac3BitAllocation.ComputeBap(this._exp[LfeChannel], this._bap[LfeChannel], 0, 7, p,
        Ac3Tables.FastGain[this._lfeFGainCod & 7], snr, this._fscod,
        isCoupling: false, 0, 0, null);
    }
  }

  // A/52 Table 5.3: the coupling channel's mantissas sit immediately after those of the first
  // coupled channel, not at the end of the block. Reading them anywhere else assigns every
  // subsequent mantissa to the wrong bin.
  private void DecodeMantissas(Ac3Mantissas m, bool[] dithflag) {
    var gotCplChan = false;
    for (var ch = 0; ch < this._nfchans; ++ch) {
      var coeff = this._coeffs[ch];
      var bap = this._bap[ch];
      var exp = this._exp[ch];
      var end = this._endmant[ch];
      for (var bin = 0; bin < end; ++bin)
        coeff[bin] = m.Next(bap[bin], dithflag[ch]) * Exp2(-exp[bin]);
      // A coupled channel's own mantissas stop where coupling starts; everything above that is
      // filled in from the coupling channel once every channel's mantissas have been read.
      var zeroFrom = this._cplInUse && this._chInCpl[ch] ? this._cplEndMant : end;
      Array.Clear(coeff, zeroFrom, MaxBins - zeroFrom);

      if (!this._cplInUse || !this._chInCpl[ch] || gotCplChan)
        continue;
      var cplCoeff = this._coeffs[CplChannel];
      var cplBap = this._bap[CplChannel];
      var cplExp = this._exp[CplChannel];
      Array.Clear(cplCoeff);
      for (var bin = this._cplStrtMant; bin < this._cplEndMant; ++bin)
        cplCoeff[bin] = m.Next(cplBap[bin], dither: true) * Exp2(-cplExp[bin]);
      gotCplChan = true;
    }
    if (gotCplChan)
      this.ApplyCoupling();

    if (!this._lfeon)
      return;
    var lfeCoeff = this._coeffs[LfeChannel];
    var lfeBap = this._bap[LfeChannel];
    var lfeExp = this._exp[LfeChannel];
    Array.Clear(lfeCoeff);
    for (var bin = 0; bin < 7; ++bin)
      lfeCoeff[bin] = m.Next(lfeBap[bin], dither: false) * Exp2(-lfeExp[bin]);
  }

  // A/52 §7.4.3: every coupled channel's high band is the coupling channel scaled by that channel's
  // per-band coordinate; in 2/0 mode a set phase flag inverts the right channel for that band.
  private void ApplyCoupling() {
    var cplCoeff = this._coeffs[CplChannel];
    for (var ch = 0; ch < this._nfchans; ++ch) {
      if (!this._chInCpl[ch])
        continue;
      var coeff = this._coeffs[ch];
      var bin = this._cplStrtMant;
      for (var bnd = 0; bnd < this._ncplbnd; ++bnd) {
        var end = Math.Min(bin + this._cplBandSize[bnd], this._cplEndMant);
        var coord = this._cplCo[ch][bnd];
        if (ch == 1 && this._phsflgInUse && this._phsflg[bnd])
          coord = -coord;
        for (; bin < end; ++bin)
          coeff[bin] = cplCoeff[bin] * coord;
      }
    }
  }

  // A/52 §7.5.4: a set flag means the pair was coded as sum/difference, so the decoder undoes it.
  private void ApplyRematrix() {
    if (this._acmod != 2 || this._nrematbnd == 0)
      return;
    var coeff0 = this._coeffs[0];
    var coeff1 = this._coeffs[1];
    var limit = Math.Min(this._endmant[0], this._endmant[1]);
    for (var bnd = 0; bnd < this._nrematbnd; ++bnd) {
      if (!this._rematflg[bnd])
        continue;
      var s = RematrixBands[bnd];
      var e = Math.Min(RematrixBands[bnd + 1], limit);
      for (var bin = s; bin < e; ++bin) {
        var sum = coeff0[bin] + coeff1[bin];
        var diff = coeff0[bin] - coeff1[bin];
        coeff0[bin] = sum;
        coeff1[bin] = diff;
      }
    }
  }

  private void TransformChannel(int ch, bool blockSwitch, float gain, int blk, int outChannel,
                                int totalChannels, short[] pcm) {
    var coeff = this._coeffs[ch];
    if (gain != 1f)
      for (var k = 0; k < MaxBins; ++k)
        coeff[k] *= gain;

    var output = new float[SamplesPerBlock];
    if (blockSwitch)
      Ac3Imdct.Short(coeff, this._delay[ch], output);
    else
      Ac3Imdct.Long(coeff, this._delay[ch], output);

    var baseOffset = blk * SamplesPerBlock * totalChannels + outChannel;
    for (var n = 0; n < SamplesPerBlock; ++n) {
      var s = (int)Math.Round(output[n] * 32768f);
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

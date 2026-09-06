#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// Decodes a single E-AC-3 (ATSC A/52 Annex E) independent-substream sync frame to interleaved
/// 16-bit PCM. One instance is reused across the stream so the IMDCT overlap-add memory persists.
/// The decode reuses the shared A/52 core machinery (<see cref="Ac3Exponents"/>,
/// <see cref="Ac3Mantissas"/>, <see cref="Ac3BitAllocation"/>, <see cref="Ac3Imdct"/>) and adds the
/// Annex E differences: the frame-level audio header (per-frame exponent / AHT / SNR / bit-allocation
/// strategy flags), the LUT-based per-frame exponent strategy, adaptive hybrid transform (AHT) with
/// GAQ-quantized vector-coded pre-mantissas and the 6-point inverse DCT, and per-block flag defaults
/// when the corresponding strategy is absent.
/// <para>
/// Enhanced coupling (ecplinu) is not supported — a frame that signals it raises
/// <see cref="NotSupportedException"/>. Spectral extension (spx) is parsed (so the bitstream stays
/// aligned) but the high-frequency reconstruction is not synthesised: SPX channels keep their decoded
/// core bands and the extension region stays silent. Standard coupling, rematrixing, AHT and the
/// variable block count are fully supported.
/// </para>
/// </summary>
internal sealed class Ac3EnhancedFrameDecoder {

  private const int MaxChannels = 6;            // 5 full-bandwidth + LFE
  private const int CplChannel = MaxChannels;   // pseudo coupling channel slot
  private const int LfeChannel = MaxChannels + 1;
  private const int Slots = MaxChannels + 2;    // fbw(0..4) + cpl(6) + lfe(7) addressing headroom
  private const int MaxBins = 256;

  private readonly float[][] _delay;

  public Ac3EnhancedFrameDecoder() {
    this._delay = new float[Slots][];
    for (var c = 0; c < Slots; ++c)
      this._delay[c] = new float[MaxBins];
  }

  // -- per-frame state --------------------------------------------------------
  private int _nfchans;
  private bool _lfeon;
  private int _fscod;
  private int _acmod;
  private int _numBlocks;

  // audio-frame-header strategy flags
  private bool _expstrAc3Style;                 // ac3_exponent_strategy
  private bool _ahte;                           // parse_aht_info
  private int _snrOffsetStrategy;
  private bool _transProce;
  private bool _blkswe;                         // block_switch_syntax
  private bool _dithflage;                      // dither_flag_syntax
  private bool _bamode;                         // bit_allocation_syntax
  private bool _frmfgaincode;                   // fast_gain_syntax
  private bool _dbaflde;                        // dba_syntax
  private bool _skipflde;                       // skip_syntax
  private bool _spxAtten;                       // parse_spx_atten_data

  // per-block / per-channel exponent strategy [blk][slot]
  private readonly Ac3Exponents.Strategy[][] _expStrategy = NewStrategyMatrix(6, Slots);
  private readonly bool[] _cplStrategyExists = new bool[6];
  private readonly bool[] _cplInUseBlk = new bool[6];
  private readonly bool[] _channelUsesAht = new bool[Slots];

  // shared per-channel decode state
  private readonly byte[][] _exp = NewByteMatrix(Slots, MaxBins);
  private readonly byte[][] _bap = NewByteMatrix(Slots, MaxBins);
  private readonly float[][] _coeffs = NewFloatMatrix(Slots, MaxBins);
  private readonly int[] _startFreq = new int[Slots];
  private readonly int[] _endFreq = new int[Slots];
  private readonly int[] _snrOffset = new int[Slots];
  private readonly int[] _fastGain = new int[Slots];

  // AHT pre-mantissa store: [slot][bin][6 blocks], filled at block 0 for AHT channels.
  private readonly int[][][] _preMantissa = NewPreMantissa();

  // coupling state
  private readonly bool[] _channelInCpl = new bool[Slots];
  private readonly bool[] _firstCplCoords = new bool[Slots];
  private bool _firstCplLeak;
  private bool _phaseFlagsInUse;
  private int _numCplBands;
  private readonly int[] _cplBandSizes = new int[18];
  private readonly float[][] _cplCoords = NewFloatMatrix(Slots, 18);
  private int _cplFastLeak, _cplSlowLeak;

  // spx state (parsed only)
  private bool _spxInUse;
  private int _spxSrcStartFreq;
  private readonly bool[] _channelUsesSpx = new bool[Slots];
  private readonly bool[] _firstSpxCoords = new bool[Slots];

  // bit-allocation parameters (persist across blocks)
  private int _slowDecay, _fastDecay, _slowGain, _dbPerBit, _floor;

  // delta bit allocation per channel
  private readonly int[] _dbaMode = new int[Slots];
  private readonly Ac3BitAllocation.DeltaSegment[]?[] _deltas = new Ac3BitAllocation.DeltaSegment[]?[Slots];

  /// <summary>
  /// Decodes the independent-substream frame at <paramref name="offset"/>. Returns interleaved
  /// little-endian 16-bit PCM (numBlocks × 256 samples × channel count), or <see langword="null"/>
  /// if the frame can't be decoded. Throws <see cref="NotSupportedException"/> for enhanced coupling.
  /// </summary>
  public byte[]? DecodeFrame(byte[] data, int offset, Ac3FrameHeader header) {
    var r = new Ac3BitReader(data, offset, Math.Min(header.FrameSize, data.Length - offset));
    this._fscod = header.FsCod;
    this._acmod = header.Acmod;
    this._nfchans = Ac3FrameHeader.AcmodChannelCount(header.Acmod);
    this._lfeon = header.LowFrequencyEffects;
    this._numBlocks = header.NumBlocks;
    this._frameSizeMinus2 = header.FrameSize / 2 - 1;   // frame size in 16-bit words, minus 2

    var bsi = Ac3EnhancedBsi.Parse(r);
    if (bsi.SubstreamId != 0)
      return null;                              // only substream 0 is decoded

    this.ParseAudioFrameHeader(r);

    var totalChannels = this._nfchans + (this._lfeon ? 1 : 0);
    var pcm = new short[this._numBlocks * 256 * totalChannels];

    try {
      for (var blk = 0; blk < this._numBlocks; ++blk)
        if (!this.DecodeAudioBlock(r, blk, pcm))
          return null;
    } catch (InvalidDataException) {
      return null;
    } catch (IndexOutOfRangeException) {
      return null;
    }

    var bytes = new byte[pcm.Length * 2];
    for (var i = 0; i < pcm.Length; ++i) {
      bytes[2 * i] = (byte)(pcm[i] & 0xFF);
      bytes[2 * i + 1] = (byte)((pcm[i] >> 8) & 0xFF);
    }
    return bytes;
  }

  // -- audio frame header (A/52 Annex E §E.2.3, FFmpeg ff_eac3_parse_header) -----------------
  private void ParseAudioFrameHeader(Ac3BitReader r) {
    if (this._numBlocks == 6) {
      this._expstrAc3Style = r.ReadFlag();      // ac3_exponent_strategy
      this._ahte = r.ReadFlag();                // parse_aht_info
    } else {
      this._expstrAc3Style = true;              // < 6 blocks: AC-3-style, no AHT
      this._ahte = false;
    }

    this._snrOffsetStrategy = (int)r.ReadBits(2);
    this._transProce = r.ReadFlag();
    this._blkswe = r.ReadFlag();
    this._dithflage = r.ReadFlag();
    this._bamode = r.ReadFlag();
    this._frmfgaincode = r.ReadFlag();
    this._dbaflde = r.ReadFlag();
    this._skipflde = r.ReadFlag();
    this._spxAtten = r.ReadFlag();

    // default bit-allocation parameters when bit_allocation_syntax is off (FFmpeg defaults).
    if (!this._bamode) {
      this._slowDecay = Ac3Tables.SlowDecay[2];
      this._fastDecay = Ac3Tables.FastDecay[1];
      this._slowGain = Ac3Tables.SlowGain[1];
      this._dbPerBit = Ac3Tables.DbPerBit[2];
      this._floor = Ac3Tables.Floor[7];
    }

    // coupling strategy occurrence + coupling-in-use per block.
    var numCplBlocks = 0;
    if (this._acmod > 1) {
      for (var blk = 0; blk < this._numBlocks; ++blk) {
        this._cplStrategyExists[blk] = blk == 0 || r.ReadFlag();
        this._cplInUseBlk[blk] = this._cplStrategyExists[blk]
          ? r.ReadFlag()
          : this._cplInUseBlk[blk - 1];
        if (this._cplInUseBlk[blk]) ++numCplBlocks;
      }
    } else {
      Array.Clear(this._cplInUseBlk, 0, this._cplInUseBlk.Length);
    }

    // exponent strategy data.
    if (this._expstrAc3Style) {
      for (var blk = 0; blk < this._numBlocks; ++blk)
        for (var ch = this._cplInUseBlk[blk] ? CplChannel : 0; ; ch = NextChannel(ch)) {
          this._expStrategy[blk][ch] = (Ac3Exponents.Strategy)r.ReadBits(2);
          if (ch == this.LastFbw()) break;
        }
    } else {
      // LUT-based: one 5-bit code per channel selects the 6-block strategy row.
      var startCh = this._acmod > 1 && numCplBlocks != 0 ? CplChannel : 0;
      for (var ch = startCh; ; ch = NextChannel(ch)) {
        var code = (int)r.ReadBits(5);
        var row = Ac3EnhancedTables.FrameExpStrategy[code];
        for (var blk = 0; blk < 6; ++blk)
          this._expStrategy[blk][ch] = (Ac3Exponents.Strategy)row[blk];
        if (ch == this.LastFbw()) break;
      }
    }
    // LFE exponent strategy (1 bit per block).
    if (this._lfeon)
      for (var blk = 0; blk < this._numBlocks; ++blk)
        this._expStrategy[blk][LfeChannel] = r.ReadFlag() ? Ac3Exponents.Strategy.D15 : Ac3Exponents.Strategy.Reuse;

    // converted-from-AC-3 exponent strategy (skip).
    if (this._numBlocks == 6 || r.ReadFlag())
      r.SkipBits(5 * this._nfchans);

    // AHT channel selection.
    Array.Clear(this._channelUsesAht, 0, this._channelUsesAht.Length);
    if (this._ahte)
      this.ParseAhtSelection(r, numCplBlocks);

    // per-frame SNR offset.
    if (this._snrOffsetStrategy == 0) {
      var csnroffst = ((int)r.ReadBits(6) - 15) << 4;
      var snroffst = (csnroffst + (int)r.ReadBits(4)) << 2;
      for (var ch = 0; ch < Slots; ++ch)
        this._snrOffset[ch] = snroffst;
    }

    // transient pre-noise processing data (parse-skip).
    if (this._transProce)
      for (var ch = 0; ch < this._nfchans; ++ch)
        if (r.ReadFlag())
          r.SkipBits(10 + 8);                   // location + length

    // spectral extension attenuation data (parse-skip).
    if (this._spxAtten)
      for (var ch = 0; ch < this._nfchans; ++ch)
        if (r.ReadFlag())
          r.SkipBits(5);                        // spxattencod

    // block start information (parse-skip): (numblks-1) * (4 + ceil(log2(frame_size-2))) bits.
    if (this._numBlocks > 1 && r.ReadFlag()) {
      var bits = (this._numBlocks - 1) * (4 + Log2(this._frameSizeMinus2));
      r.SkipBits(bits);
    }

    for (var ch = 0; ch < Slots; ++ch) {
      this._firstSpxCoords[ch] = true;
      this._firstCplCoords[ch] = true;
    }
    this._firstCplLeak = true;
  }

  private int _frameSizeMinus2;

  private void ParseAhtSelection(Ac3BitReader r, int numCplBlocks) {
    // For AHT to apply, blocks 1..5 must reuse exponents (and the coupling channel additionally needs
    // a consistent coupling strategy). The coupling-channel slot is checked first when every block
    // couples; otherwise iteration is fbw 0..nfchans-1 then LFE.
    foreach (var ch in this.AhtChannelOrder(numCplBlocks)) {
      var useAht = true;
      for (var blk = 1; blk < 6; ++blk) {
        var isCpl = ch == CplChannel;
        if (this._expStrategy[blk][ch] != Ac3Exponents.Strategy.Reuse ||
            (isCpl && this._cplStrategyExists[blk])) {
          useAht = false;
          break;
        }
      }
      this._channelUsesAht[ch] = useAht && r.ReadFlag();
    }
  }

  private IEnumerable<int> AhtChannelOrder(int numCplBlocks) {
    if (numCplBlocks == 6)
      yield return CplChannel;
    for (var ch = 0; ch < this._nfchans; ++ch)
      yield return ch;
    if (this._lfeon)
      yield return LfeChannel;
  }

  // -- per-block decode -------------------------------------------------------
  private bool DecodeAudioBlock(Ac3BitReader r, int blk, short[] pcm) {
    var nfchans = this._nfchans;

    // block switch flags.
    var blksw = new bool[Slots];
    if (this._blkswe)
      for (var ch = 0; ch < nfchans; ++ch)
        blksw[ch] = r.ReadFlag();

    // dither flags.
    var dithflag = new bool[Slots];
    if (this._dithflage) {
      for (var ch = 0; ch < nfchans; ++ch)
        dithflag[ch] = r.ReadFlag();
    } else {
      for (var ch = 0; ch < nfchans; ++ch)
        dithflag[ch] = true;                    // default when syntax absent
    }

    // dynamic range (1 set, 2 for dual mono).
    var sets = this._acmod == 0 ? 2 : 1;
    for (var i = 0; i < sets; ++i)
      if (r.ReadFlag())
        r.SkipBits(8);

    // spectral extension strategy.
    if (blk == 0 || r.ReadFlag()) {
      this._spxInUse = r.ReadFlag();
      if (this._spxInUse)
        this.ParseSpxStrategy(r, blk);
    }
    if (!this._spxInUse)
      for (var ch = 0; ch < nfchans; ++ch) {
        this._channelUsesSpx[ch] = false;
        this._firstSpxCoords[ch] = true;
      }

    // spectral extension coordinates (parse-skip; reconstruction not synthesised).
    if (this._spxInUse)
      this.ParseSpxCoordinates(r);

    // coupling strategy.
    if (this._cplStrategyExists[blk]) {
      if (!this.ParseCouplingStrategy(r, blk))
        return false;
    }
    var cplInUse = this._cplInUseBlk[blk];

    // coupling coordinates.
    if (cplInUse)
      this.ParseCouplingCoordinates(r, blk);

    // rematrixing.
    var rematflg = new bool[4];
    var numRematBands = 0;
    if (this._acmod == 2) {
      if (blk == 0 || r.ReadFlag()) {
        numRematBands = 4;
        if (cplInUse && this._startFreq[CplChannel] <= 61)
          numRematBands -= 1 + (this._startFreq[CplChannel] == 37 ? 1 : 0);
        else if (this._spxInUse && this._spxSrcStartFreq <= 61)
          --numRematBands;
        for (var bnd = 0; bnd < numRematBands; ++bnd)
          rematflg[bnd] = r.ReadFlag();
      }
    }

    // exponent strategies are pre-decoded from the frame header (no per-block read for E-AC-3).
    // channel bandwidth → end_freq when this block sets exponents.
    for (var ch = 0; ch < nfchans; ++ch) {
      this._startFreq[ch] = 0;
      if (this._expStrategy[blk][ch] != Ac3Exponents.Strategy.Reuse) {
        if (this._channelInCpl[ch])
          this._endFreq[ch] = this._startFreq[CplChannel];
        else if (this._channelUsesSpx[ch])
          this._endFreq[ch] = this._spxSrcStartFreq;
        else {
          var bwcod = (int)r.ReadBits(6);
          if (bwcod > 60) return false;
          this._endFreq[ch] = bwcod * 3 + 73;
        }
      }
    }
    // The coupling channel's start/end freq are already set by the coupling-strategy parse.

    // decode exponents.
    if (cplInUse && this._expStrategy[blk][CplChannel] != Ac3Exponents.Strategy.Reuse) {
      var nmant = this._endFreq[CplChannel] - this._startFreq[CplChannel];
      var strat = this._expStrategy[blk][CplChannel];
      var ngrp = nmant / (3 * Ac3Exponents.GroupSize(strat));
      var absExp = (int)r.ReadBits(4) << 1;
      Ac3Exponents.DecodeCoupling(r, this._exp[CplChannel], this._startFreq[CplChannel], absExp, ngrp, strat);
    }
    for (var ch = 0; ch < nfchans; ++ch) {
      var strat = this._expStrategy[blk][ch];
      if (strat == Ac3Exponents.Strategy.Reuse) continue;
      var ngrp = Ac3Exponents.GroupCount(this._endFreq[ch], strat);
      var absExp = (int)r.ReadBits(4);
      Ac3Exponents.Decode(r, this._exp[ch], 0, absExp, ngrp, strat);
      r.SkipBits(2);                            // gainrng
    }
    if (this._lfeon) {
      var strat = this._expStrategy[blk][LfeChannel];
      if (strat != Ac3Exponents.Strategy.Reuse) {
        this._endFreq[LfeChannel] = 7;
        var ngrp = Ac3Exponents.GroupCount(7, strat);
        var absExp = (int)r.ReadBits(4);
        Ac3Exponents.Decode(r, this._exp[LfeChannel], 0, absExp, ngrp, strat);
      }
    }

    // bit-allocation parameters.
    if (this._bamode && r.ReadFlag()) {
      this._slowDecay = Ac3Tables.SlowDecay[(int)r.ReadBits(2)];
      this._fastDecay = Ac3Tables.FastDecay[(int)r.ReadBits(2)];
      this._slowGain = Ac3Tables.SlowGain[(int)r.ReadBits(2)];
      this._dbPerBit = Ac3Tables.DbPerBit[(int)r.ReadBits(2)];
      this._floor = Ac3Tables.Floor[(int)r.ReadBits(3)];
    }

    // SNR offsets (block 0 only for E-AC-3, gated by snr_offset_strategy).
    if (blk == 0 && this._snrOffsetStrategy != 0 && r.ReadFlag()) {
      var csnr = ((int)r.ReadBits(6) - 15) << 4;
      var prev = 0;
      var isFirst = true;
      foreach (var ch in this.SnrChannelOrder(cplInUse)) {
        if (isFirst || this._snrOffsetStrategy == 2)
          prev = (csnr + (int)r.ReadBits(4)) << 2;
        this._snrOffset[ch] = prev;
        isFirst = false;
      }
    }

    // fast gain (E-AC-3): gated by fast_gain_syntax, else default at block 0.
    if (this._frmfgaincode && r.ReadFlag()) {
      foreach (var ch in this.SnrChannelOrder(cplInUse))
        this._fastGain[ch] = Ac3Tables.FastGain[(int)r.ReadBits(3)];
    } else if (blk == 0) {
      foreach (var ch in this.SnrChannelOrder(cplInUse))
        this._fastGain[ch] = Ac3Tables.FastGain[4];
    }

    // E-AC-3 → AC-3 converter SNR offset (skip).
    if (r.ReadFlag())
      r.SkipBits(10);

    // coupling leak.
    if (cplInUse) {
      if (this._firstCplLeak || r.ReadFlag()) {
        this._cplFastLeak = (int)r.ReadBits(3);
        this._cplSlowLeak = (int)r.ReadBits(3);
      }
      this._firstCplLeak = false;
    }

    // delta bit allocation.
    Array.Clear(this._deltas, 0, this._deltas.Length);
    if (this._dbaflde && r.ReadFlag()) {
      foreach (var ch in this.DbaChannelOrder(cplInUse)) {
        this._dbaMode[ch] = (int)r.ReadBits(2);
        if (this._dbaMode[ch] == 3) return false;  // reserved
      }
      foreach (var ch in this.DbaChannelOrder(cplInUse))
        if (this._dbaMode[ch] == 1)
          this._deltas[ch] = ReadDeltaBa(r);
    } else if (blk == 0) {
      for (var ch = 0; ch < Slots; ++ch)
        this._dbaMode[ch] = 0;
    }

    this.ComputeAllBap(cplInUse);

    // skip field.
    if (this._skipflde && r.ReadFlag()) {
      var skipl = (int)r.ReadBits(9);
      r.SkipBits(skipl * 8);
    }

    // mantissas / AHT.
    this.DecodeTransformCoeffs(r, blk, cplInUse, dithflag);

    // coupling reconstruction.
    if (cplInUse)
      this.ApplyCoupling();

    // rematrixing.
    if (this._acmod == 2)
      this.ApplyRematrix(rematflg, numRematBands);

    // IMDCT + overlap-add.
    var totalChannels = nfchans + (this._lfeon ? 1 : 0);
    for (var ch = 0; ch < nfchans; ++ch)
      this.TransformChannel(ch, blksw[ch], blk, ch, totalChannels, pcm);
    if (this._lfeon)
      this.TransformChannel(LfeChannel, blockSwitch: false, blk, nfchans, totalChannels, pcm);

    return true;
  }

  // -- spectral extension (parse only) ----------------------------------------
  private void ParseSpxStrategy(Ac3BitReader r, int blk) {
    if (this._acmod == 1)
      this._channelUsesSpx[0] = true;
    else {
      for (var ch = 0; ch < this._nfchans; ++ch)
        this._channelUsesSpx[ch] = r.ReadFlag();
    }
    var startSubband = (int)r.ReadBits(2) + 2;
    var endSubband = (int)r.ReadBits(3) + 5;
    this._spxSrcStartFreq = startSubband * 12 + 25;
    this.DecodeSpxBandStructure(r, blk, startSubband, endSubband, Ac3EnhancedTables.DefaultSpxBandStruct);
  }

  private void ParseSpxCoordinates(Ac3BitReader r) {
    for (var ch = 0; ch < this._nfchans; ++ch) {
      if (!this._channelUsesSpx[ch]) {
        this._firstSpxCoords[ch] = true;
        continue;
      }
      if (this._firstSpxCoords[ch] || r.ReadFlag()) {
        this._firstSpxCoords[ch] = false;
        r.SkipBits(5);                          // spxblnd
        r.SkipBits(2);                          // mstrspxco
        for (var bnd = 0; bnd < this._numSpxBands; ++bnd) {
          var exp = (int)r.ReadBits(4);
          r.SkipBits(2);                        // spxcomant
          _ = exp;
        }
      }
    }
  }

  private int _numSpxBands;

  // -- coupling ---------------------------------------------------------------
  private bool ParseCouplingStrategy(Ac3BitReader r, int blk) {
    if (!this._cplInUseBlk[blk]) {
      for (var ch = 0; ch < this._nfchans; ++ch) {
        this._channelInCpl[ch] = false;
        this._firstCplCoords[ch] = true;
      }
      this._firstCplLeak = true;
      this._phaseFlagsInUse = false;
      return true;
    }
    if (this._acmod < 2)
      return false;                             // coupling illegal in mono/dual-mono

    if (r.ReadFlag())                            // ecplinu (enhanced coupling)
      throw new NotSupportedException("E-AC-3 enhanced coupling (ecplinu) is not supported.");

    if (this._acmod == 2) {
      this._channelInCpl[0] = true;
      this._channelInCpl[1] = true;
    } else {
      for (var ch = 0; ch < this._nfchans; ++ch)
        this._channelInCpl[ch] = r.ReadFlag();
    }

    if (this._acmod == 2)
      this._phaseFlagsInUse = r.ReadFlag();

    var startSubband = (int)r.ReadBits(4);
    var endSubband = this._spxInUse
      ? (this._spxSrcStartFreq - 37) / 12
      : (int)r.ReadBits(4) + 3;
    if (startSubband >= endSubband)
      return false;
    this._startFreq[CplChannel] = startSubband * 12 + 37;
    this._endFreq[CplChannel] = endSubband * 12 + 37;

    DecodeBandStructure(r, blk, startSubband, endSubband, Ac3EnhancedTables.DefaultCplBandStruct,
      out this._numCplBands, this._cplBandSizes);
    return true;
  }

  private void ParseCouplingCoordinates(Ac3BitReader r, int blk) {
    var cplCoordsExist = false;
    for (var ch = 0; ch < this._nfchans; ++ch) {
      if (this._channelInCpl[ch]) {
        if (this._firstCplCoords[ch] || r.ReadFlag()) {
          this._firstCplCoords[ch] = false;
          cplCoordsExist = true;
          var mstrcplco = (int)r.ReadBits(2);
          for (var bnd = 0; bnd < this._numCplBands; ++bnd) {
            var cplcoexp = (int)r.ReadBits(4);
            var cplcomant = (int)r.ReadBits(4);
            float mant = cplcoexp == 15 ? cplcomant / 16f : (cplcomant + 16) / 32f;
            var scale = (float)Math.Pow(2.0, -(cplcoexp + 3 * mstrcplco));
            this._cplCoords[ch][bnd] = mant * scale * 8f;
          }
        }
      } else {
        this._firstCplCoords[ch] = true;
      }
    }
    if (this._acmod == 2 && cplCoordsExist)
      for (var bnd = 0; bnd < this._numCplBands; ++bnd)
        if (this._phaseFlagsInUse)
          r.SkipBits(1);
  }

  // -- transform coefficients --------------------------------------------------
  private void DecodeTransformCoeffs(Ac3BitReader r, int blk, bool cplInUse, bool[] dithflag) {
    var m = new Ac3Mantissas(r);

    // full-bandwidth channels.
    for (var ch = 0; ch < this._nfchans; ++ch) {
      var end = this._channelInCpl[ch] ? this._startFreq[CplChannel] : this._endFreq[ch];
      var coeff = this._coeffs[ch];
      if (this._channelUsesAht[ch]) {
        if (blk == 0) this.DecodeAht(r, ch, dithflag[ch]);
        for (var bin = 0; bin < end; ++bin)
          coeff[bin] = this._preMantissa[ch][bin][blk] * Exp2Pre(-this._exp[ch][bin]);
      } else {
        for (var bin = 0; bin < end; ++bin)
          coeff[bin] = m.Next(this._bap[ch][bin], dithflag[ch]) * Exp2(-this._exp[ch][bin]);
      }
      for (var bin = end; bin < MaxBins; ++bin)
        coeff[bin] = 0f;
    }

    // coupling channel.
    if (cplInUse) {
      var coeff = this._coeffs[CplChannel];
      Array.Clear(coeff, 0, coeff.Length);
      var s = this._startFreq[CplChannel];
      var e = this._endFreq[CplChannel];
      if (this._channelUsesAht[CplChannel]) {
        if (blk == 0) this.DecodeAht(r, CplChannel, dither: false);
        for (var bin = s; bin < e; ++bin)
          coeff[bin] = this._preMantissa[CplChannel][bin][blk] * Exp2Pre(-this._exp[CplChannel][bin]);
      } else {
        for (var bin = s; bin < e; ++bin)
          coeff[bin] = m.Next(this._bap[CplChannel][bin], dither: false) * Exp2(-this._exp[CplChannel][bin]);
      }
    }

    // LFE.
    if (this._lfeon) {
      var coeff = this._coeffs[LfeChannel];
      Array.Clear(coeff, 0, coeff.Length);
      if (this._channelUsesAht[LfeChannel]) {
        if (blk == 0) this.DecodeAht(r, LfeChannel, dither: false);
        for (var bin = 0; bin < 7; ++bin)
          coeff[bin] = this._preMantissa[LfeChannel][bin][blk] * Exp2Pre(-this._exp[LfeChannel][bin]);
      } else {
        for (var bin = 0; bin < 7; ++bin)
          coeff[bin] = m.Next(this._bap[LfeChannel][bin], dither: false) * Exp2(-this._exp[LfeChannel][bin]);
      }
    }
  }

  // Adaptive Hybrid Transform decode (A/52 Annex E §E.2.3.2, FFmpeg ff_eac3_decode_transform_coeffs_aht_ch).
  private void DecodeAht(Ac3BitReader r, int ch, bool dither) {
    var start = ch == CplChannel ? this._startFreq[CplChannel] : 0;
    var end = ch == CplChannel ? this._endFreq[CplChannel] : this._endFreq[ch];
    var bap = this._bap[ch];

    var gaqMode = (int)r.ReadBits(2);
    var endBap = gaqMode < 2 ? 12 : 17;

    // GAQ gain codes.
    var gaqGain = new int[MaxBins];
    var gs = 0;
    if (gaqMode is 1 or 2) {                     // EAC3_GAQ_12 / EAC3_GAQ_14
      for (var bin = start; bin < end; ++bin)
        if (bap[bin] > 7 && bap[bin] < endBap)
          gaqGain[gs++] = (r.ReadFlag() ? 1 : 0) << (gaqMode - 1);
    } else if (gaqMode == 3) {                   // EAC3_GAQ_124: 3 codes in 5 bits
      var gc = 2;
      for (var bin = start; bin < end; ++bin)
        if (bap[bin] > 7 && bap[bin] < 17) {
          if (gc++ == 2) {
            var group = (int)r.ReadBits(5);
            if (group > 26) group = 26;
            gaqGain[gs++] = Ac3EnhancedTables.Ungroup3In5[group][0];
            gaqGain[gs++] = Ac3EnhancedTables.Ungroup3In5[group][1];
            gaqGain[gs++] = Ac3EnhancedTables.Ungroup3In5[group][2];
            gc = 0;
          }
        }
    }

    gs = 0;
    for (var bin = start; bin < end; ++bin) {
      var hebap = bap[bin];
      var bits = Ac3EnhancedTables.BitsVsHebap[hebap];
      var pm = this._preMantissa[ch][bin];
      if (hebap == 0) {
        for (var blk = 0; blk < 6; ++blk)
          pm[blk] = dither ? NextAhtDither() : 0;
      } else if (hebap < 8) {
        var v = (int)r.ReadBits(bits);
        var vq = Ac3EnhancedTables.MantissaVq[hebap]!;
        for (var blk = 0; blk < 6; ++blk)
          pm[blk] = vq[v][blk] << 8;
      } else {
        var logGain = gaqMode != 0 && hebap < endBap ? gaqGain[gs++] : 0;
        for (var blk = 0; blk < 6; ++blk)
          pm[blk] = Ac3Aht.GaqDequant(r, hebap, logGain);
      }
      Ac3Aht.Idct6(pm);
    }
  }

  private uint _ahtDither = 1;
  private int NextAhtDither() {
    // 23-bit signed dither matching the AHT zero-mantissa path domain.
    var state = this._ahtDither;
    var lsb = state & 1;
    state >>= 1;
    if (lsb != 0) state ^= 0xB400u;
    this._ahtDither = state;
    return (int)(state & 0x7FFFFF) - 0x400000;
  }

  // Pre-mantissas carry a 24-bit fixed-point scale (1<<23 ↔ 1.0 mantissa) plus the idct6 gain;
  // collapse that to the normalized mantissa domain the rest of the pipeline expects.
  private static float Exp2Pre(int e) => (float)Math.Pow(2.0, e) / (1 << 23);

  // -- bit allocation ----------------------------------------------------------
  private void ComputeAllBap(bool cplInUse) {
    var p = new Ac3BitAllocation.AllocParams(this._slowDecay, this._fastDecay, this._slowGain, this._dbPerBit, this._floor);
    for (var ch = 0; ch < this._nfchans; ++ch) {
      var end = this._channelInCpl[ch] ? this._startFreq[CplChannel] : this._endFreq[ch];
      ComputeBapFor(this._exp[ch], this._bap[ch], 0, end, p, this._fastGain[ch], this._snrOffset[ch],
        this._fscod, this._channelUsesAht[ch], false, 0, 0, this._deltas[ch]);
    }
    if (cplInUse)
      ComputeBapFor(this._exp[CplChannel], this._bap[CplChannel], this._startFreq[CplChannel],
        this._endFreq[CplChannel], p, this._fastGain[CplChannel], this._snrOffset[CplChannel], this._fscod,
        this._channelUsesAht[CplChannel], true, this._cplFastLeak, this._cplSlowLeak, this._deltas[CplChannel]);
    if (this._lfeon)
      ComputeBapFor(this._exp[LfeChannel], this._bap[LfeChannel], 0, 7, p, this._fastGain[LfeChannel],
        this._snrOffset[LfeChannel], this._fscod, this._channelUsesAht[LfeChannel], false, 0, 0, this._deltas[LfeChannel]);
  }

  // Wraps Ac3BitAllocation.ComputeBap, remapping the bap address through the AHT hebap table when the
  // channel uses AHT (the masking curve is identical; only the address→pointer LUT differs).
  private static void ComputeBapFor(byte[] exp, byte[] bap, int start, int end, Ac3BitAllocation.AllocParams p,
      int fgain, int snr, int fscod, bool usesAht, bool isCoupling, int cplFast, int cplSlow,
      Ac3BitAllocation.DeltaSegment[]? deltas) {
    if (!usesAht) {
      Ac3BitAllocation.ComputeBap(exp, bap, start, end, p, fgain, snr, fscod, isCoupling, cplFast, cplSlow, deltas);
      return;
    }
    Ac3BitAllocation.ComputeBap(exp, bap, start, end, p, fgain, snr, fscod, isCoupling, cplFast, cplSlow, deltas,
      Ac3EnhancedTables.HebapTab);
  }

  private void ApplyCoupling() {
    var cplCoeff = this._coeffs[CplChannel];
    for (var ch = 0; ch < this._nfchans; ++ch) {
      if (!this._channelInCpl[ch]) continue;
      var coeff = this._coeffs[ch];
      for (var bin = this._startFreq[CplChannel]; bin < this._endFreq[CplChannel]; ++bin) {
        var band = this.CouplingBandOf(bin);
        coeff[bin] = cplCoeff[bin] * this._cplCoords[ch][band];
      }
    }
  }

  private int CouplingBandOf(int bin) {
    var subband = (bin - this._startFreq[CplChannel]) / 12;
    return Math.Min(subband < this._cplSubbandToBand.Length ? this._cplSubbandToBand[subband] : 0, 17);
  }

  private void ApplyRematrix(bool[] rematflg, int numBands) {
    int[] bounds = [13, 25, 37, 61, this._endFreq[0]];
    var c0 = this._coeffs[0];
    var c1 = this._coeffs[1];
    for (var bnd = 0; bnd < numBands && bnd < 4; ++bnd) {
      if (!rematflg[bnd]) continue;
      var s = bounds[bnd];
      var e = Math.Min(bounds[bnd + 1], this._endFreq[0]);
      for (var bin = s; bin < e; ++bin) {
        var sum = c0[bin] + c1[bin];
        var diff = c0[bin] - c1[bin];
        c0[bin] = sum;
        c1[bin] = diff;
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

    var baseOffset = blk * 256 * totalChannels + outChannel;
    for (var n = 0; n < 256; ++n) {
      var v = output[n] * 32768f;
      var s = (int)Math.Round(v);
      pcm[baseOffset + n * totalChannels] = (short)Math.Clamp(s, short.MinValue, short.MaxValue);
    }
  }

  // -- band-structure decode --------------------------------------------------
  private readonly int[] _cplSubbandToBand = new int[18];

  // Reads the per-boundary merge bits (from the bitstream in block 0 / when present, else from the
  // default banding) and resolves them into bands via Ac3EnhancedBandStructure.Decode.
  private Ac3EnhancedBandStructure.Result DecodeBandStructure(
      Ac3BitReader r, int blk, int startSubband, int endSubband, byte[] defaultStruct) {
    var numSubbands = endSubband - startSubband;
    Span<byte> mergeBits = stackalloc byte[22];
    if (blk == 0 || r.ReadFlag()) {
      for (var sb = 0; sb < numSubbands - 1; ++sb)
        mergeBits[sb] = (byte)(r.ReadFlag() ? 1 : 0);
    } else {
      for (var sb = 0; sb < numSubbands - 1; ++sb) {
        var idx = startSubband + 1 + sb;
        mergeBits[sb] = idx < defaultStruct.Length ? defaultStruct[idx] : (byte)0;
      }
    }
    return Ac3EnhancedBandStructure.Decode(numSubbands, mergeBits);
  }

  private void DecodeBandStructure(Ac3BitReader r, int blk, int startSubband, int endSubband,
      byte[] defaultStruct, out int numBands, int[] bandSizes) {
    var result = this.DecodeBandStructure(r, blk, startSubband, endSubband, defaultStruct);
    numBands = result.NumBands;
    Array.Copy(result.BandSizes, bandSizes, Math.Min(result.BandSizes.Length, bandSizes.Length));
    Array.Copy(result.SubbandToBand, this._cplSubbandToBand,
      Math.Min(result.SubbandToBand.Length, this._cplSubbandToBand.Length));
  }

  // SPX variant: only the band count is needed (parse-only reconstruction).
  private void DecodeSpxBandStructure(Ac3BitReader r, int blk, int startSubband, int endSubband, byte[] defaultStruct) {
    this._numSpxBands = this.DecodeBandStructure(r, blk, startSubband, endSubband, defaultStruct).NumBands;
  }

  private static Ac3BitAllocation.DeltaSegment[] ReadDeltaBa(Ac3BitReader r) {
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

  // -- small helpers ----------------------------------------------------------
  private static float Exp2(int e) => (float)Math.Pow(2.0, e);

  private static int Log2(int v) {
    var r = 0;
    while ((v >>= 1) != 0) ++r;
    return r;
  }

  private int LastFbw() => this._nfchans - 1;
  private int NextChannel(int ch) => ch == CplChannel ? 0 : ch + 1;

  private IEnumerable<int> SnrChannelOrder(bool cplInUse) {
    if (cplInUse) yield return CplChannel;
    for (var ch = 0; ch < this._nfchans; ++ch) yield return ch;
    if (this._lfeon) yield return LfeChannel;
  }

  private IEnumerable<int> DbaChannelOrder(bool cplInUse) {
    if (cplInUse) yield return CplChannel;
    for (var ch = 0; ch < this._nfchans; ++ch) yield return ch;
  }

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

  private static Ac3Exponents.Strategy[][] NewStrategyMatrix(int rows, int cols) {
    var m = new Ac3Exponents.Strategy[rows][];
    for (var i = 0; i < rows; ++i) m[i] = new Ac3Exponents.Strategy[cols];
    return m;
  }

  private static int[][][] NewPreMantissa() {
    var m = new int[Slots][][];
    for (var c = 0; c < Slots; ++c) {
      m[c] = new int[MaxBins][];
      for (var b = 0; b < MaxBins; ++b)
        m[c][b] = new int[6];
    }
    return m;
  }
}

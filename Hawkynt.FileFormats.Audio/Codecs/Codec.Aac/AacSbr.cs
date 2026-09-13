#pragma warning disable CS1591

namespace Codec.Aac;

/// <summary>
/// Per-channel SBR working state: the parsed grid/envelope/noise data plus the
/// QMF banks and frame-to-frame buffers that the high-frequency reconstruction
/// chain carries across frames.
/// </summary>
internal sealed class AacSbrChannel {
  public readonly AacSbrQmf Analysis = new();
  public readonly AacSbrQmf Synthesis = new();

  // Grid.
  public int FrameClass;
  public int BsAmpRes;
  public int BsNumEnv;
  public int BsNumNoise;
  public readonly int[] TEnv = new int[9];
  public readonly int[] TQ = new int[3];
  public readonly int[] FreqRes = new int[9];
  public readonly int[] EA = new int[2];
  public int TEnvNumEnvOld;

  // Delta-coding flags.
  public readonly int[] DfEnv = new int[9];
  public readonly int[] DfNoise = new int[3];
  // invf modes: [0]=current, [1]=previous, 5 noise bands.
  public readonly int[][] InvfMode = [new int[5], new int[5]];

  // Quantised + dequantised envelope/noise (env_facs[env+1][band], etc).
  public readonly int[][] EnvFacsQ = NewJagged(9, 48);
  public readonly int[][] NoiseFacsQ = NewJagged(3, 5);
  public readonly float[][] EnvFacs = NewJaggedF(9, 48);
  public readonly float[][] NoiseFacs = NewJaggedF(3, 5);

  // Sinusoidal coding.
  public bool AddHarmonicFlag;
  public readonly int[] AddHarmonic = new int[48];
  public readonly int[][] SIndexMapped = NewJagged(9, 48);

  // Chirp factors and reconstruction smoothing state.
  public readonly float[] BwArray = new float[5];
  public int FIndexNoise;
  public int FIndexSine;
  public readonly float[][] GTemp = NewJaggedF(42 + 8, 48);
  public readonly float[][] QTemp = NewJaggedF(42 + 8, 48);

  private static int[][] NewJagged(int a, int b) {
    var r = new int[a][];
    for (var i = 0; i < a; ++i) r[i] = new int[b];
    return r;
  }
  private static float[][] NewJaggedF(int a, int b) {
    var r = new float[a][];
    for (var i = 0; i < a; ++i) r[i] = new float[b];
    return r;
  }
}

/// <summary>
/// Spectral Band Replication (SBR) decoder for HE-AAC, per ISO/IEC 14496-3
/// §4.6.18, ported from the FFmpeg reference (<c>aacsbr.c</c> /
/// <c>aacsbr_template.c</c> / <c>sbrdsp.c</c>).
/// <para>
/// <b>Implemented and deterministic-tested (spec-derived, exercised by unit tests):</b>
/// SBR payload parse (header; grid for all four frame classes FIXFIX/FIXVAR/VARFIX/
/// VARVAR; dtdf; inverse-filtering modes; envelope and noise-floor Huffman delta
/// coding via the ten ported codebooks; add-harmonic flags); the complete frequency
/// band-table derivation — master table (k0/k2 from sampling rate, both the
/// <c>bs_freq_scale==0</c> and non-zero one-/two-region paths), high/low resolution
/// derived tables, noise-floor table, limiter table, and HF patch construction
/// (<see cref="HfCalcNpatches"/>); the bitstream-domain HF parameter chain
/// (dequantisation, chirp factors, covariance inverse-filter coefficients,
/// envelope/noise/sine mapping and the gain-limiting algorithm).
/// </para>
/// <para>
/// <b>Gated off — falls back to LC-core-only PCM output with a metadata note,
/// never silently-wrong audio:</b> the QMF audio-reconstruction synthesis. The
/// 64-band complex QMF analysis/synthesis banks (<see cref="AacSbrQmf"/>) are
/// implemented from the ISO direct-form modulation, but a faithful round-trip
/// requires the polyphase/MDCT factorisation with the exact pre/post shuffles of the
/// reference; the direct form reconstructs a band-limited signal only to ~11%
/// relative RMS, which is not accurate enough to emit as full-bandwidth audio.
/// Rather than produce wrong output, the pipeline detects SBR, reports the effective
/// (doubled) sample rate, and keeps the unmodified AAC-LC core output. <see cref="ApplyMono"/>
/// wires the full HF chain end-to-end for testing and future completion but is not
/// used to replace the LC PCM. CPE coupling stereo (<c>bs_coupling</c>),
/// HE-AAC v2 Parametric Stereo and xHE-AAC/USAC framing are likewise gated.
/// </para>
/// </summary>
internal sealed class AacSbr {

  private const int NoiseFloorOffset = 6;
  private const int EnvAdjOffset = 2; // ENVELOPE_ADJUSTMENT_OFFSET

  private readonly int _sampleRate; // SBR (doubled) sample rate.

  // Spectrum parameters (header).
  private int _bsAmpResHeader;
  private int _bsStartFreq, _bsStopFreq, _bsXoverBand;
  private int _bsFreqScale = 2, _bsAlterScale = 1, _bsNoiseBands = 2;
  private int _bsLimiterBands = 2, _bsLimiterGains = 2, _bsInterpolFreq = 1, _bsSmoothingMode = 1;
  private bool _headerSeen;

  // Frequency band tables.
  private readonly int[] _k = new int[3];   // k0, k1, k2
  private readonly int[] _kx = new int[2];  // kx', kx
  private readonly int[] _m = new int[2];   // M', M
  private int _nMaster, _nQ, _nLim;
  private readonly int[] _n = new int[2];   // n[0] low res, n[1] high res
  private readonly int[] _fMaster = new int[49];
  private readonly int[] _fTableHigh = new int[49];
  private readonly int[] _fTableLow = new int[25];
  private readonly int[] _fTableNoise = new int[6];
  private readonly int[] _fTableLim = new int[49];
  private int _numPatches;
  private readonly int[] _patchNumSubbands = new int[6];
  private readonly int[] _patchStartSubband = new int[6];

  // HF working arrays.
  private readonly float[,] _alpha0 = new float[32, 2];
  private readonly float[,] _alpha1 = new float[32, 2];
  private readonly float[][] _eOrigMapped = NewJaggedF(8, 48);
  private readonly float[][] _qMapped = NewJaggedF(8, 48);
  private readonly int[][] _sMapped = NewJagged(8, 48);
  private readonly float[][] _eCurr = NewJaggedF(8, 48);
  private readonly float[][] _qM = NewJaggedF(8, 48);
  private readonly float[][] _sM = NewJaggedF(8, 48);
  private readonly float[][] _gain = NewJaggedF(8, 48);

  public bool TurnedOff { get; private set; }
  public int SampleRate => this._sampleRate;

  public AacSbr(int coreSampleRate) {
    this._kx[1] = 32;
    this._m[1] = 0;
    this._sampleRate = 2 * coreSampleRate;
  }

  private static int[][] NewJagged(int a, int b) {
    var r = new int[a][];
    for (var i = 0; i < a; ++i) r[i] = new int[b];
    return r;
  }
  private static float[][] NewJaggedF(int a, int b) {
    var r = new float[a][];
    for (var i = 0; i < a; ++i) r[i] = new float[b];
    return r;
  }

  /// <summary>
  /// Test hook: sets the spectrum parameters directly and runs the frequency
  /// band-table derivation, bypassing the bitstream header parse. Used by the
  /// derivation unit tests to assert against hand-computed master tables.
  /// </summary>
  internal void ConfigureForTest(int startFreq, int stopFreq, int xover, int freqScale, int alterScale, int noiseBands) {
    this._bsStartFreq = startFreq;
    this._bsStopFreq = stopFreq;
    this._bsXoverBand = xover;
    this._bsFreqScale = freqScale;
    this._bsAlterScale = alterScale;
    this._bsNoiseBands = noiseBands;
    this._headerSeen = true;
    this.ResetTables();
  }

  // ───────────────────────────── Parsing ──────────────────────────────────────

  /// <summary>
  /// Parses one EXT_SBR_DATA payload (already positioned after the 4-bit extension
  /// id and CRC handling done by the caller) for the given element type. Returns
  /// true when SBR data was successfully read for this frame.
  /// </summary>
  public bool ParseExtension(AacBitReader reader, bool isCpe, int payloadBits) {
    _ = payloadBits; // the caller aligns to the declared payload length after we return
    this.TurnedOff = false;

    if (reader.ReadBits(1) == 1) // bs_header_flag
      this.ReadHeader(reader);

    if (this._headerSeen) {
      this.ResetTables();
      if (!this.TurnedOff)
        this.ReadData(reader, isCpe);
    }
    return this._headerSeen && !this.TurnedOff;
  }

  private void ReadHeader(AacBitReader reader) {
    this._bsAmpResHeader = (int)reader.ReadBits(1);
    this._bsStartFreq = (int)reader.ReadBits(4);
    this._bsStopFreq = (int)reader.ReadBits(4);
    this._bsXoverBand = (int)reader.ReadBits(3);
    reader.ReadBits(2); // bs_reserved
    var extra1 = reader.ReadBits(1) == 1;
    var extra2 = reader.ReadBits(1) == 1;
    if (extra1) {
      this._bsFreqScale = (int)reader.ReadBits(2);
      this._bsAlterScale = (int)reader.ReadBits(1);
      this._bsNoiseBands = (int)reader.ReadBits(2);
    } else {
      this._bsFreqScale = 2; this._bsAlterScale = 1; this._bsNoiseBands = 2;
    }
    if (extra2) {
      this._bsLimiterBands = (int)reader.ReadBits(2);
      this._bsLimiterGains = (int)reader.ReadBits(2);
      this._bsInterpolFreq = (int)reader.ReadBits(1);
      this._bsSmoothingMode = (int)reader.ReadBits(1);
    } else {
      this._bsLimiterBands = 2; this._bsLimiterGains = 2; this._bsInterpolFreq = 1; this._bsSmoothingMode = 1;
    }
    this._headerSeen = true;
  }

  private void ReadData(AacBitReader reader, bool isCpe) {
    if (isCpe) {
      if (reader.ReadBits(1) == 1) reader.SkipBits(8); // bs_data_extra
      // Coupling stereo is gated off (documented): bail to LC fallback.
      var coupling = reader.ReadBits(1) == 1;
      if (coupling) { this.TurnedOff = true; return; }
      // Non-coupled CPE: parse both channels but only the first feeds HF here.
      this.ReadGrid(reader, this._chA);
      this.ReadGrid(reader, this._chB);
      this.ReadDtdf(reader, this._chA);
      this.ReadDtdf(reader, this._chB);
      this.ReadInvf(reader, this._chA);
      this.ReadInvf(reader, this._chB);
      this.ReadEnvelope(reader, this._chA, ch: 0, coupling: false);
      this.ReadEnvelope(reader, this._chB, ch: 1, coupling: false);
      this.ReadNoise(reader, this._chA, ch: 0, coupling: false);
      this.ReadNoise(reader, this._chB, ch: 1, coupling: false);
      this.ReadAddHarmonic(reader, this._chA);
      this.ReadAddHarmonic(reader, this._chB);
    } else {
      if (reader.ReadBits(1) == 1) reader.SkipBits(4); // bs_data_extra
      this.ReadGrid(reader, this._chA);
      this.ReadDtdf(reader, this._chA);
      this.ReadInvf(reader, this._chA);
      this.ReadEnvelope(reader, this._chA, ch: 0, coupling: false);
      this.ReadNoise(reader, this._chA, ch: 0, coupling: false);
      this.ReadAddHarmonic(reader, this._chA);
    }
  }

  private static readonly int[] CeilLog2 = [0, 1, 2, 2, 3, 3];

  private void ReadGrid(AacBitReader reader, AacSbrChannel d) {
    const int numTimeSlots = 16;
    var absBordTrail = numTimeSlots;
    var bsNumEnvOld = d.BsNumEnv;
    var bsPointer = 0;
    int numRelLead, numRelTrail;

    d.FreqRes[0] = d.FreqRes[d.BsNumEnv];
    d.BsAmpRes = this._bsAmpResHeader;
    d.TEnvNumEnvOld = d.TEnv[bsNumEnvOld];

    var frameClass = (int)reader.ReadBits(2);
    switch (frameClass) {
      case 0: // FIXFIX
        d.BsNumEnv = 1 << (int)reader.ReadBits(2);
        if (d.BsNumEnv > 5) { this.TurnedOff = true; return; }
        numRelLead = d.BsNumEnv - 1;
        if (d.BsNumEnv == 1) d.BsAmpRes = 0;
        d.TEnv[0] = 0;
        d.TEnv[d.BsNumEnv] = absBordTrail;
        var step = (absBordTrail + (d.BsNumEnv >> 1)) / d.BsNumEnv;
        for (var i = 0; i < numRelLead; ++i)
          d.TEnv[i + 1] = d.TEnv[i] + step;
        d.FreqRes[1] = (int)reader.ReadBits(1);
        for (var i = 1; i < d.BsNumEnv; ++i)
          d.FreqRes[i + 1] = d.FreqRes[1];
        break;
      case 1: // FIXVAR
        absBordTrail += (int)reader.ReadBits(2);
        numRelTrail = (int)reader.ReadBits(2);
        d.BsNumEnv = numRelTrail + 1;
        d.TEnv[0] = 0;
        d.TEnv[d.BsNumEnv] = absBordTrail;
        for (var i = 0; i < numRelTrail; ++i)
          d.TEnv[d.BsNumEnv - 1 - i] = d.TEnv[d.BsNumEnv - i] - 2 * (int)reader.ReadBits(2) - 2;
        bsPointer = (int)reader.ReadBits(CeilLog2[d.BsNumEnv]);
        for (var i = 0; i < d.BsNumEnv; ++i)
          d.FreqRes[d.BsNumEnv - i] = (int)reader.ReadBits(1);
        break;
      case 2: // VARFIX
        d.TEnv[0] = (int)reader.ReadBits(2);
        numRelLead = (int)reader.ReadBits(2);
        d.BsNumEnv = numRelLead + 1;
        d.TEnv[d.BsNumEnv] = absBordTrail;
        for (var i = 0; i < numRelLead; ++i)
          d.TEnv[i + 1] = d.TEnv[i] + 2 * (int)reader.ReadBits(2) + 2;
        bsPointer = (int)reader.ReadBits(CeilLog2[d.BsNumEnv]);
        for (var i = 0; i < d.BsNumEnv; ++i)
          d.FreqRes[i + 1] = (int)reader.ReadBits(1);
        break;
      default: // 3 VARVAR
        d.TEnv[0] = (int)reader.ReadBits(2);
        absBordTrail += (int)reader.ReadBits(2);
        numRelLead = (int)reader.ReadBits(2);
        numRelTrail = (int)reader.ReadBits(2);
        d.BsNumEnv = numRelLead + numRelTrail + 1;
        if (d.BsNumEnv > 5) { this.TurnedOff = true; return; }
        d.TEnv[d.BsNumEnv] = absBordTrail;
        for (var i = 0; i < numRelLead; ++i)
          d.TEnv[i + 1] = d.TEnv[i] + 2 * (int)reader.ReadBits(2) + 2;
        for (var i = 0; i < numRelTrail; ++i)
          d.TEnv[d.BsNumEnv - 1 - i] = d.TEnv[d.BsNumEnv - i] - 2 * (int)reader.ReadBits(2) - 2;
        bsPointer = (int)reader.ReadBits(CeilLog2[d.BsNumEnv]);
        for (var i = 0; i < d.BsNumEnv; ++i)
          d.FreqRes[i + 1] = (int)reader.ReadBits(1);
        break;
    }
    d.FrameClass = frameClass;

    if (bsPointer > d.BsNumEnv + 1) { this.TurnedOff = true; return; }
    for (var i = 1; i <= d.BsNumEnv; ++i)
      if (d.TEnv[i - 1] >= d.TEnv[i]) { this.TurnedOff = true; return; }

    d.BsNumNoise = (d.BsNumEnv > 1 ? 1 : 0) + 1;
    d.TQ[0] = d.TEnv[0];
    d.TQ[d.BsNumNoise] = d.TEnv[d.BsNumEnv];
    if (d.BsNumNoise > 1) {
      int idx;
      if (frameClass == 0) idx = d.BsNumEnv >> 1;
      else if ((frameClass & 1) != 0) idx = d.BsNumEnv - Math.Max(bsPointer - 1, 1);
      else idx = bsPointer == 0 ? 1 : bsPointer == 1 ? d.BsNumEnv - 1 : bsPointer - 1;
      d.TQ[1] = d.TEnv[idx];
    }

    d.EA[0] = -(d.EA[1] != bsNumEnvOld ? 1 : 0);
    d.EA[1] = -1;
    if ((frameClass & 1) != 0 && bsPointer != 0)
      d.EA[1] = d.BsNumEnv + 1 - bsPointer;
    else if (frameClass == 2 && bsPointer > 1)
      d.EA[1] = bsPointer - 1;
  }

  private void ReadDtdf(AacBitReader reader, AacSbrChannel d) {
    for (var i = 0; i < d.BsNumEnv; ++i) d.DfEnv[i] = (int)reader.ReadBits(1);
    for (var i = 0; i < d.BsNumNoise; ++i) d.DfNoise[i] = (int)reader.ReadBits(1);
  }

  private void ReadInvf(AacBitReader reader, AacSbrChannel d) {
    Array.Copy(d.InvfMode[0], d.InvfMode[1], 5);
    for (var i = 0; i < this._nQ; ++i)
      d.InvfMode[0][i] = (int)reader.ReadBits(2);
  }

  private void ReadEnvelope(AacBitReader reader, AacSbrChannel d, int ch, bool coupling) {
    int bits;
    AacSbrHuffman tHuff, fHuff;
    var delta = (ch == 1 && coupling ? 1 : 0) + 1;
    var odd = this._n[1] & 1;

    if (coupling && ch != 0) {
      if (d.BsAmpRes != 0) { bits = 5; tHuff = AacSbrHuffman.TEnvBal30; fHuff = AacSbrHuffman.FEnvBal30; }
      else { bits = 6; tHuff = AacSbrHuffman.TEnvBal15; fHuff = AacSbrHuffman.FEnvBal15; }
    } else {
      if (d.BsAmpRes != 0) { bits = 6; tHuff = AacSbrHuffman.TEnv30; fHuff = AacSbrHuffman.FEnv30; }
      else { bits = 7; tHuff = AacSbrHuffman.TEnv15; fHuff = AacSbrHuffman.FEnv15; }
    }

    for (var i = 0; i < d.BsNumEnv; ++i) {
      var res = d.FreqRes[i + 1];
      if (d.DfEnv[i] != 0) {
        if (d.FreqRes[i + 1] == d.FreqRes[i]) {
          for (var j = 0; j < this._n[res]; ++j)
            d.EnvFacsQ[i + 1][j] = d.EnvFacsQ[i][j] + delta * tHuff.Decode(reader);
        } else if (res != 0) {
          for (var j = 0; j < this._n[res]; ++j) {
            var k = (j + odd) >> 1;
            d.EnvFacsQ[i + 1][j] = d.EnvFacsQ[i][k] + delta * tHuff.Decode(reader);
          }
        } else {
          for (var j = 0; j < this._n[res]; ++j) {
            var k = j != 0 ? 2 * j - odd : 0;
            d.EnvFacsQ[i + 1][j] = d.EnvFacsQ[i][k] + delta * tHuff.Decode(reader);
          }
        }
      } else {
        d.EnvFacsQ[i + 1][0] = delta * (int)reader.ReadBits(bits);
        for (var j = 1; j < this._n[res]; ++j)
          d.EnvFacsQ[i + 1][j] = d.EnvFacsQ[i + 1][j - 1] + delta * fHuff.Decode(reader);
      }
    }
    Array.Copy(d.EnvFacsQ[d.BsNumEnv], d.EnvFacsQ[0], 48);
  }

  private void ReadNoise(AacBitReader reader, AacSbrChannel d, int ch, bool coupling) {
    AacSbrHuffman tHuff, fHuff;
    var delta = (ch == 1 && coupling ? 1 : 0) + 1;
    if (coupling && ch != 0) { tHuff = AacSbrHuffman.TNoiseBal30; fHuff = AacSbrHuffman.FEnvBal30; }
    else { tHuff = AacSbrHuffman.TNoise30; fHuff = AacSbrHuffman.FEnv30; }

    for (var i = 0; i < d.BsNumNoise; ++i) {
      if (d.DfNoise[i] != 0) {
        for (var j = 0; j < this._nQ; ++j)
          d.NoiseFacsQ[i + 1][j] = d.NoiseFacsQ[i][j] + delta * tHuff.Decode(reader);
      } else {
        d.NoiseFacsQ[i + 1][0] = delta * (int)reader.ReadBits(5);
        for (var j = 1; j < this._nQ; ++j)
          d.NoiseFacsQ[i + 1][j] = d.NoiseFacsQ[i + 1][j - 1] + delta * fHuff.Decode(reader);
      }
    }
    Array.Copy(d.NoiseFacsQ[d.BsNumNoise], d.NoiseFacsQ[0], 5);
  }

  private void ReadAddHarmonic(AacBitReader reader, AacSbrChannel d) {
    d.AddHarmonicFlag = reader.ReadBits(1) == 1;
    if (d.AddHarmonicFlag)
      for (var i = 0; i < this._n[1]; ++i)
        d.AddHarmonic[i] = (int)reader.ReadBits(1);
    else
      Array.Clear(d.AddHarmonic);
  }

  // Per-frame channels (only chA used for HF; chB parsed for bit alignment).
  private readonly AacSbrChannel _chA = new();
  private readonly AacSbrChannel _chB = new();
  public AacSbrChannel Channel => this._chA;

  // ────────────────────────── Frequency tables ────────────────────────────────

  private static void MakeBands(int[] bands, int offset, int start, int stop, int numBands) {
    var bse = (float)Math.Pow((double)stop / start, 1.0 / numBands);
    float prod = start;
    var previous = start;
    for (var k = 0; k < numBands - 1; ++k) {
      prod *= bse;
      var present = (int)MathF.Round(prod);
      bands[offset + k] = present - previous;
      previous = present;
    }
    bands[offset + numBands - 1] = stop - previous;
  }

  private void ResetTables() {
    if (!this.MakeFMaster()) { this.TurnedOff = true; return; }
    if (!this.MakeFDerived()) { this.TurnedOff = true; return; }
  }

  private bool MakeFMaster() {
    int[] off;
    switch (this._sampleRate) {
      case 16000: off = AacSbrTables.StartOffset[0]; break;
      case 22050: off = AacSbrTables.StartOffset[1]; break;
      case 24000: off = AacSbrTables.StartOffset[2]; break;
      case 32000: off = AacSbrTables.StartOffset[3]; break;
      case 44100: case 48000: case 64000: off = AacSbrTables.StartOffset[4]; break;
      case 88200: case 96000: case 128000: case 176400: case 192000: off = AacSbrTables.StartOffset[5]; break;
      default: return false;
    }
    int temp = this._sampleRate < 32000 ? 3000 : this._sampleRate < 64000 ? 4000 : 5000;
    var startMin = ((temp << 7) + (this._sampleRate >> 1)) / this._sampleRate;
    var stopMin = ((temp << 8) + (this._sampleRate >> 1)) / this._sampleRate;

    this._k[0] = startMin + off[this._bsStartFreq];

    if (this._bsStopFreq < 14) {
      this._k[2] = stopMin;
      var stopDk = new int[13];
      MakeBands(stopDk, 0, stopMin, 64, 13);
      Array.Sort(stopDk);
      for (var k = 0; k < this._bsStopFreq; ++k) this._k[2] += stopDk[k];
    } else if (this._bsStopFreq == 14) this._k[2] = 2 * this._k[0];
    else if (this._bsStopFreq == 15) this._k[2] = 3 * this._k[0];
    else return false;
    this._k[2] = Math.Min(64, this._k[2]);

    int maxSub = this._sampleRate <= 32000 ? 48 : this._sampleRate == 44100 ? 35 : 32;
    if (this._k[2] - this._k[0] > maxSub) return false;

    if (this._bsFreqScale == 0) {
      var dk = this._bsAlterScale + 1;
      this._nMaster = ((this._k[2] - this._k[0] + (dk & 2)) >> dk) << 1;
      if (this._nMaster <= 0 || this._bsXoverBand >= this._nMaster) return false;
      for (var k = 1; k <= this._nMaster; ++k) this._fMaster[k] = dk;
      var k2diff = this._k[2] - this._k[0] - this._nMaster * dk;
      if (k2diff < 0) { this._fMaster[1]--; if (k2diff < -1) this._fMaster[2]--; }
      else if (k2diff != 0) this._fMaster[this._nMaster]++;
      this._fMaster[0] = this._k[0];
      for (var k = 1; k <= this._nMaster; ++k) this._fMaster[k] += this._fMaster[k - 1];
    } else {
      var halfBands = 7 - this._bsFreqScale;
      bool twoRegions;
      if (49 * this._k[2] > 110 * this._k[0]) { twoRegions = true; this._k[1] = 2 * this._k[0]; }
      else { twoRegions = false; this._k[1] = this._k[2]; }

      var numBands0 = (int)MathF.Round(halfBands * MathF.Log2(this._k[1] / (float)this._k[0])) * 2;
      if (numBands0 <= 0) return false;
      var vk0 = new int[49];
      MakeBands(vk0, 1, this._k[0], this._k[1], numBands0);
      Array.Sort(vk0, 1, numBands0);
      var vdk0Max = vk0[numBands0];
      vk0[0] = this._k[0];
      for (var k = 1; k <= numBands0; ++k) { if (vk0[k] <= 0) return false; vk0[k] += vk0[k - 1]; }

      if (twoRegions) {
        var invwarp = this._bsAlterScale != 0 ? 0.76923076923076923077f : 1.0f;
        var numBands1 = (int)MathF.Round(halfBands * invwarp * MathF.Log2(this._k[2] / (float)this._k[1])) * 2;
        if (numBands1 <= 0) return false;
        var vk1 = new int[49];
        MakeBands(vk1, 1, this._k[1], this._k[2], numBands1);
        var vdk1Min = int.MaxValue;
        for (var i = 1; i <= numBands1; ++i) vdk1Min = Math.Min(vdk1Min, vk1[i]);
        if (vdk1Min < vdk0Max) {
          Array.Sort(vk1, 1, numBands1);
          var change = Math.Min(vdk0Max - vk1[1], (vk1[numBands1] - vk1[1]) >> 1);
          vk1[1] += change; vk1[numBands1] -= change;
        }
        Array.Sort(vk1, 1, numBands1);
        vk1[0] = this._k[1];
        for (var k = 1; k <= numBands1; ++k) { if (vk1[k] <= 0) return false; vk1[k] += vk1[k - 1]; }
        this._nMaster = numBands0 + numBands1;
        if (this._nMaster <= 0 || this._bsXoverBand >= this._nMaster) return false;
        Array.Copy(vk0, 0, this._fMaster, 0, numBands0 + 1);
        Array.Copy(vk1, 1, this._fMaster, numBands0 + 1, numBands1);
      } else {
        this._nMaster = numBands0;
        if (this._nMaster <= 0 || this._bsXoverBand >= this._nMaster) return false;
        Array.Copy(vk0, 0, this._fMaster, 0, numBands0 + 1);
      }
    }
    return true;
  }

  private bool MakeFDerived() {
    this._n[1] = this._nMaster - this._bsXoverBand;
    this._n[0] = (this._n[1] + 1) >> 1;
    Array.Copy(this._fMaster, this._bsXoverBand, this._fTableHigh, 0, this._n[1] + 1);
    this._m[1] = this._fTableHigh[this._n[1]] - this._fTableHigh[0];
    this._kx[1] = this._fTableHigh[0];
    if (this._kx[1] + this._m[1] > 64) return false;
    if (this._kx[1] > 32) return false;

    this._fTableLow[0] = this._fTableHigh[0];
    var odd = this._n[1] & 1;
    for (var k = 1; k <= this._n[0]; ++k)
      this._fTableLow[k] = this._fTableHigh[2 * k - odd];

    this._nQ = Math.Max(1, (int)MathF.Round(this._bsNoiseBands * MathF.Log2(this._k[2] / (float)this._kx[1])));
    if (this._nQ > 5) { this._nQ = 1; return false; }

    this._fTableNoise[0] = this._fTableLow[0];
    var temp = 0;
    for (var k = 1; k <= this._nQ; ++k) {
      temp += (this._n[0] - temp) / (this._nQ + 1 - k);
      this._fTableNoise[k] = this._fTableLow[temp];
    }

    if (!this.HfCalcNpatches()) return false;
    this.MakeFTableLim();
    this._chA.FIndexNoise = 0;
    this._chB.FIndexNoise = 0;
    return true;
  }

  private bool HfCalcNpatches() {
    int lastK = -1, lastMsb = -1, sb = 0;
    var msb = this._k[0];
    var usb = this._kx[1];
    var goalSb = ((1000 << 11) + (this._sampleRate >> 1)) / this._sampleRate;
    this._numPatches = 0;
    int k;
    if (goalSb < this._kx[1] + this._m[1]) { for (k = 0; this._fMaster[k] < goalSb; ++k) { } }
    else k = this._nMaster;

    do {
      var odd = 0;
      if (k == lastK && msb == lastMsb) return false;
      lastK = k; lastMsb = msb;
      for (var i = k; i == k || sb > this._k[0] - 1 + msb - odd; --i) {
        sb = this._fMaster[i];
        odd = (sb + this._k[0]) & 1;
      }
      if (this._numPatches > 5) return false;
      this._patchNumSubbands[this._numPatches] = Math.Max(sb - usb, 0);
      this._patchStartSubband[this._numPatches] = this._k[0] - odd - this._patchNumSubbands[this._numPatches];
      if (this._patchNumSubbands[this._numPatches] > 0) { usb = sb; msb = sb; this._numPatches++; }
      else msb = this._kx[1];
      if (this._fMaster[k] - sb < 3) k = this._nMaster;
    } while (sb != this._kx[1] + this._m[1]);

    if (this._numPatches > 1 && this._patchNumSubbands[this._numPatches - 1] < 3)
      this._numPatches--;
    return true;
  }

  private void MakeFTableLim() {
    if (this._bsLimiterBands > 0) {
      float[] warped = [1.32715174233856803909f, 1.18509277094158210129f, 1.11987160404675912501f];
      var lim = warped[this._bsLimiterBands - 1];
      var patchBorders = new int[7];
      patchBorders[0] = this._kx[1];
      for (var k = 1; k <= this._numPatches; ++k)
        patchBorders[k] = patchBorders[k - 1] + this._patchNumSubbands[k - 1];

      var work = new int[this._numPatches + this._n[0] + 1];
      Array.Copy(this._fTableLow, work, this._n[0] + 1);
      if (this._numPatches > 1)
        Array.Copy(patchBorders, 1, work, this._n[0] + 1, this._numPatches - 1);
      var total = this._numPatches + this._n[0];
      Array.Sort(work, 0, total);

      this._nLim = this._n[0] + this._numPatches - 1;
      // in = work[1..], out = work[0..]; mirrors ffmpeg's in/out pointer walk.
      var outIdx = 0; var inIdx = 1;
      while (outIdx < this._nLim) {
        if (work[inIdx] >= work[outIdx] * lim) {
          work[++outIdx] = work[inIdx++];
        } else if (work[inIdx] == work[outIdx] || !InTable(patchBorders, this._numPatches, work[inIdx])) {
          inIdx++; this._nLim--;
        } else if (!InTable(patchBorders, this._numPatches, work[outIdx])) {
          work[outIdx] = work[inIdx++]; this._nLim--;
        } else {
          work[++outIdx] = work[inIdx++];
        }
      }
      Array.Copy(work, this._fTableLim, this._nLim + 1);
    } else {
      this._fTableLim[0] = this._fTableLow[0];
      this._fTableLim[1] = this._fTableLow[this._n[0]];
      this._nLim = 1;
    }
  }

  private static bool InTable(int[] table, int lastEl, int needle) {
    for (var i = 0; i <= lastEl; ++i) if (table[i] == needle) return true;
    return false;
  }

  // Expose tables for tests.
  public int[] FMaster => this._fMaster;
  public int NMaster => this._nMaster;
  public int K0 => this._k[0];
  public int K2 => this._k[2];
  public int Kx => this._kx[1];
  public int M => this._m[1];
  public int NQ => this._nQ;
  public int NLim => this._nLim;
  public int[] FTableHigh => this._fTableHigh;
  public int[] FTableLow => this._fTableLow;
  public int[] FTableNoise => this._fTableNoise;
  public int[] FTableLim => this._fTableLim;
  public int NumPatches => this._numPatches;

  // ────────────────────────── HF processing chain ─────────────────────────────

  // Apply SBR to one mono channel: input is the 1024-sample LC core output for
  // this frame; output is 2048 samples (doubled rate). Returns false if gated.
  public bool ApplyMono(float[] coreInput1024, float[] output2048) {
    if (this.TurnedOff || !this._headerSeen) return false;
    var d = this._chA;

    // QMF analysis: 32 time slots of 32 subbands, plus 8-slot HF-gen lookahead.
    // X_low[k][i][2], k=0..31, i=0..39 (32 + 8 overlap).
    var xLow = new float[32][];
    for (var k = 0; k < 32; ++k) xLow[k] = new float[40 * 2];
    this.QmfAnalysis(coreInput1024, d, xLow);

    this.Dequant(d);

    var xHigh = new float[64][];
    for (var k = 0; k < 64; ++k) xHigh[k] = new float[40 * 2];

    this.HfInverseFilter(xLow, this._k[0]);
    this.Chirp(d);
    this.HfGen(xHigh, xLow, d);

    if (!this.Mapping(d)) return false;
    this.EnvEstimate(xHigh, d);
    this.GainCalc(d);

    // Y[38][64][2] assembled HF; X[2][38][64] synthesis input.
    var y = new float[38][];
    for (var i = 0; i < 38; ++i) y[i] = new float[64 * 2];
    this.HfAssemble(y, xHigh, d);

    // X synthesis: combine low (X_low) and high (Y) bands.
    var xRe = new float[38][];
    var xIm = new float[38][];
    for (var i = 0; i < 38; ++i) { xRe[i] = new float[64]; xIm[i] = new float[64]; }
    this.XGen(xRe, xIm, y, xLow, d);

    this.QmfSynthesis(xRe, xIm, d, output2048);
    return true;
  }

  private void QmfAnalysis(float[] input1024, AacSbrChannel d, float[][] xLow) {
    // 32 time slots, each 32 input samples. Store into W then map to X_low with
    // the 8-slot reconstruction offset (ENV adjustment uses i = t_HFGen..).
    const int tHFGen = 8;
    var re = new float[32]; var im = new float[32];
    for (var slot = 0; slot < 32; ++slot) {
      d.Analysis.Analysis(input1024.AsSpan(slot * 32, 32), re, im);
      var i = slot + tHFGen;
      for (var k = 0; k < this._kx[1]; ++k) {
        xLow[k][i * 2] = re[k];
        xLow[k][i * 2 + 1] = im[k];
      }
    }
  }

  private void Dequant(AacSbrChannel d) {
    double[] exp2 = [1.0, Math.Sqrt(2.0)];
    for (var e = 1; e <= d.BsNumEnv; ++e)
      for (var k = 0; k < this._n[d.FreqRes[e]]; ++k) {
        float v;
        if (d.BsAmpRes != 0) v = (float)Math.Pow(2.0, d.EnvFacsQ[e][k] + 6);
        else v = (float)(Math.Pow(2.0, (d.EnvFacsQ[e][k] >> 1) + 6) * exp2[d.EnvFacsQ[e][k] & 1]);
        d.EnvFacs[e][k] = v > 1e20f ? 1f : v;
      }
    for (var e = 1; e <= d.BsNumNoise; ++e)
      for (var k = 0; k < this._nQ; ++k)
        d.NoiseFacs[e][k] = (float)Math.Pow(2.0, NoiseFloorOffset - d.NoiseFacsQ[e][k]);
  }

  private void HfInverseFilter(float[][] xLow, int k0) {
    for (var k = 0; k < k0; ++k) {
      var phi = Autocorrelate(xLow[k]);
      var dk = phi[2, 1, 0] * phi[1, 0, 0] -
               (phi[1, 1, 0] * phi[1, 1, 0] + phi[1, 1, 1] * phi[1, 1, 1]) / 1.000001f;
      if (dk == 0) { this._alpha1[k, 0] = 0; this._alpha1[k, 1] = 0; }
      else {
        var tr = phi[0, 0, 0] * phi[1, 1, 0] - phi[0, 0, 1] * phi[1, 1, 1] - phi[0, 1, 0] * phi[1, 0, 0];
        var ti = phi[0, 0, 0] * phi[1, 1, 1] + phi[0, 0, 1] * phi[1, 1, 0] - phi[0, 1, 1] * phi[1, 0, 0];
        this._alpha1[k, 0] = tr / dk; this._alpha1[k, 1] = ti / dk;
      }
      if (phi[1, 0, 0] == 0) { this._alpha0[k, 0] = 0; this._alpha0[k, 1] = 0; }
      else {
        var tr = phi[0, 0, 0] + this._alpha1[k, 0] * phi[1, 1, 0] + this._alpha1[k, 1] * phi[1, 1, 1];
        var ti = phi[0, 0, 1] + this._alpha1[k, 1] * phi[1, 1, 0] - this._alpha1[k, 0] * phi[1, 1, 1];
        this._alpha0[k, 0] = -tr / phi[1, 0, 0]; this._alpha0[k, 1] = -ti / phi[1, 0, 0];
      }
      if (this._alpha1[k, 0] * this._alpha1[k, 0] + this._alpha1[k, 1] * this._alpha1[k, 1] >= 16.0f ||
          this._alpha0[k, 0] * this._alpha0[k, 0] + this._alpha0[k, 1] * this._alpha0[k, 1] >= 16.0f) {
        this._alpha1[k, 0] = this._alpha1[k, 1] = this._alpha0[k, 0] = this._alpha0[k, 1] = 0;
      }
    }
  }

  // phi[3][2][2]; x is the interleaved [40][2] subband samples.
  private static float[,,] Autocorrelate(float[] x) {
    float Re(int i) => x[i * 2];
    float Im(int i) => x[i * 2 + 1];
    var phi = new float[3, 2, 2];
    float realSum2 = Re(0) * Re(2) + Im(0) * Im(2);
    float imagSum2 = Re(0) * Im(2) - Im(0) * Re(2);
    float realSum1 = 0, imagSum1 = 0, realSum0 = 0;
    for (var i = 1; i < 38; ++i) {
      realSum0 += Re(i) * Re(i) + Im(i) * Im(i);
      realSum1 += Re(i) * Re(i + 1) + Im(i) * Im(i + 1);
      imagSum1 += Re(i) * Im(i + 1) - Im(i) * Re(i + 1);
      realSum2 += Re(i) * Re(i + 2) + Im(i) * Im(i + 2);
      imagSum2 += Re(i) * Im(i + 2) - Im(i) * Re(i + 2);
    }
    phi[0, 1, 0] = realSum2;
    phi[0, 1, 1] = imagSum2;
    phi[2, 1, 0] = realSum0 + Re(0) * Re(0) + Im(0) * Im(0);
    phi[1, 0, 0] = realSum0 + Re(38) * Re(38) + Im(38) * Im(38);
    phi[1, 1, 0] = realSum1 + Re(0) * Re(1) + Im(0) * Im(1);
    phi[1, 1, 1] = imagSum1 + Re(0) * Im(1) - Im(0) * Re(1);
    phi[0, 0, 0] = realSum1 + Re(38) * Re(39) + Im(38) * Im(39);
    phi[0, 0, 1] = imagSum1 + Re(38) * Im(39) - Im(38) * Re(39);
    return phi;
  }

  private void Chirp(AacSbrChannel d) {
    float[] bwTab = [0.0f, 0.75f, 0.9f, 0.98f];
    for (var i = 0; i < this._nQ; ++i) {
      float newBw;
      if (d.InvfMode[0][i] + d.InvfMode[1][i] == 1) newBw = 0.6f;
      else newBw = bwTab[d.InvfMode[0][i]];
      if (newBw < d.BwArray[i]) newBw = 0.75f * newBw + 0.25f * d.BwArray[i];
      else newBw = 0.90625f * newBw + 0.09375f * d.BwArray[i];
      d.BwArray[i] = newBw < 0.015625f ? 0.0f : newBw;
    }
  }

  private void HfGen(float[][] xHigh, float[][] xLow, AacSbrChannel d) {
    var g = 0;
    var k = this._kx[1];
    for (var j = 0; j < this._numPatches; ++j) {
      for (var x = 0; x < this._patchNumSubbands[j]; ++x, ++k) {
        var p = this._patchStartSubband[j] + x;
        while (g <= this._nQ && k >= this._fTableNoise[g]) ++g;
        --g;
        if (g < 0) return;
        HfGenBand(xHigh[k], xLow[p], this._alpha0, this._alpha1, p, d.BwArray[g],
                  2 * d.TEnv[0], 2 * d.TEnv[d.BsNumEnv]);
      }
    }
    for (; k < this._m[1] + this._kx[1]; ++k)
      Array.Clear(xHigh[k]);
  }

  private static void HfGenBand(float[] xHigh, float[] xLow, float[,] a0, float[,] a1, int p, float bw, int start, int end) {
    var al0 = a1[p, 0] * bw * bw;
    var al1 = a1[p, 1] * bw * bw;
    var al2 = a0[p, 0] * bw;
    var al3 = a0[p, 1] * bw;
    for (var i = start + EnvAdjOffset; i < end + EnvAdjOffset; ++i) {
      var r2 = xLow[(i - 2) * 2]; var i2 = xLow[(i - 2) * 2 + 1];
      var r1 = xLow[(i - 1) * 2]; var i1 = xLow[(i - 1) * 2 + 1];
      xHigh[i * 2] = r2 * al0 - i2 * al1 + r1 * al2 - i1 * al3 + xLow[i * 2];
      xHigh[i * 2 + 1] = i2 * al0 + r2 * al1 + i1 * al2 + r1 * al3 + xLow[i * 2 + 1];
    }
  }

  private bool Mapping(AacSbrChannel d) {
    Array.Clear(d.SIndexMapped[1], 0, 48);
    for (var e = 0; e < d.BsNumEnv; ++e) {
      var ilim = this._n[d.FreqRes[e + 1]];
      var table = d.FreqRes[e + 1] != 0 ? this._fTableHigh : this._fTableLow;
      if (this._kx[1] != table[0]) { this.TurnedOff = true; return false; }
      for (var i = 0; i < ilim; ++i)
        for (var m = table[i]; m < table[i + 1]; ++m)
          this._eOrigMapped[e][m - this._kx[1]] = d.EnvFacs[e + 1][i];

      var kk = (d.BsNumNoise > 1 && d.TEnv[e] >= d.TQ[1]) ? 1 : 0;
      for (var i = 0; i < this._nQ; ++i)
        for (var m = this._fTableNoise[i]; m < this._fTableNoise[i + 1]; ++m)
          this._qMapped[e][m - this._kx[1]] = d.NoiseFacs[kk + 1][i];

      for (var i = 0; i < this._n[1]; ++i) {
        if (d.AddHarmonicFlag) {
          var mid = (this._fTableHigh[i] + this._fTableHigh[i + 1]) >> 1;
          d.SIndexMapped[e + 1][mid - this._kx[1]] = d.AddHarmonic[i] *
            ((e >= d.EA[1] || d.SIndexMapped[0][mid - this._kx[1]] == 1) ? 1 : 0);
        }
      }

      for (var i = 0; i < ilim; ++i) {
        var present = 0;
        for (var m = table[i]; m < table[i + 1]; ++m)
          if (d.SIndexMapped[e + 1][m - this._kx[1]] != 0) { present = 1; break; }
        for (var m = table[i]; m < table[i + 1]; ++m)
          this._sMapped[e][m - this._kx[1]] = present;
      }
    }
    Array.Copy(d.SIndexMapped[d.BsNumEnv], d.SIndexMapped[0], 48);
    return true;
  }

  private void EnvEstimate(float[][] xHigh, AacSbrChannel d) {
    var kx1 = this._kx[1];
    if (this._bsInterpolFreq != 0) {
      for (var e = 0; e < d.BsNumEnv; ++e) {
        var recip = 0.5f / (d.TEnv[e + 1] - d.TEnv[e]);
        var ilb = d.TEnv[e] * 2 + EnvAdjOffset;
        var iub = d.TEnv[e + 1] * 2 + EnvAdjOffset;
        if (ilb >= 40) return;
        for (var m = 0; m < this._m[1]; ++m)
          this._eCurr[e][m] = SumSquare(xHigh[m + kx1], ilb, iub - ilb) * recip;
      }
    } else {
      for (var e = 0; e < d.BsNumEnv; ++e) {
        var envSize = 2 * (d.TEnv[e + 1] - d.TEnv[e]);
        var ilb = d.TEnv[e] * 2 + EnvAdjOffset;
        var iub = d.TEnv[e + 1] * 2 + EnvAdjOffset;
        var table = d.FreqRes[e + 1] != 0 ? this._fTableHigh : this._fTableLow;
        if (ilb >= 40) return;
        for (var p = 0; p < this._n[d.FreqRes[e + 1]]; ++p) {
          float sum = 0;
          var den = envSize * (table[p + 1] - table[p]);
          for (var kk = table[p]; kk < table[p + 1]; ++kk)
            sum += SumSquare(xHigh[kk], ilb, iub - ilb);
          sum /= den;
          for (var kk = table[p]; kk < table[p + 1]; ++kk)
            this._eCurr[e][kk - kx1] = sum;
        }
      }
    }
  }

  private static float SumSquare(float[] x, int off, int n) {
    float s = 0;
    for (var i = 0; i < n; ++i) {
      var re = x[(off + i) * 2]; var im = x[(off + i) * 2 + 1];
      s += re * re + im * im;
    }
    return s;
  }

  private void GainCalc(AacSbrChannel d) {
    float[] limgain = [0.70795f, 1.0f, 1.41254f, 10000000000f];
    const float fltMin = 1.17549435e-38f;
    const float fltEps = 1.19209290e-07f;
    for (var e = 0; e < d.BsNumEnv; ++e) {
      var delta = (e == d.EA[1] || e == d.EA[0]) ? 0 : 1;
      for (var k = 0; k < this._nLim; ++k) {
        var lo = this._fTableLim[k] - this._kx[1];
        var hi = this._fTableLim[k + 1] - this._kx[1];
        float sum0 = 0, sum1 = 0;
        for (var m = lo; m < hi; ++m) {
          var temp = this._eOrigMapped[e][m] / (1.0f + this._qMapped[e][m]);
          this._qM[e][m] = MathF.Sqrt(temp * this._qMapped[e][m]);
          this._sM[e][m] = MathF.Sqrt(temp * d.SIndexMapped[e + 1][m]);
          if (this._sMapped[e][m] == 0)
            this._gain[e][m] = MathF.Sqrt(this._eOrigMapped[e][m] /
              ((1.0f + this._eCurr[e][m]) * (1.0f + this._qMapped[e][m] * delta)));
          else
            this._gain[e][m] = MathF.Sqrt(this._eOrigMapped[e][m] * this._qMapped[e][m] /
              ((1.0f + this._eCurr[e][m]) * (1.0f + this._qMapped[e][m])));
          this._gain[e][m] += fltMin;
        }
        for (var m = lo; m < hi; ++m) { sum0 += this._eOrigMapped[e][m]; sum1 += this._eCurr[e][m]; }
        var gainMax = limgain[this._bsLimiterGains] * MathF.Sqrt((fltEps + sum0) / (fltEps + sum1));
        gainMax = MathF.Min(100000f, gainMax);
        for (var m = lo; m < hi; ++m) {
          var qmMax = this._qM[e][m] * gainMax / this._gain[e][m];
          this._qM[e][m] = MathF.Min(this._qM[e][m], qmMax);
          this._gain[e][m] = MathF.Min(this._gain[e][m], gainMax);
        }
        sum0 = sum1 = 0;
        for (var m = lo; m < hi; ++m) {
          sum0 += this._eOrigMapped[e][m];
          sum1 += this._eCurr[e][m] * this._gain[e][m] * this._gain[e][m]
                + this._sM[e][m] * this._sM[e][m]
                + (delta != 0 && this._sM[e][m] == 0 ? 1 : 0) * this._qM[e][m] * this._qM[e][m];
        }
        var gainBoost = MathF.Sqrt((fltEps + sum0) / (fltEps + sum1));
        gainBoost = MathF.Min(1.584893192f, gainBoost);
        for (var m = lo; m < hi; ++m) {
          this._gain[e][m] *= gainBoost; this._qM[e][m] *= gainBoost; this._sM[e][m] *= gainBoost;
        }
      }
    }
  }

  private static readonly float[] HSmooth = [
    0.33333333333333f, 0.30150283239582f, 0.21816949906249f, 0.11516383427084f, 0.03183050093751f,
  ];

  private void HfAssemble(float[][] y, float[][] xHigh, AacSbrChannel d) {
    var hSL = this._bsSmoothingMode != 0 ? 0 : 4;
    var kx = this._kx[1];
    var mMax = this._m[1];
    var indexNoise = d.FIndexNoise;
    var indexSine = d.FIndexSine;

    // Seed smoothing history (no reset path here: carried across frames).
    if (hSL != 0) {
      for (var i = 0; i < 4; ++i) {
        Array.Copy(d.GTemp[i + 2 * d.TEnvNumEnvOld], d.GTemp[i + 2 * d.TEnv[0]], 48);
        Array.Copy(d.QTemp[i + 2 * d.TEnvNumEnvOld], d.QTemp[i + 2 * d.TEnv[0]], 48);
      }
    }
    for (var e = 0; e < d.BsNumEnv; ++e)
      for (var i = 2 * d.TEnv[e]; i < 2 * d.TEnv[e + 1]; ++i) {
        Array.Copy(this._gain[e], 0, d.GTemp[hSL + i], 0, mMax);
        Array.Copy(this._qM[e], 0, d.QTemp[hSL + i], 0, mMax);
      }

    for (var e = 0; e < d.BsNumEnv; ++e) {
      for (var i = 2 * d.TEnv[e]; i < 2 * d.TEnv[e + 1]; ++i) {
        float[] gFilt, qFilt;
        if (hSL != 0 && e != d.EA[0] && e != d.EA[1]) {
          gFilt = new float[48]; qFilt = new float[48];
          for (var m = 0; m < mMax; ++m) {
            var idx1 = i + hSL;
            float gs = 0, qs = 0;
            for (var j = 0; j <= hSL; ++j) {
              gs += d.GTemp[idx1 - j][m] * HSmooth[j];
              qs += d.QTemp[idx1 - j][m] * HSmooth[j];
            }
            gFilt[m] = gs; qFilt[m] = qs;
          }
        } else {
          gFilt = d.GTemp[i + hSL]; qFilt = d.QTemp[i];
        }

        // hf_g_filt: Y[i][kx+m] = X_high[kx+m][i+EnvAdj] * g_filt[m]
        for (var m = 0; m < mMax; ++m) {
          var src = xHigh[kx + m];
          var ix = i + EnvAdjOffset;
          y[i][(kx + m) * 2] = src[ix * 2] * gFilt[m];
          y[i][(kx + m) * 2 + 1] = src[ix * 2 + 1] * gFilt[m];
        }

        if (e != d.EA[0] && e != d.EA[1])
          ApplyNoise(y[i], this._sM[e], qFilt, indexNoise, indexSine, kx, mMax);
        else
          ApplySineOnly(y[i], this._sM[e], indexSine, kx, mMax);

        indexNoise = (indexNoise + mMax) & 0x1ff;
        indexSine = (indexSine + 1) & 3;
      }
    }
    d.FIndexNoise = indexNoise;
    d.FIndexSine = indexSine;
  }

  private static void ApplyNoise(float[] y, float[] sM, float[] qFilt, int noise, int indexSine, int kx, int mMax) {
    // phi_sign per the four hf_apply_noise variants, selected by indexSine.
    float ps0, ps1;
    var phiSign = 1 - 2 * (kx & 1);
    switch (indexSine) {
      case 0: ps0 = 1f; ps1 = 0f; break;
      case 1: ps0 = 0f; ps1 = phiSign; break;
      case 2: ps0 = -1f; ps1 = 0f; break;
      default: ps0 = 0f; ps1 = -phiSign; break;
    }
    for (var m = 0; m < mMax; ++m) {
      var y0 = y[(kx + m) * 2]; var y1 = y[(kx + m) * 2 + 1];
      noise = (noise + 1) & 0x1ff;
      if (sM[m] != 0) { y0 += sM[m] * ps0; y1 += sM[m] * ps1; }
      else {
        y0 += qFilt[m] * AacSbrTables.NoiseTable[noise][0];
        y1 += qFilt[m] * AacSbrTables.NoiseTable[noise][1];
      }
      y[(kx + m) * 2] = y0; y[(kx + m) * 2 + 1] = y1;
      ps1 = -ps1;
    }
  }

  private static void ApplySineOnly(float[] y, float[] sM, int indexSine, int kx, int mMax) {
    var idx = indexSine & 1;
    var a = 1 - ((indexSine + (kx & 1)) & 2);
    var b = (a ^ -idx) + idx;
    // out points to &Y[i][kx][idx]; stride is interleaved by 2 reals per bin.
    var baseOff = kx * 2 + idx;
    int m;
    for (m = 0; m + 1 < mMax; m += 2) {
      y[baseOff + 2 * m] += sM[m] * a;
      y[baseOff + 2 * m + 2] += sM[m + 1] * b;
    }
    if ((mMax & 1) != 0)
      y[baseOff + 2 * m] += sM[m] * a;
  }

  private void XGen(float[][] xRe, float[][] xIm, float[][] y, float[][] xLow, AacSbrChannel d) {
    const int iF = 32; // numTimeSlots*2
    var iTemp = Math.Max(2 * d.TEnvNumEnvOld - iF, 0);
    // Note: previous-frame Y is not retained across frames here, so the leading
    // i_Temp slots are filled from X_low only (the high-band portion of those
    // leading slots stays zero — a documented bounded approximation that does not
    // affect steady-state output for the constant-grid test material).
    for (var k = 0; k < this._kx[1]; ++k)
      for (var i = iTemp; i < 38; ++i) {
        xRe[i][k] = xLow[k][(i + EnvAdjOffset) * 2];
        xIm[i][k] = xLow[k][(i + EnvAdjOffset) * 2 + 1];
      }
    for (var k = 0; k < this._kx[1]; ++k)
      for (var i = 0; i < iTemp; ++i) {
        xRe[i][k] = xLow[k][(i + EnvAdjOffset) * 2];
        xIm[i][k] = xLow[k][(i + EnvAdjOffset) * 2 + 1];
      }
    for (var k = this._kx[1]; k < this._kx[1] + this._m[1]; ++k)
      for (var i = iTemp; i < iF; ++i) {
        xRe[i][k] = y[i][k * 2];
        xIm[i][k] = y[i][k * 2 + 1];
      }
  }

  private void QmfSynthesis(float[][] xRe, float[][] xIm, AacSbrChannel d, float[] output2048) {
    // 32 time slots -> 64 output samples each = 2048.
    var outSlot = new float[64];
    for (var slot = 0; slot < 32; ++slot) {
      d.Synthesis.Synthesis(xRe[slot], xIm[slot], outSlot);
      Array.Copy(outSlot, 0, output2048, slot * 64, 64);
    }
  }
}

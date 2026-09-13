#pragma warning disable CS1591

namespace Codec.Wma;

/// <summary>
/// Microsoft Windows Media Audio v1/v2 decoder (WAVEFORMATEX tags <c>0x160</c> /
/// <c>0x161</c>). A faithful port of FFmpeg's <c>libavcodec/wma.c</c>,
/// <c>wmadec.c</c>, <c>wma_common.c</c> and <c>wmadata.h</c>; the large VLC / LSP /
/// exponent-band tables live in <see cref="WmaTables"/> and were transcribed from the
/// same upstream source. Decode-only.
/// <para>
/// The codec is constructed from the stream's WAVEFORMATEX fields plus the codec-private
/// extradata (4 bytes for v1, 6 for v2) that carries the feature flags (VLC vs LSP
/// exponents, bit-reservoir use, variable block length). Each ASF media-object payload
/// is one coded superframe; <see cref="DecodeSuperframe"/> turns it into interleaved
/// signed 16-bit PCM. Per the reference, with a bit reservoir a superframe holds several
/// frames whose first frame spills bits into the previous superframe, so decoder state
/// (the reservoir buffer, MDCT overlap, block lengths) is carried across calls.
/// </para>
/// <para>
/// Features covered: VLC and LSP exponent coding, mid/side stereo, variable block
/// lengths, the bit reservoir / superframe framing, perceptual noise coding for high
/// bands and VBR streams. WMA Pro / Lossless (tags <c>0x162</c>/<c>0x163</c>) are a
/// different bitstream and are not handled.
/// </para>
/// </summary>
public sealed class WmaCodec {

  // ── block size constants (wma.h) ─────────────────────────────────────────────
  private const int BlockMinBits = 7;
  private const int BlockMaxBits = 11;
  private const int BlockMaxSize = 1 << BlockMaxBits;
  private const int HighBandMaxSize = 16;
  private const int NbLspCoefs = 10;
  private const int MaxChannels = 2;
  private const int NoiseTabSize = 8192;
  private const int LspPowBits = 7;
  private const int MaxCodedSuperframeSize = 32768;

  private const int VlcBits = 9; // informational; the bit-by-bit VLC reader ignores it

  // ── immutable config ─────────────────────────────────────────────────────────
  private readonly int _version;       // 1 or 2
  private readonly int _channels;
  private readonly int _sampleRate;
  private readonly int _blockAlign;

  private readonly bool _useExpVlc;
  private readonly bool _useBitReservoir;
  private readonly bool _useVariableBlockLen;
  private bool _useNoiseCoding;
  private float _noiseMult;

  private readonly int _frameLenBits;
  private readonly int _frameLen;
  private int _nbBlockSizes;
  private int _byteOffsetBits;
  private int _coefsStart;

  private readonly int[] _exponentSizes = new int[BlockNbSizes];
  private readonly ushort[][] _exponentBands = NewJagged(BlockNbSizes, 25);
  private readonly int[] _highBandStart = new int[BlockNbSizes];
  private readonly int[] _coefsEnd = new int[BlockNbSizes];
  private readonly int[] _exponentHighSizes = new int[BlockNbSizes];
  private readonly int[][] _exponentHighBands = NewJaggedInt(BlockNbSizes, HighBandMaxSize);

  private static int BlockNbSizes => BlockMaxBits - BlockMinBits + 1;

  // VLC tables
  private WmaVlc _expVlc = null!;
  private WmaVlc _hgainVlc = null!;
  private readonly WmaVlc[] _coefVlc = new WmaVlc[2];
  private readonly ushort[][] _runTable = new ushort[2][];
  private readonly float[][] _levelTable = new float[2][];

  private readonly float[] _noiseTable = new float[NoiseTabSize];
  private int _noiseIndex;

  // LSP curve tables
  private readonly float[] _lspCosTable = new float[BlockMaxSize];
  private readonly float[] _lspPowETable = new float[256];
  private readonly float[] _lspPowMTable1 = new float[1 << LspPowBits];
  private readonly float[] _lspPowMTable2 = new float[1 << LspPowBits];

  // MDCT per block size
  private readonly WmaMdct[] _mdct = new WmaMdct[BlockNbSizes];
  private readonly float[][] _windows = new float[BlockNbSizes][];

  // ── mutable decode state ──────────────────────────────────────────────────────
  private WmaBitReader _gb = null!;
  private bool _resetBlockLengths;
  private int _blockLenBits, _nextBlockLenBits, _prevBlockLenBits, _blockLen;
  private int _blockNum, _blockPos;
  private bool _msStereo;
  private readonly bool[] _channelCoded = new bool[MaxChannels];
  private readonly int[] _exponentsBsize = new int[MaxChannels];
  private readonly bool[] _exponentsInitialized = new bool[MaxChannels];
  private readonly float[][] _exponents = NewJaggedFloat(MaxChannels, BlockMaxSize);
  private readonly float[] _maxExponent = new float[MaxChannels];
  private readonly float[][] _coefs1 = NewJaggedFloat(MaxChannels, BlockMaxSize);
  private readonly float[][] _coefs = NewJaggedFloat(MaxChannels, BlockMaxSize);
  private readonly float[] _output = new float[BlockMaxSize * 2];
  private readonly int[][] _highBandCoded = NewJaggedInt(MaxChannels, HighBandMaxSize);
  private readonly int[][] _highBandValues = NewJaggedInt(MaxChannels, HighBandMaxSize);
  private readonly float[][] _frameOut = NewJaggedFloat(MaxChannels, BlockMaxSize * 2);

  // bit reservoir carryover
  private readonly byte[] _lastSuperframe = new byte[MaxCodedSuperframeSize + 8];
  private int _lastBitoffset;
  private int _lastSuperframeLen;

  /// <summary>Number of output samples per decoded frame (one frame per superframe without a bit reservoir).</summary>
  public int FrameLength => this._frameLen;

  /// <summary>Channel count carried by the stream.</summary>
  public int Channels => this._channels;

  /// <summary>Sample rate in Hz.</summary>
  public int SampleRate => this._sampleRate;

  /// <summary>True when VLC exponent coding is in use; false for LSP exponent coding.</summary>
  public bool UsesExponentVlc => this._useExpVlc;

  /// <summary>True when the bit-reservoir / multi-frame superframe framing is in use.</summary>
  public bool UsesBitReservoir => this._useBitReservoir;

  /// <summary>True when variable (split) block lengths are in use.</summary>
  public bool UsesVariableBlockLength => this._useVariableBlockLen;

  /// <summary>True when perceptual noise coding is active for this rate/bitrate.</summary>
  public bool UsesNoiseCoding => this._useNoiseCoding;

  /// <summary>Number of distinct block sizes used by the variable-block-length scheme.</summary>
  public int BlockSizeCount => this._nbBlockSizes;

  /// <summary>log2 of the frame length in samples.</summary>
  public int FrameLengthBits => this._frameLenBits;

  /// <summary>
  /// Constructs a decoder from the stream parameters. <paramref name="version"/> is 1
  /// (tag 0x160) or 2 (tag 0x161). <paramref name="extradata"/> is the WAVEFORMATEX
  /// codec-private tail (≥4 bytes for v1, ≥6 for v2); shorter data is tolerated with the
  /// feature flags defaulting to off.
  /// </summary>
  public WmaCodec(int version, int channels, int sampleRate, long bitrate, int blockAlign, ReadOnlySpan<byte> extradata) {
    if (version is not (1 or 2))
      throw new ArgumentOutOfRangeException(nameof(version), "WMA decoder handles version 1 or 2 only.");
    if (channels is < 1 or > MaxChannels)
      throw new ArgumentOutOfRangeException(nameof(channels), "WMA v1/v2 supports 1 or 2 channels.");
    if (sampleRate is <= 0 or > 50000)
      throw new ArgumentOutOfRangeException(nameof(sampleRate), "WMA v1/v2 sample rate must be in (0, 50000].");
    if (bitrate <= 0)
      throw new ArgumentOutOfRangeException(nameof(bitrate), "WMA bitrate must be positive.");
    if (blockAlign <= 0)
      throw new ArgumentOutOfRangeException(nameof(blockAlign), "block_align must be set.");

    this._version = version;
    this._channels = channels;
    this._sampleRate = sampleRate;
    this._blockAlign = blockAlign;

    // extract flag info (wma_decode_init)
    var flags2 = 0;
    if (version == 1 && extradata.Length >= 4)
      flags2 = extradata[2] | (extradata[3] << 8);
    else if (version == 2 && extradata.Length >= 6)
      flags2 = extradata[4] | (extradata[5] << 8);

    this._useExpVlc = (flags2 & 0x0001) != 0;
    this._useBitReservoir = (flags2 & 0x0002) != 0;
    this._useVariableBlockLen = (flags2 & 0x0004) != 0;

    if (version == 2 && extradata.Length >= 8) {
      var lo = extradata[4] | (extradata[5] << 8);
      if (lo == 0xd && this._useVariableBlockLen)
        this._useVariableBlockLen = false; // mirrors the issue1503 workaround
    }

    for (var i = 0; i < MaxChannels; ++i) this._maxExponent[i] = 1.0f;

    // ff_wma_get_frame_len_bits (version < 3 path)
    this._frameLenBits = FrameLenBits(sampleRate, version);
    this._nextBlockLenBits = this._frameLenBits;
    this._prevBlockLenBits = this._frameLenBits;
    this._blockLenBits = this._frameLenBits;
    this._frameLen = 1 << this._frameLenBits;

    if (this._useVariableBlockLen) {
      var nb = ((flags2 >> 3) & 3) + 1;
      if (bitrate / channels >= 32000) nb += 2;
      var nbMax = this._frameLenBits - BlockMinBits;
      if (nb > nbMax) nb = nbMax;
      this._nbBlockSizes = nb + 1;
    } else {
      this._nbBlockSizes = 1;
    }

    InitRateDependent(bitrate);

    // init MDCT windows (sine windows) and inverse transforms
    for (var i = 0; i < this._nbBlockSizes; ++i) {
      var n = this._frameLen >> i;            // block length for this size index
      this._windows[i] = SineWindow(n);
      this._mdct[i] = new WmaMdct(n, 1.0f / 32768.0f);
    }

    this._resetBlockLengths = true;

    if (this._useNoiseCoding) {
      this._noiseMult = this._useExpVlc ? 0.02f : 0.04f;
      uint seed = 1;
      var norm = (1.0 / (1L << 31)) * Math.Sqrt(3) * this._noiseMult;
      for (var i = 0; i < NoiseTabSize; ++i) {
        seed = seed * 314159 + 1;
        this._noiseTable[i] = (float)((int)seed * norm);
      }
    }

    InitVlcTables(bitrate);

    if (this._useExpVlc)
      this._expVlc = WmaVlc.FromCodes(WmaTables.AacScalefactorCode, WmaTables.AacScalefactorBits);
    else
      LspToCurveInit(this._frameLen);

    if (this._useNoiseCoding)
      this._hgainVlc = WmaVlc.FromLengths(WmaTables.HgainHuffTab, symbolOffset: -18);
  }

  private static int FrameLenBits(int sampleRate, int version) {
    int bits;
    if (sampleRate <= 16000) bits = 9;
    else if (sampleRate <= 22050 || (sampleRate <= 32000 && version == 1)) bits = 10;
    else bits = 11; // version < 3 caps at 11 for the rates WMA v1/v2 supports
    return bits;
  }

  // ── ff_wma_init: rate-dependent setup + exponent band tables ──────────────────
  private void InitRateDependent(long bitRate) {
    this._useNoiseCoding = true;
    double highFreq = this._sampleRate * 0.5;

    var sampleRate1 = this._sampleRate;
    if (this._version == 2) {
      if (sampleRate1 >= 44100) sampleRate1 = 44100;
      else if (sampleRate1 >= 22050) sampleRate1 = 22050;
      else if (sampleRate1 >= 16000) sampleRate1 = 16000;
      else if (sampleRate1 >= 11025) sampleRate1 = 11025;
      else if (sampleRate1 >= 8000) sampleRate1 = 8000;
    }

    var bps = (float)bitRate / (this._channels * this._sampleRate);
    this._byteOffsetBits = Log2((int)(bps * this._frameLen / 8.0 + 0.5)) + 2;

    var bps1 = bps;
    if (this._channels == 2) bps1 = bps * 1.6f;
    if (sampleRate1 == 44100) {
      if (bps1 >= 0.61) this._useNoiseCoding = false; else highFreq *= 0.4;
    } else if (sampleRate1 == 22050) {
      if (bps1 >= 1.16) this._useNoiseCoding = false;
      else if (bps1 >= 0.72) highFreq *= 0.7; else highFreq *= 0.6;
    } else if (sampleRate1 == 16000) {
      highFreq = bps > 0.5 ? highFreq * 0.5 : highFreq * 0.3;
    } else if (sampleRate1 == 11025) {
      highFreq *= 0.7;
    } else if (sampleRate1 == 8000) {
      if (bps <= 0.625) highFreq *= 0.5;
      else if (bps > 0.75) this._useNoiseCoding = false; else highFreq *= 0.65;
    } else {
      if (bps >= 0.8) highFreq *= 0.75;
      else if (bps >= 0.6) highFreq *= 0.6; else highFreq *= 0.5;
    }

    this._coefsStart = this._version == 1 ? 3 : 0;

    for (var k = 0; k < this._nbBlockSizes; ++k) {
      var blockLen = this._frameLen >> k;

      if (this._version == 1) {
        var lpos = 0;
        int i;
        for (i = 0; i < 25; ++i) {
          var a = WmaTables.CriticalFreqs[i];
          var b = this._sampleRate;
          var pos = ((blockLen * 2 * a) + (b >> 1)) / b;
          if (pos > blockLen) pos = blockLen;
          this._exponentBands[0][i] = (ushort)(pos - lpos);
          if (pos >= blockLen) { ++i; break; }
          lpos = pos;
        }
        this._exponentSizes[0] = i;
      } else {
        byte[]? table = null;
        var a = this._frameLenBits - BlockMinBits - k;
        if (a < 3) {
          if (this._sampleRate >= 44100) table = WmaTables.ExponentBand44100[a];
          else if (this._sampleRate >= 32000) table = WmaTables.ExponentBand32000[a];
          else if (this._sampleRate >= 22050) table = WmaTables.ExponentBand22050[a];
        }
        if (table != null) {
          var n = table[0];
          for (var i = 0; i < n; ++i) this._exponentBands[k][i] = table[i + 1];
          this._exponentSizes[k] = n;
        } else {
          var j = 0;
          var lpos = 0;
          for (var i = 0; i < 25; ++i) {
            var aa = WmaTables.CriticalFreqs[i];
            var b = this._sampleRate;
            var pos = ((blockLen * 2 * aa) + (b << 1)) / (4 * b);
            pos <<= 2;
            if (pos > blockLen) pos = blockLen;
            if (pos > lpos) this._exponentBands[k][j++] = (ushort)(pos - lpos);
            if (pos >= blockLen) break;
            lpos = pos;
          }
          this._exponentSizes[k] = j;
        }
      }

      this._coefsEnd[k] = (this._frameLen - ((this._frameLen * 9) / 100)) >> k;
      this._highBandStart[k] = (int)((blockLen * 2 * highFreq) / this._sampleRate + 0.5);

      var sizes = this._exponentSizes[k];
      var jj = 0;
      var p = 0;
      for (var i = 0; i < sizes; ++i) {
        var start = p;
        p += this._exponentBands[k][i];
        var end = p;
        if (start < this._highBandStart[k]) start = this._highBandStart[k];
        if (end > this._coefsEnd[k]) end = this._coefsEnd[k];
        if (end > start) this._exponentHighBands[k][jj++] = end - start;
      }
      this._exponentHighSizes[k] = jj;
    }
  }

  private void InitVlcTables(long bitRate) {
    var bps = (float)bitRate / (this._channels * this._sampleRate);
    var bps1 = this._channels == 2 ? bps * 1.6f : bps;
    var coefVlcTable = 2;
    if (this._sampleRate >= 32000) {
      if (bps1 < 0.72) coefVlcTable = 0;
      else if (bps1 < 1.16) coefVlcTable = 1;
    }
    for (var t = 0; t < 2; ++t) {
      var tbl = WmaTables.CoefVlcs[coefVlcTable * 2 + t];
      this._coefVlc[t] = WmaVlc.FromCodes(tbl.HuffCodes, tbl.HuffBits);
      BuildRunLevel(tbl, out this._runTable[t], out this._levelTable[t]);
    }
  }

  // init_coef_vlc: expand the per-level run table.
  private static void BuildRunLevel(WmaTables.CoefVlcTable tbl, out ushort[] runTable, out float[] levelTable) {
    var n = tbl.N;
    runTable = new ushort[n];
    levelTable = new float[n];
    var i = 2;
    var level = 1;
    var k = 0;
    while (i < n) {
      var l = tbl.Levels[k++];
      for (var j = 0; j < l; ++j) {
        runTable[i] = (ushort)j;
        levelTable[i] = level;
        ++i;
      }
      ++level;
    }
  }

  // ── LSP curve tables (wma_lsp_to_curve_init) ─────────────────────────────────
  private void LspToCurveInit(int frameLen) {
    var wdel = Math.PI / frameLen;
    for (var i = 0; i < frameLen; ++i)
      this._lspCosTable[i] = (float)(2.0 * Math.Cos(wdel * i));
    for (var i = 0; i < 256; ++i) {
      var e = i - 126;
      this._lspPowETable[i] = (float)Math.Pow(2.0, e * -0.25);
    }
    var b = 1.0;
    for (var i = (1 << LspPowBits) - 1; i >= 0; --i) {
      var m = (1 << LspPowBits) + i;
      var a = m * (0.5 / (1 << LspPowBits));
      a = 1.0 / Math.Sqrt(Math.Sqrt(a));
      this._lspPowMTable1[i] = (float)(2 * a - b);
      this._lspPowMTable2[i] = (float)(b - a);
      b = a;
    }
  }

  private float PowM14(float x) {
    var v = BitConverter.SingleToUInt32Bits(x);
    var e = v >> 23;
    var m = (int)((v >> (23 - LspPowBits)) & ((1 << LspPowBits) - 1));
    var tv = ((v << LspPowBits) & ((1u << 23) - 1)) | (127u << 23);
    var t = BitConverter.UInt32BitsToSingle(tv);
    var a = this._lspPowMTable1[m];
    var bb = this._lspPowMTable2[m];
    return this._lspPowETable[e] * (a + bb * t);
  }

  private void LspToCurve(float[] outBuf, out float valMax, int n, float[] lsp) {
    valMax = 0;
    for (var i = 0; i < n; ++i) {
      var p = 0.5f;
      var q = 0.5f;
      var w = this._lspCosTable[i];
      for (var j = 1; j < NbLspCoefs; j += 2) {
        q *= w - lsp[j - 1];
        p *= w - lsp[j];
      }
      p *= p * (2.0f - w);
      q *= q * (2.0f + w);
      var vv = p + q;
      vv = this.PowM14(vv);
      if (vv > valMax) valMax = vv;
      outBuf[i] = vv;
    }
  }

  private void DecodeExpLsp(int ch) {
    var lsp = new float[NbLspCoefs];
    for (var i = 0; i < NbLspCoefs; ++i) {
      int val = i is 0 || i >= 8 ? (int)this._gb.GetBits(3) : (int)this._gb.GetBits(4);
      lsp[i] = WmaTables.LspCodebook[i][val];
    }
    this.LspToCurve(this._exponents[ch], out this._maxExponent[ch], this._blockLen, lsp);
  }

  // pow(10, i/16) for i in -60..95 (wmadec.c pow_tab), offset by +60 in use.
  private static readonly double[] PowTab = BuildPowTab();
  private static double[] BuildPowTab() {
    var t = new double[156];
    for (var i = 0; i < 156; ++i) t[i] = Math.Pow(10.0, (i - 60) / 16.0);
    return t;
  }

  private bool DecodeExpVlc(int ch) {
    var ptr = this._exponentBands[this._frameLenBits - this._blockLenBits];
    var pi = 0;
    var q = 0;
    var qEnd = this._blockLen;
    var exps = this._exponents[ch];
    float maxScale = 0;
    int lastExp;
    if (this._version == 1) {
      lastExp = (int)this._gb.GetBits(5) + 10;
      var v0 = (float)PowTab[lastExp + 60];
      maxScale = v0;
      var n0 = ptr[pi++];
      // emulate the (n & 3) duff's-device fill of n0 entries
      for (var c = 0; c < n0 && q < qEnd; ++c) exps[q++] = v0;
    } else {
      lastExp = 36;
    }

    while (q < qEnd) {
      var code = this._expVlc.Decode(this._gb, out var ok);
      if (!ok) return false;
      lastExp += code - 60;
      if ((uint)(lastExp + 60) >= PowTab.Length) return false;
      var v = (float)PowTab[lastExp + 60];
      if (v > maxScale) maxScale = v;
      var n = ptr[pi++];
      for (var c = 0; c < n && q < qEnd; ++c) exps[q++] = v;
    }
    this._maxExponent[ch] = maxScale;
    return true;
  }

  // ── run/level coefficient decode (ff_wma_run_level_decode, version 0 path) ─────
  private bool RunLevelDecode(WmaVlc vlc, float[] levelTable, ushort[] runTable, float[] ptr, int numCoefs, int blockLen, int coefNbBits) {
    var coefMask = blockLen - 1;
    var offset = 0;
    for (; offset < numCoefs; ++offset) {
      var code = vlc.Decode(this._gb, out var ok);
      if (!ok) return false;
      if (code > 1) {
        offset += runTable[code];
        var sign = (int)this._gb.GetBit() - 1; // 0 -> -1, 1 -> 0
        var lvl = levelTable[code];
        ptr[offset & coefMask] = sign == 0 ? lvl : -lvl;
      } else if (code == 1) {
        break; // EOB
      } else {
        // escape (version == 0 path for WMA v1/v2)
        var level = (int)this._gb.GetBits(coefNbBits);
        offset += (int)this._gb.GetBits(this._frameLenBits);
        var sign = (int)this._gb.GetBit() - 1;
        ptr[offset & coefMask] = (level ^ sign) - sign;
      }
    }
    return offset <= numCoefs;
  }

  // ── superframe / frame / block decode ─────────────────────────────────────────

  /// <summary>
  /// Decodes one coded superframe (one ASF media-object payload, padded/truncated to
  /// <c>block_align</c>) to interleaved signed 16-bit PCM. Returns an empty array when
  /// the superframe only fills the bit reservoir (no output yet) and throws
  /// <see cref="InvalidDataException"/> on a corrupt superframe.
  /// </summary>
  public short[] DecodeSuperframe(ReadOnlySpan<byte> payload) {
    var bufSize = this._blockAlign;
    var buf = new byte[bufSize + 8];
    var copy = Math.Min(payload.Length, bufSize);
    payload[..copy].CopyTo(buf);

    this._gb = new WmaBitReader(buf, 0, bufSize * 8);

    int nbFrames;
    if (this._useBitReservoir) {
      this._gb.SkipBits(4); // super frame index
      nbFrames = (int)this._gb.GetBits(4) - (this._lastSuperframeLen <= 0 ? 1 : 0);
      if (nbFrames <= 0) {
        // this superframe only feeds the reservoir
        var q0 = this._lastSuperframeLen;
        if (q0 + bufSize - 1 > MaxCodedSuperframeSize) throw new InvalidDataException("WMA reservoir overflow.");
        var len0 = bufSize - 1;
        for (var i = 0; i < len0; ++i) this._lastSuperframe[q0 + i] = (byte)this._gb.GetBits(8);
        this._lastSuperframeLen += 8 * bufSize - 8;
        return [];
      }
    } else {
      nbFrames = 1;
    }

    var totalSamples = nbFrames * this._frameLen;
    var perChannel = new float[this._channels][];
    for (var ch = 0; ch < this._channels; ++ch) perChannel[ch] = new float[totalSamples];
    var samplesOffset = 0;

    if (this._useBitReservoir) {
      var bitOffset = (int)this._gb.GetBits(this._byteOffsetBits + 3);
      if (bitOffset > this._gb.BitsLeft) throw new InvalidDataException("WMA invalid last-frame bit offset.");

      if (this._lastSuperframeLen > 0) {
        var q = this._lastSuperframeLen;
        var len = bitOffset;
        while (len > 7) { this._lastSuperframe[q++] = (byte)this._gb.GetBits(8); len -= 8; }
        if (len > 0) this._lastSuperframe[q++] = (byte)(this._gb.GetBits(len) << (8 - len));
        for (var z = 0; z < 8; ++z) this._lastSuperframe[q + z] = 0;

        var lastGb = new WmaBitReader(this._lastSuperframe, 0, this._lastSuperframeLen * 8 + bitOffset);
        if (this._lastBitoffset > 0) lastGb.SkipBits(this._lastBitoffset);
        this._gb = lastGb;
        DecodeFrame(perChannel, samplesOffset);
        samplesOffset += this._frameLen;
        nbFrames--;
      }

      var pos = bitOffset + 4 + 4 + this._byteOffsetBits + 3;
      if (pos >= MaxCodedSuperframeSize * 8 || pos > bufSize * 8) throw new InvalidDataException("WMA superframe position out of range.");
      this._gb = new WmaBitReader(buf, pos >> 3, (bufSize - (pos >> 3)) * 8);
      var skew = pos & 7;
      if (skew > 0) this._gb.SkipBits(skew);

      this._resetBlockLengths = true;
      for (var i = 0; i < nbFrames; ++i) {
        DecodeFrame(perChannel, samplesOffset);
        samplesOffset += this._frameLen;
      }

      var endPos = this._gb.BitsCount + ((bitOffset + 4 + 4 + this._byteOffsetBits + 3) & ~7);
      this._lastBitoffset = endPos & 7;
      endPos >>= 3;
      var tail = bufSize - endPos;
      if (tail > MaxCodedSuperframeSize || tail < 0) throw new InvalidDataException("WMA reservoir tail invalid.");
      this._lastSuperframeLen = tail;
      Array.Copy(buf, endPos, this._lastSuperframe, 0, tail);
    } else {
      DecodeFrame(perChannel, samplesOffset);
    }

    return Interleave(perChannel, totalSamples);
  }

  private void DecodeFrame(float[][] samples, int samplesOffset) {
    this._blockNum = 0;
    this._blockPos = 0;
    while (true) {
      var last = this.DecodeBlock();
      if (last) break;
    }
    for (var ch = 0; ch < this._channels; ++ch) {
      Array.Copy(this._frameOut[ch], 0, samples[ch], samplesOffset, this._frameLen);
      Array.Copy(this._frameOut[ch], this._frameLen, this._frameOut[ch], 0, this._frameLen);
    }
  }

  // returns true if last block of frame
  private bool DecodeBlock() {
    var channels = this._channels;

    if (this._useVariableBlockLen) {
      var n = Log2(this._nbBlockSizes - 1) + 1;
      if (this._resetBlockLengths) {
        this._resetBlockLengths = false;
        var v0 = (int)this._gb.GetBits(n);
        if (v0 >= this._nbBlockSizes) throw new InvalidDataException("WMA prev_block_len out of range.");
        this._prevBlockLenBits = this._frameLenBits - v0;
        var v1 = (int)this._gb.GetBits(n);
        if (v1 >= this._nbBlockSizes) throw new InvalidDataException("WMA block_len out of range.");
        this._blockLenBits = this._frameLenBits - v1;
      } else {
        this._prevBlockLenBits = this._blockLenBits;
        this._blockLenBits = this._nextBlockLenBits;
      }
      var v2 = (int)this._gb.GetBits(n);
      if (v2 >= this._nbBlockSizes) throw new InvalidDataException("WMA next_block_len out of range.");
      this._nextBlockLenBits = this._frameLenBits - v2;
    } else {
      this._nextBlockLenBits = this._frameLenBits;
      this._prevBlockLenBits = this._frameLenBits;
      this._blockLenBits = this._frameLenBits;
    }

    if (this._frameLenBits - this._blockLenBits >= this._nbBlockSizes)
      throw new InvalidDataException("WMA block_len_bits not valid.");

    this._blockLen = 1 << this._blockLenBits;
    if (this._blockPos + this._blockLen > this._frameLen)
      throw new InvalidDataException("WMA frame_len overflow.");

    if (channels == 2) this._msStereo = this._gb.GetBit() != 0;
    var anyCoded = false;
    for (var ch = 0; ch < channels; ++ch) {
      var a = this._gb.GetBit() != 0;
      this._channelCoded[ch] = a;
      anyCoded |= a;
    }

    var bsize = this._frameLenBits - this._blockLenBits;

    if (anyCoded) {
      // total gain + coef escape bit width
      var totalGain = 1;
      while (true) {
        if (this._gb.BitsLeft < 7) throw new InvalidDataException("WMA total_gain overread.");
        var a = (int)this._gb.GetBits(7);
        totalGain += a;
        if (a != 127) break;
      }
      var coefNbBits = TotalGainToBits(totalGain);

      var n = this._coefsEnd[bsize] - this._coefsStart;
      var nbCoefs = new int[MaxChannels];
      for (var ch = 0; ch < channels; ++ch) nbCoefs[ch] = n;

      if (this._useNoiseCoding) {
        for (var ch = 0; ch < channels; ++ch) {
          if (!this._channelCoded[ch]) continue;
          var hc = this._exponentHighSizes[bsize];
          for (var i = 0; i < hc; ++i) {
            var a = (int)this._gb.GetBit();
            this._highBandCoded[ch][i] = a;
            if (a != 0) nbCoefs[ch] -= this._exponentHighBands[bsize][i];
          }
        }
        for (var ch = 0; ch < channels; ++ch) {
          if (!this._channelCoded[ch]) continue;
          var hc = this._exponentHighSizes[bsize];
          var val = unchecked((int)0x80000000);
          for (var i = 0; i < hc; ++i) {
            if (this._highBandCoded[ch][i] == 0) continue;
            if (val == unchecked((int)0x80000000)) val = (int)this._gb.GetBits(7) - 19;
            else {
              var code = this._hgainVlc.Decode(this._gb, out var hok);
              if (!hok) throw new InvalidDataException("WMA hgain VLC error.");
              val += code;
            }
            this._highBandValues[ch][i] = val;
          }
        }
      }

      if (this._blockLenBits == this._frameLenBits || this._gb.GetBit() != 0) {
        for (var ch = 0; ch < channels; ++ch) {
          if (!this._channelCoded[ch]) continue;
          if (this._useExpVlc) {
            if (!this.DecodeExpVlc(ch)) throw new InvalidDataException("WMA exponent VLC error.");
          } else {
            this.DecodeExpLsp(ch);
          }
          this._exponentsBsize[ch] = bsize;
          this._exponentsInitialized[ch] = true;
        }
      }

      for (var ch = 0; ch < channels; ++ch)
        if (this._channelCoded[ch] && !this._exponentsInitialized[ch])
          throw new InvalidDataException("WMA exponents not initialized.");

      // spectral coefficients (run/level)
      for (var ch = 0; ch < channels; ++ch) {
        if (this._channelCoded[ch]) {
          var tindex = ch == 1 && this._msStereo ? 1 : 0;
          Array.Clear(this._coefs1[ch], 0, this._blockLen);
          if (!this.RunLevelDecode(this._coefVlc[tindex], this._levelTable[tindex], this._runTable[tindex],
                this._coefs1[ch], nbCoefs[ch], this._blockLen, coefNbBits))
            throw new InvalidDataException("WMA run/level error.");
        }
        if (this._version == 1 && channels >= 2) this._gb.AlignToByte();
      }

      // normalize
      var n4 = this._blockLen / 2;
      var mdctNorm = 1.0f / n4;
      if (this._version == 1) mdctNorm *= (float)Math.Sqrt(n4);

      // build MDCT input coefficients
      for (var ch = 0; ch < channels; ++ch) {
        if (!this._channelCoded[ch]) continue;
        ReconstructCoefficients(ch, bsize, totalGain, mdctNorm);
      }

      if (this._msStereo && this._channelCoded[1]) {
        if (!this._channelCoded[0]) {
          Array.Clear(this._coefs[0], 0, this._blockLen);
          this._channelCoded[0] = true;
        }
        // butterfly: (l, r) -> (l + r, l - r)
        var c0 = this._coefs[0];
        var c1 = this._coefs[1];
        for (var i = 0; i < this._blockLen; ++i) {
          var l = c0[i];
          var r = c1[i];
          c0[i] = l + r;
          c1[i] = l - r;
        }
      }
    }

    // IMDCT + window + overlap-add
    var mdct = this._mdct[bsize];
    for (var ch = 0; ch < channels; ++ch) {
      var n4 = this._blockLen / 2;
      if (this._channelCoded[ch])
        mdct.Inverse(this._coefs[ch], this._output);
      else if (!(this._msStereo && ch == 1))
        Array.Clear(this._output, 0, this._output.Length);

      var index = this._frameLen / 2 + this._blockPos - n4;
      this.WmaWindow(this._frameOut[ch], index);
    }

    this._blockNum++;
    this._blockPos += this._blockLen;
    return this._blockPos >= this._frameLen;
  }

  private void ReconstructCoefficients(int ch, int bsize, int totalGain, float mdctNorm) {
    var coefs1 = this._coefs1[ch];
    var exponents = this._exponents[ch];
    var esize = this._exponentsBsize[ch];
    var mult = (float)(Math.Pow(10.0, totalGain * 0.05) / this._maxExponent[ch]) * mdctNorm;
    var coefs = this._coefs[ch];
    var co = 0;   // write cursor into coefs
    var c1 = 0;   // read cursor into coefs1

    if (this._useNoiseCoding) {
      var mult1 = mult;
      for (var i = 0; i < this._coefsStart; ++i) {
        coefs[co++] = this._noiseTable[this._noiseIndex] * exponents[(i << bsize) >> esize] * mult1;
        this._noiseIndex = (this._noiseIndex + 1) & (NoiseTabSize - 1);
      }

      var n1 = this._exponentHighSizes[bsize];
      var expPower = new float[HighBandMaxSize];
      var ei = this._highBandStart[bsize] << bsize >> esize; // index into exponents[]
      var lastHighBand = 0;
      for (var j = 0; j < n1; ++j) {
        var nn = this._exponentHighBands[this._frameLenBits - this._blockLenBits][j];
        if (this._highBandCoded[ch][j] != 0) {
          float e2 = 0;
          for (var i = 0; i < nn; ++i) { var vv = exponents[ei + ((i << bsize) >> esize)]; e2 += vv * vv; }
          expPower[j] = e2 / nn;
          lastHighBand = j;
        }
        ei += (nn << bsize) >> esize;
      }

      var ebase = this._coefsStart << bsize >> esize;
      ei = ebase;
      for (var j = -1; j < n1; ++j) {
        int nn;
        if (j < 0) nn = this._highBandStart[bsize] - this._coefsStart;
        else nn = this._exponentHighBands[this._frameLenBits - this._blockLenBits][j];
        if (j >= 0 && this._highBandCoded[ch][j] != 0) {
          var m1 = (float)Math.Sqrt(expPower[j] / expPower[lastHighBand]);
          m1 *= (float)Math.Pow(10.0, this._highBandValues[ch][j] * 0.05);
          m1 /= this._maxExponent[ch] * this._noiseMult;
          m1 *= mdctNorm;
          for (var i = 0; i < nn; ++i) {
            var noise = this._noiseTable[this._noiseIndex];
            this._noiseIndex = (this._noiseIndex + 1) & (NoiseTabSize - 1);
            coefs[co++] = noise * exponents[ei + ((i << bsize) >> esize)] * m1;
          }
        } else {
          for (var i = 0; i < nn; ++i) {
            var noise = this._noiseTable[this._noiseIndex];
            this._noiseIndex = (this._noiseIndex + 1) & (NoiseTabSize - 1);
            coefs[co++] = (coefs1[c1++] + noise) * exponents[ei + ((i << bsize) >> esize)] * mult;
          }
        }
        ei += (nn << bsize) >> esize;
      }

      var n = this._blockLen - this._coefsEnd[bsize];
      var lastMult = mult * exponents[ei + (((-(1 << bsize)) >> esize))];
      for (var i = 0; i < n; ++i) {
        coefs[co++] = this._noiseTable[this._noiseIndex] * lastMult;
        this._noiseIndex = (this._noiseIndex + 1) & (NoiseTabSize - 1);
      }
    } else {
      for (var i = 0; i < this._coefsStart; ++i) coefs[co++] = 0.0f;
      var n = this._coefsEnd[bsize] - this._coefsStart;
      for (var i = 0; i < n; ++i) coefs[co++] = coefs1[i] * exponents[(i << bsize) >> esize] * mult;
      n = this._blockLen - this._coefsEnd[bsize];
      for (var i = 0; i < n; ++i) coefs[co++] = 0.0f;
    }
  }

  // wma_window: overlap-add the IMDCT output into frame_out with the sine windows.
  private void WmaWindow(float[] frameOut, int outBase) {
    var inBuf = this._output;
    var inPos = 0;
    var outPos = outBase;
    int blockLen, bsize, n;

    // left part
    if (this._blockLenBits <= this._prevBlockLenBits) {
      blockLen = this._blockLen;
      bsize = this._frameLenBits - this._blockLenBits;
      var win = this._windows[bsize];
      for (var i = 0; i < blockLen; ++i) frameOut[outPos + i] += inBuf[inPos + i] * win[i];
    } else {
      blockLen = 1 << this._prevBlockLenBits;
      n = (this._blockLen - blockLen) / 2;
      bsize = this._frameLenBits - this._prevBlockLenBits;
      var win = this._windows[bsize];
      for (var i = 0; i < blockLen; ++i) frameOut[outPos + n + i] += inBuf[inPos + n + i] * win[i];
      for (var i = 0; i < n; ++i) frameOut[outPos + n + blockLen + i] = inBuf[inPos + n + blockLen + i];
    }

    outPos += this._blockLen;
    inPos += this._blockLen;

    // right part (reversed window)
    if (this._blockLenBits <= this._nextBlockLenBits) {
      blockLen = this._blockLen;
      bsize = this._frameLenBits - this._blockLenBits;
      var win = this._windows[bsize];
      for (var i = 0; i < blockLen; ++i) frameOut[outPos + i] = inBuf[inPos + i] * win[blockLen - 1 - i];
    } else {
      blockLen = 1 << this._nextBlockLenBits;
      n = (this._blockLen - blockLen) / 2;
      bsize = this._frameLenBits - this._nextBlockLenBits;
      var win = this._windows[bsize];
      for (var i = 0; i < n; ++i) frameOut[outPos + i] = inBuf[inPos + i];
      for (var i = 0; i < blockLen; ++i) frameOut[outPos + n + i] = inBuf[inPos + n + i] * win[blockLen - 1 - i];
      for (var i = 0; i < n; ++i) frameOut[outPos + n + blockLen + i] = 0;
    }
  }

  private static int TotalGainToBits(int totalGain) =>
    totalGain < 15 ? 13 : totalGain < 32 ? 12 : totalGain < 40 ? 11 : totalGain < 45 ? 10 : 9;

  private static short[] Interleave(float[][] perChannel, int samples) {
    var channels = perChannel.Length;
    var pcm = new short[samples * channels];
    for (var i = 0; i < samples; ++i)
      for (var ch = 0; ch < channels; ++ch) {
        var s = perChannel[ch][i] * 32768.0f;
        pcm[i * channels + ch] = (short)Math.Clamp((int)Math.Round(s), short.MinValue, short.MaxValue);
      }
    return pcm;
  }

  private static int Log2(int v) {
    var r = 0;
    while ((v >>= 1) != 0) ++r;
    return r;
  }

  private static float[] SineWindow(int n) {
    var w = new float[n];
    for (var i = 0; i < n; ++i) w[i] = (float)Math.Sin((i + 0.5) * (Math.PI / (2.0 * n)));
    return w;
  }

  private static ushort[][] NewJagged(int a, int b) { var r = new ushort[a][]; for (var i = 0; i < a; ++i) r[i] = new ushort[b]; return r; }
  private static int[][] NewJaggedInt(int a, int b) { var r = new int[a][]; for (var i = 0; i < a; ++i) r[i] = new int[b]; return r; }
  private static float[][] NewJaggedFloat(int a, int b) { var r = new float[a][]; for (var i = 0; i < a; ++i) r[i] = new float[b]; return r; }
}

#pragma warning disable CS1591

namespace Codec.Dts;

/// <summary>
/// Decodes a single DTS Coherent Acoustics (DCA) core frame to per-channel float PCM. One instance
/// is reused across the stream so the QMF synthesis memory and the ADPCM predictor history persist
/// between frames. The decode follows the DTS core bitstream / FFmpeg's <c>dcadec.c</c>:
/// the primary audio coding header (subband activity, VQ start, joint intensity, the bit-allocation
/// / scale-factor / transient / quantization-index code-book selections and scale-factor adjusts),
/// then per subframe a set of sub-subframes carrying bit allocation, scale factors, the quantized
/// subband samples (Huffman, block-code or plain), inverse ADPCM prediction, high-frequency VQ and
/// the LFE samples; finally the 32-band QMF synthesis and LFE FIR interpolation.
/// <para>
/// Scope: the standard 16-bit-BE core is decoded. The DTS-HD extension substreams (XCH, XXCH, X96,
/// XBR, XLL and the EXSS container) are out of scope — they are parsed past at the stream level and
/// the embedded core is decoded on its own. Stereo down-mixing is not applied; channels are emitted
/// at the core's native count.
/// </para>
/// </summary>
internal sealed class DtsFrameDecoder {

  private const int MaxPrimChannels = 7;   // DCA_PRIM_CHANNELS_MAX
  private const int Subbands = 32;         // DCA_SUBBANDS
  private const int MaxSubsubframes = 4;

  // ── Code books (built once) ───────────────────────────────────────────────
  private static readonly DtsBitAllocBook BitAllocIndex = BuildBitAllocIndex();
  private static readonly DtsBitAllocBook ScaleFactor = BuildScaleFactor();
  private static readonly DtsBitAllocBook Tmode = BuildTmode();
  private static readonly DtsBitAllocBook?[] SampleBitAlloc = BuildSampleBitAlloc();

  // FFmpeg quant_index_huffman bit widths / thresholds per abits group (1..10), index 0 unused.
  private static readonly int[] QuantIndexBitLen = [0, 1, 2, 2, 2, 2, 3, 3, 3, 3, 3];
  private static readonly int[] QuantIndexThreshold = [0, 1, 3, 3, 3, 3, 7, 7, 7, 7, 7];
  private static readonly float[] ScaleFactorAdjTable = [1.0f, 1.1250f, 1.2500f, 1.4375f];

  // ── Persistent per-channel synthesis / predictor state ────────────────────
  private readonly DtsQmf[] _qmf;
  private readonly float[][][] _subbandSamplesHist; // [chan][subband][4]

  public DtsFrameDecoder() {
    this._qmf = new DtsQmf[MaxPrimChannels];
    for (var c = 0; c < MaxPrimChannels; ++c)
      this._qmf[c] = new DtsQmf();
    this._subbandSamplesHist = new float[MaxPrimChannels][][];
    for (var c = 0; c < MaxPrimChannels; ++c) {
      this._subbandSamplesHist[c] = new float[Subbands][];
      for (var s = 0; s < Subbands; ++s)
        this._subbandSamplesHist[c][s] = new float[4];
    }
  }

  // ── Per-frame state ───────────────────────────────────────────────────────
  private int _primChannels;
  private int _amode;
  private int _lfe;                  // 0, 1 or 2
  private bool _crcPresent;
  private bool _aspf;
  private bool _predictorHistory;
  private int _sampleBlocks;
  private int _subframes;
  private int _bitRateIndex;
  private bool _perfectReconstruction;

  private readonly int[] _subbandActivity = new int[MaxPrimChannels];
  private readonly int[] _vqStartSubband = new int[MaxPrimChannels];
  private readonly int[] _jointIntensity = new int[MaxPrimChannels];
  private readonly int[] _transientHuffman = new int[MaxPrimChannels];
  private readonly int[] _scalefactorHuffman = new int[MaxPrimChannels];
  private readonly int[] _bitallocHuffman = new int[MaxPrimChannels];
  private readonly int[][] _quantIndexHuffman = NewIntMatrix(MaxPrimChannels, 11);
  private readonly float[][] _scalefactorAdj = NewFloatMatrix(MaxPrimChannels, 11);

  // Per-subframe header state.
  private readonly int[][] _predictionMode = NewIntMatrix(MaxPrimChannels, Subbands);
  private readonly int[][] _predictionVq = NewIntMatrix(MaxPrimChannels, Subbands);
  private readonly int[][] _bitalloc = NewIntMatrix(MaxPrimChannels, Subbands);
  private readonly int[][] _transitionMode = NewIntMatrix(MaxPrimChannels, Subbands);
  private readonly int[][][] _scaleFactor = NewScaleFactor(MaxPrimChannels, Subbands);
  private readonly int[][] _highFreqVq = NewIntMatrix(MaxPrimChannels, Subbands);
  private readonly int[] _subsubframes = new int[16];
  private readonly int[] _partialSamples = new int[16];

  // LFE decimated samples (history + current frame). Sized for the worst case.
  private readonly float[] _lfeData = new float[64 * 2 + 256];
  private float _lfeScaleFactor;

  // Subband samples for the whole frame: [block][chan][subband][8].
  private float[][][][]? _subbandSamples;

  /// <summary>
  /// Decodes the core frame at <paramref name="offset"/>. On success <paramref name="outChannelCount"/>
  /// is the native channel count (AMODE channels + LFE last when present) and the return value is
  /// <c>[channel][sample]</c> float PCM (normalised to roughly ±1). Returns <see langword="null"/>
  /// when the frame cannot be decoded.
  /// </summary>
  public float[][]? DecodeFrame(byte[] data, int offset, DtsFrameHeader header, out int outChannelCount) {
    outChannelCount = 0;
    try {
      var len = Math.Min(header.FrameSize, data.Length - offset);
      var r = new DtsBitReader(data, offset, len);

      this._amode = header.Amode;
      this._lfe = header.Lfe;
      this._crcPresent = header.CrcPresent;
      this._aspf = header.Aspf;
      this._predictorHistory = header.PredictorHistory;
      this._sampleBlocks = header.SampleBlocks;
      this._subframes = header.Subframes;
      this._bitRateIndex = header.BitRateIndex;
      // MULTIRATE_INTER selects the perfect-reconstruction prototype; it is bit 0 of the field that
      // immediately follows the (optional) header CRC. Re-read it from the parsed header position.
      this._perfectReconstruction = ReadMultirateInter(data, offset, header);

      // Seek the reader to the end of the parsed frame header (which is byte-exact in bits).
      r.SkipBits(header.HeaderBitLength);

      if (!this.ParseAudioCodingHeader(r))
        return null;

      var totalChannels = this._primChannels + (this._lfe > 0 ? 1 : 0);
      var blocks = this._sampleBlocks / 8;
      if (blocks <= 0)
        return null;

      this._subbandSamples = NewSubbandSamples(blocks, this._primChannels);
      Array.Clear(this._lfeData, 0, this._lfeData.Length);

      var currentSubframe = 0;
      var currentSubsubframe = 0;
      for (var blockIndex = 0; blockIndex < blocks; ++blockIndex) {
        if (currentSubframe >= this._subframes)
          return null;
        if (currentSubsubframe == 0 && !this.ParseSubframeHeader(r, blockIndex, currentSubframe))
          return null;
        if (!this.DecodeSubsubframe(r, blockIndex, currentSubframe, currentSubsubframe))
          return null;

        ++currentSubsubframe;
        if (currentSubsubframe >= this._subsubframes[currentSubframe]) {
          currentSubsubframe = 0;
          ++currentSubframe;
        }
      }

      // QMF synthesis + LFE → per-channel PCM (256 samples per block).
      var samplesPerChannel = 256 * blocks;
      var pcm = new float[totalChannels][];
      for (var c = 0; c < totalChannels; ++c)
        pcm[c] = new float[samplesPerChannel];

      this.FilterChannels(blocks, pcm);

      outChannelCount = totalChannels;
      return pcm;
    } catch (InvalidDataException) {
      return null;
    } catch (IndexOutOfRangeException) {
      return null;
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }

  // The MULTIRATE_INTER flag sits right after the optional 16-bit header CRC. Re-parse minimally.
  private static bool ReadMultirateInter(byte[] data, int offset, DtsFrameHeader header) {
    var buffer = data.AsSpan(offset, Math.Min(data.Length - offset, 32)).ToArray();
    var r = new DtsBitReader(buffer, 0, buffer.Length);
    r.SkipBits(32 + 1 + 5 + 1 + 7 + 14 + 6 + 4 + 5 + 1 + 1 + 1 + 1 + 1 + 3 + 1 + 1 + 2 + 1);
    if (header.CrcPresent)
      r.SkipBits(16);
    return r.ReadFlag();
  }

  // ── Primary audio coding header (FFmpeg dca_parse_audio_coding_header, base_channel 0) ──
  private bool ParseAudioCodingHeader(DtsBitReader r) {
    var nchans = (int)r.ReadBits(3) + 1;
    if (nchans > MaxPrimChannels)
      return false;
    this._primChannels = nchans;

    for (var i = 0; i < this._primChannels; ++i) {
      this._subbandActivity[i] = (int)r.ReadBits(5) + 2;
      if (this._subbandActivity[i] > Subbands)
        this._subbandActivity[i] = Subbands;
    }
    for (var i = 0; i < this._primChannels; ++i) {
      this._vqStartSubband[i] = (int)r.ReadBits(5) + 1;
      if (this._vqStartSubband[i] > Subbands)
        this._vqStartSubband[i] = Subbands;
    }
    for (var i = 0; i < this._primChannels; ++i) this._jointIntensity[i] = (int)r.ReadBits(3);
    for (var i = 0; i < this._primChannels; ++i) this._transientHuffman[i] = (int)r.ReadBits(2);
    for (var i = 0; i < this._primChannels; ++i) this._scalefactorHuffman[i] = (int)r.ReadBits(3);
    for (var i = 0; i < this._primChannels; ++i) this._bitallocHuffman[i] = (int)r.ReadBits(3);

    for (var i = 0; i < this._primChannels; ++i)
      this._quantIndexHuffman[i][0] = 0;
    for (var j = 1; j < 11; ++j)
      for (var i = 0; i < this._primChannels; ++i)
        this._quantIndexHuffman[i][j] = (int)r.ReadBits(QuantIndexBitLen[j]);

    for (var j = 0; j < 11; ++j)
      for (var i = 0; i < this._primChannels; ++i)
        this._scalefactorAdj[i][j] = 1f;
    for (var j = 1; j < 11; ++j)
      for (var i = 0; i < this._primChannels; ++i)
        if (this._quantIndexHuffman[i][j] < QuantIndexThreshold[j])
          this._scalefactorAdj[i][j] = ScaleFactorAdjTable[(int)r.ReadBits(2)];

    if (this._crcPresent)
      r.SkipBits(16);                 // audio header CRC

    return true;
  }

  // ── Subframe header (FFmpeg dca_subframe_header) ──────────────────────────
  private bool ParseSubframeHeader(DtsBitReader r, int blockIndex, int currentSubframe) {
    this._subsubframes[currentSubframe] = (int)r.ReadBits(2) + 1;
    if (blockIndex + this._subsubframes[currentSubframe] > this._sampleBlocks / 8) {
      this._subsubframes[currentSubframe] = 1;
      return false;
    }
    this._partialSamples[currentSubframe] = (int)r.ReadBits(3);

    for (var j = 0; j < this._primChannels; ++j)
      for (var k = 0; k < this._subbandActivity[j]; ++k)
        this._predictionMode[j][k] = (int)r.ReadBits(1);

    for (var j = 0; j < this._primChannels; ++j)
      for (var k = 0; k < this._subbandActivity[j]; ++k)
        if (this._predictionMode[j][k] > 0)
          this._predictionVq[j][k] = (int)r.ReadBits(12);

    for (var j = 0; j < this._primChannels; ++j) {
      for (var k = 0; k < this._vqStartSubband[j]; ++k) {
        switch (this._bitallocHuffman[j]) {
          case 6: this._bitalloc[j][k] = (int)r.ReadBits(5); break;
          case 5: this._bitalloc[j][k] = (int)r.ReadBits(4); break;
          case 7: return false;     // invalid bit-allocation code book
          default: this._bitalloc[j][k] = BitAllocIndex.Get(r, this._bitallocHuffman[j]); break;
        }
        if (this._bitalloc[j][k] > 26)
          return false;
      }
    }

    for (var j = 0; j < this._primChannels; ++j) {
      for (var k = 0; k < this._subbandActivity[j]; ++k) {
        this._transitionMode[j][k] = 0;
        if (this._subsubframes[currentSubframe] > 1 && k < this._vqStartSubband[j] && this._bitalloc[j][k] > 0)
          this._transitionMode[j][k] = Tmode.Get(r, this._transientHuffman[j]);
      }
    }

    for (var j = 0; j < this._primChannels; ++j) {
      uint[] scaleTable;
      int logSize;
      if (this._scalefactorHuffman[j] == 6) { scaleTable = DtsTables.ScaleFactorQuant7; logSize = 7; }
      else { scaleTable = DtsTables.ScaleFactorQuant6; logSize = 6; }

      for (var k = 0; k < this._subbandActivity[j]; ++k) {
        this._scaleFactor[j][k][0] = 0;
        this._scaleFactor[j][k][1] = 0;
      }

      var scaleSum = 0;
      for (var k = 0; k < this._subbandActivity[j]; ++k) {
        if (k >= this._vqStartSubband[j] || this._bitalloc[j][k] > 0) {
          scaleSum = GetScale(r, this._scalefactorHuffman[j], scaleSum, logSize);
          this._scaleFactor[j][k][0] = (int)scaleTable[scaleSum];
        }
        if (k < this._vqStartSubband[j] && this._transitionMode[j][k] != 0) {
          scaleSum = GetScale(r, this._scalefactorHuffman[j], scaleSum, logSize);
          this._scaleFactor[j][k][1] = (int)scaleTable[scaleSum];
        }
      }
    }

    // Joint subband scale factor code book select (joint intensity coding is not reconstructed,
    // matching the reference, but the bits must be consumed to stay aligned).
    var jointHuff = new int[MaxPrimChannels];
    for (var j = 0; j < this._primChannels; ++j)
      if (this._jointIntensity[j] > 0)
        jointHuff[j] = (int)r.ReadBits(3);

    for (var j = 0; j < this._primChannels; ++j) {
      if (this._jointIntensity[j] > 0) {
        var source = this._jointIntensity[j] - 1;
        for (var k = this._subbandActivity[j]; k < this._subbandActivity[source]; ++k)
          GetScale(r, jointHuff[j], 64, 7);
      }
    }

    // Dynamic range coefficient — present only in the core (base channel 0).
    // (DYNF is captured at the frame-header level; FFmpeg reads dynrange_coef here when set.)

    if (this._crcPresent)
      r.SkipBits(16);                 // side information CRC

    // VQ encoded high-frequency subbands.
    for (var j = 0; j < this._primChannels; ++j)
      for (var k = this._vqStartSubband[j]; k < this._subbandActivity[j]; ++k)
        this._highFreqVq[j][k] = (int)r.ReadBits(10);

    // Low-frequency effect samples.
    if (this._lfe > 0) {
      var lfeSamples = 2 * this._lfe * (4 + blockIndex);
      var lfeEnd = 2 * this._lfe * (4 + blockIndex + this._subsubframes[currentSubframe]);
      for (var j = lfeSamples; j < lfeEnd; ++j)
        this._lfeData[j] = r.ReadSigned(8);

      var quant7 = (int)r.ReadBits(8);
      if (quant7 > 127)
        return false;
      this._lfeScaleFactor = DtsTables.ScaleFactorQuant7[quant7];
      var lfeScale = 0.035f * this._lfeScaleFactor;
      for (var j = lfeSamples; j < lfeEnd; ++j)
        this._lfeData[j] *= lfeScale;
    }

    return true;
  }

  private static int GetScale(DtsBitReader r, int level, int value, int log2Range) {
    if (level < 5) {
      value += ScaleFactor.Get(r, level);
      value = Math.Clamp(value, 0, (1 << log2Range) - 1);
    } else if (level < 8) {
      if (level + 1 > log2Range) {
        r.SkipBits(level + 1 - log2Range);
        value = (int)r.ReadBits(log2Range);
      } else {
        value = (int)r.ReadBits(level + 1);
      }
    }
    return value;
  }

  // ── Sub-subframe sample decode (FFmpeg dca_subsubframe) ───────────────────
  private bool DecodeSubsubframe(DtsBitReader r, int blockIndex, int currentSubframe, int subsubframe) {
    var quantStep = this._bitRateIndex == 0x1f ? DtsTables.LosslessQuant : DtsTables.LossyQuant;
    var subbandSamples = this._subbandSamples![blockIndex];
    var block = new int[8 * Subbands];

    for (var k = 0; k < this._primChannels; ++k) {
      var rscale = new float[Subbands];
      for (var l = 0; l < this._vqStartSubband[k]; ++l) {
        var abits = this._bitalloc[k][l];
        var quantStepSize = quantStep[abits];
        var sel = this._quantIndexHuffman[k][abits];

        if (abits == 0) {
          rscale[l] = 0f;
          Array.Clear(block, 8 * l, 8);
        } else {
          var sfi = this._transitionMode[k][l] != 0 && subsubframe >= this._transitionMode[k][l] ? 1 : 0;
          rscale[l] = quantStepSize * this._scaleFactor[k][l][sfi] * this._scalefactorAdj[k][sel];

          var book = abits < SampleBitAlloc.Length ? SampleBitAlloc[abits] : null;
          if (abits >= 11 || book == null || book.Vlc[sel] == null) {
            if (abits <= 7) {
              var size = DtsBlockCode.Sizes[abits - 1];
              var levels = DtsBlockCode.Levels[abits - 1];
              var code1 = (int)r.ReadBits(size);
              var code2 = (int)r.ReadBits(size);
              if (DtsBlockCode.DecodeBlockCodes(code1, code2, levels, block, 8 * l) != 0)
                return false;
            } else {
              for (var m = 0; m < 8; ++m)
                block[8 * l + m] = r.ReadSigned(abits - 3);
            }
          } else {
            for (var m = 0; m < 8; ++m)
              block[8 * l + m] = book.Get(r, sel);
          }
        }
      }

      // int → float * rscale (per subband).
      for (var l = 0; l < this._vqStartSubband[k]; ++l)
        for (var m = 0; m < 8; ++m)
          subbandSamples[k][l][m] = block[8 * l + m] * rscale[l];

      // Inverse ADPCM where prediction is on.
      for (var l = 0; l < this._vqStartSubband[k]; ++l) {
        if (this._predictionMode[k][l] == 0)
          continue;
        var vq = DtsTables.AdpcmVb[this._predictionVq[k][l]];
        var hist = this._subbandSamplesHist[k][l];
        if (this._predictorHistory)
          subbandSamples[k][l][0] += (vq[0] * hist[3] + vq[1] * hist[2] + vq[2] * hist[1] + vq[3] * hist[0]) * (1f / 8192f);
        for (var m = 1; m < 8; ++m) {
          var sum = vq[0] * subbandSamples[k][l][m - 1];
          for (var n = 2; n <= 4; ++n) {
            if (m >= n)
              sum += vq[n - 1] * subbandSamples[k][l][m - n];
            else if (this._predictorHistory)
              sum += vq[n - 1] * hist[m - n + 4];
          }
          subbandSamples[k][l][m] += sum * (1f / 8192f);
        }
      }

      // High-frequency VQ subbands.
      if (this._subbandActivity[k] > this._vqStartSubband[k]) {
        for (var l = this._vqStartSubband[k]; l < this._subbandActivity[k]; ++l) {
          var ptr = DtsTables.HighFreqVq[this._highFreqVq[k][l]];
          var fscale = this._scaleFactor[k][l][0] * (1f / 16f);
          var vqOffset = subsubframe * 8;
          for (var m = 0; m < 8; ++m)
            subbandSamples[k][l][m] = ptr[vqOffset + m] * fscale;
        }
      }
    }

    // DSYNC after the last sub-subframe (or every one when ASPF set).
    if (this._aspf || subsubframe == this._subsubframes[currentSubframe] - 1) {
      if (r.ReadBits(16) != 0xFFFF)
        return false;
    }

    // Back up predictor history.
    for (var k = 0; k < this._primChannels; ++k)
      for (var l = 0; l < this._vqStartSubband[k]; ++l)
        for (var m = 0; m < 4; ++m)
          this._subbandSamplesHist[k][l][m] = subbandSamples[k][l][4 + m];

    return true;
  }

  // ── QMF synthesis + LFE interpolation → per-channel PCM ───────────────────
  private void FilterChannels(int blocks, float[][] pcm) {
    var totalChannels = this._primChannels + (this._lfe > 0 ? 1 : 0);
    // Output ordering: decoded prim channels in document (AMODE) order, LFE last. The QMF scale
    // 1/sqrt(2) / 32768 matches the reference; we keep ±1-normalised floats (no /32768) for WAV.
    var qmfScale = (float)(1.0 / Math.Sqrt(2.0));

    for (var blk = 0; blk < blocks; ++blk) {
      var subbandSamples = this._subbandSamples![blk];
      for (var k = 0; k < this._primChannels; ++k)
        this._qmf[k].Process(subbandSamples[k], this._subbandActivity[k], pcm[k], blk * 256,
          this._perfectReconstruction, qmfScale);

      if (this._lfe > 0) {
        var lfeChannel = totalChannels - 1;
        DtsLfe.Interpolate(this._lfe, this._lfeData, 2 * this._lfe * (blk + 4), pcm[lfeChannel], blk * 256);
      }
    }
  }

  // ── Code-book construction ─────────────────────────────────────────────────
  private static DtsBitAllocBook BuildBitAllocIndex() {
    var vlc = new DtsVlc?[8];
    for (var i = 0; i < DtsHuffmanTables.Bitalloc12Codes.Length; ++i)
      vlc[i] = new DtsVlc(DtsHuffmanTables.Bitalloc12Codes[i], DtsHuffmanTables.Bitalloc12Bits[i]);
    return new DtsBitAllocBook(1, vlc);
  }

  private static DtsBitAllocBook BuildScaleFactor() {
    var vlc = new DtsVlc?[8];
    for (var i = 0; i < DtsHuffmanTables.ScalesCodes.Length; ++i)
      vlc[i] = new DtsVlc(DtsHuffmanTables.ScalesCodes[i], DtsHuffmanTables.ScalesBits[i]);
    return new DtsBitAllocBook(-64, vlc);
  }

  private static DtsBitAllocBook BuildTmode() {
    var vlc = new DtsVlc?[8];
    for (var i = 0; i < DtsHuffmanTables.TmodeCodes.Length; ++i)
      vlc[i] = new DtsVlc(DtsHuffmanTables.TmodeCodes[i], DtsHuffmanTables.TmodeBits[i]);
    return new DtsBitAllocBook(0, vlc);
  }

  // dca_smpl_bitalloc[1..10]: per abits group, up to 7 selectable sample books with a per-group offset.
  private static DtsBitAllocBook?[] BuildSampleBitAlloc() {
    var books = new DtsBitAllocBook?[11];
    (ushort[][] codes, byte[][] bits, int group)[] groups = [
      (DtsHuffmanTables.Bitalloc3Codes, DtsHuffmanTables.Bitalloc3Bits, 0),
      (DtsHuffmanTables.Bitalloc5Codes, DtsHuffmanTables.Bitalloc5Bits, 1),
      (DtsHuffmanTables.Bitalloc7Codes, DtsHuffmanTables.Bitalloc7Bits, 2),
      (DtsHuffmanTables.Bitalloc9Codes, DtsHuffmanTables.Bitalloc9Bits, 3),
      (DtsHuffmanTables.Bitalloc13Codes, DtsHuffmanTables.Bitalloc13Bits, 4),
      (DtsHuffmanTables.Bitalloc17Codes, DtsHuffmanTables.Bitalloc17Bits, 5),
      (DtsHuffmanTables.Bitalloc25Codes, DtsHuffmanTables.Bitalloc25Bits, 6),
      (DtsHuffmanTables.Bitalloc33Codes, DtsHuffmanTables.Bitalloc33Bits, 7),
      (DtsHuffmanTables.Bitalloc65Codes, DtsHuffmanTables.Bitalloc65Bits, 8),
      (DtsHuffmanTables.Bitalloc129Codes, DtsHuffmanTables.Bitalloc129Bits, 9),
    ];
    for (var g = 0; g < groups.Length; ++g) {
      var (codes, bits, idx) = groups[g];
      var vlc = new DtsVlc?[8];
      for (var sel = 0; sel < codes.Length; ++sel)
        vlc[sel] = new DtsVlc(codes[sel], bits[sel]);
      books[g + 1] = new DtsBitAllocBook(DtsHuffmanTables.BitallocOffsets[idx], vlc);
    }
    return books;
  }

  // ── matrix helpers ──────────────────────────────────────────────────────────
  private static int[][] NewIntMatrix(int rows, int cols) {
    var m = new int[rows][];
    for (var i = 0; i < rows; ++i) m[i] = new int[cols];
    return m;
  }

  private static float[][] NewFloatMatrix(int rows, int cols) {
    var m = new float[rows][];
    for (var i = 0; i < rows; ++i) m[i] = new float[cols];
    return m;
  }

  private static int[][][] NewScaleFactor(int chans, int subbands) {
    var m = new int[chans][][];
    for (var c = 0; c < chans; ++c) {
      m[c] = new int[subbands][];
      for (var s = 0; s < subbands; ++s)
        m[c][s] = new int[2];
    }
    return m;
  }

  private static float[][][][] NewSubbandSamples(int blocks, int chans) {
    var m = new float[blocks][][][];
    for (var b = 0; b < blocks; ++b) {
      m[b] = new float[chans][][];
      for (var c = 0; c < chans; ++c) {
        m[b][c] = new float[Subbands][];
        for (var s = 0; s < Subbands; ++s)
          m[b][c][s] = new float[8];
      }
    }
    return m;
  }
}

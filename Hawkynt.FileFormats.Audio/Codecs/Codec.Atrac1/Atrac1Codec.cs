#pragma warning disable CS1591
namespace Codec.Atrac1;

/// <summary>
/// Sony ATRAC1 (Adaptive TRansform Acoustic Coding) decoder — a faithful, decode-only port of
/// FFmpeg's <c>libavcodec/atrac1.c</c> together with the shared ATRAC routines in
/// <c>libavcodec/atrac.c</c>. ATRAC1 is the MiniDisc codec; a stream is a sequence of 212-byte
/// sound units, one per channel, each decoding to exactly 512 samples per channel at 44100 Hz.
/// <para>Per sound unit the bitstream carries a block-size-mode byte (long/short windows per QMF
/// band), then a BFU count, word-length and scale-factor indices per block-floating-unit and the
/// quantised mantissas. Each of the three QMF bands (low/mid via a shared 48-tap inverse QMF,
/// then the high band) reconstructs its MDCT spectrum, runs a half-length inverse MDCT per block,
/// applies the sine overlap-add window, and the bands are recombined by a two-stage inverse QMF
/// synthesis chain.</para>
/// <para>Decode-only — there is no encoder. Per-channel state (MDCT overlap spectra, the three QMF
/// delay lines) is carried across sound units exactly as the reference does, so a buffer of
/// concatenated frames decodes identically to feeding the frames one at a time.</para>
/// </summary>
public sealed class Atrac1Codec {

  private const int MaxBfu = 52;
  private const int SuSize = 212;        // bytes per channel sound unit
  private const int SuSamples = 512;     // samples per channel per frame
  private const int SuMaxBits = SuSize * 8;
  private const int QmfBands = 3;
  private const int IdxHighBand = 2;

  /// <summary>Bytes per channel sound unit (212).</summary>
  public const int SoundUnitSize = SuSize;

  /// <summary>Samples produced per channel per frame (512).</summary>
  public const int SamplesPerFrame = SuSamples;

  /// <summary>Channel count (1–8).</summary>
  public int Channels { get; }

  /// <summary>Coded bytes per frame across all channels (<c>212 × channels</c>).</summary>
  public int FrameSize => SuSize * this.Channels;

  private readonly SoundUnit[] _units;
  private readonly float[] _spec = new float[SuSamples];
  private readonly float[] _low = new float[256];
  private readonly float[] _mid = new float[256];
  private readonly float[] _high = new float[512];
  private readonly float[][] _bands;

  /// <summary>Constructs a decoder for <paramref name="channels"/> (1–8) channels at 44100 Hz.</summary>
  public Atrac1Codec(int channels) {
    if (channels is < 1 or > 8)
      throw new ArgumentOutOfRangeException(nameof(channels), channels, "ATRAC1 supports 1–8 channels.");
    this.Channels = channels;
    this._units = new SoundUnit[channels];
    for (var i = 0; i < channels; ++i)
      this._units[i] = new SoundUnit();
    this._bands = [this._low, this._mid, this._high];
  }

  /// <summary>
  /// Decodes one frame (<see cref="FrameSize"/> bytes) to interleaved signed-16-bit PCM,
  /// <c>512 × channels</c> samples. State is carried for subsequent calls.
  /// </summary>
  public short[] Decode(ReadOnlySpan<byte> frame) {
    if (frame.Length < this.FrameSize)
      throw new ArgumentException($"ATRAC1 frame too small ({frame.Length} < {this.FrameSize}).", nameof(frame));

    var outCh = new float[this.Channels][];
    for (var ch = 0; ch < this.Channels; ++ch) {
      outCh[ch] = new float[SuSamples];
      var su = this._units[ch];
      var gb = new BitReader(frame.Slice(ch * SuSize, SuSize));

      ParseBsm(gb, su.Log2BlockCount);
      UnpackDequant(gb, su, this._spec);
      ImdctBlock(su);
      SubbandSynthesis(su, outCh[ch]);
    }

    var interleaved = new short[SuSamples * this.Channels];
    for (var n = 0; n < SuSamples; ++n)
      for (var ch = 0; ch < this.Channels; ++ch) {
        var v = (int)MathF.Round(outCh[ch][n] * 32768.0f);
        interleaved[n * this.Channels + ch] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
      }
    return interleaved;
  }

  /// <summary>
  /// Decodes a stream of back-to-back frames. A ragged tail shorter than one frame is ignored.
  /// Output is <c>(payload.Length / FrameSize) × 512 × channels</c> interleaved int16 samples.
  /// </summary>
  public short[] DecodeStream(ReadOnlySpan<byte> payload) {
    var frames = payload.Length / this.FrameSize;
    if (frames == 0)
      return [];
    var perFrame = SuSamples * this.Channels;
    var output = new short[frames * perFrame];
    for (var f = 0; f < frames; ++f) {
      var frame = this.Decode(payload.Slice(f * this.FrameSize, this.FrameSize));
      frame.CopyTo(output, f * perFrame);
    }
    return output;
  }

  // ── block size mode ────────────────────────────────────────────────────────────

  private static void ParseBsm(BitReader gb, int[] log2BlockCount) {
    for (var i = 0; i < 2; ++i) {
      var tmp = gb.GetBits(2);
      if ((tmp & 1) != 0)
        throw new InvalidDataException("ATRAC1: invalid block-size mode (low/mid).");
      log2BlockCount[i] = 2 - tmp;
    }

    var hi = gb.GetBits(2);
    if (hi is not (0 or 3))
      throw new InvalidDataException("ATRAC1: invalid block-size mode (high).");
    log2BlockCount[IdxHighBand] = 3 - hi;

    gb.SkipBits(2);
  }

  // ── spectrum unpack / dequant ─────────────────────────────────────────────────

  private static void UnpackDequant(BitReader gb, SoundUnit su, float[] spec) {
    var idwls = new int[MaxBfu];
    var idsfs = new int[MaxBfu];

    su.NumBfus = Atrac1Tables.BfuAmountTab1[gb.GetBits(3)];

    var bitsUsed = su.NumBfus * 10 + 32
      + Atrac1Tables.BfuAmountTab2[gb.GetBits(2)]
      + (Atrac1Tables.BfuAmountTab3[gb.GetBits(3)] << 1);

    for (var i = 0; i < su.NumBfus; ++i)
      idwls[i] = gb.GetBits(4);
    for (var i = 0; i < su.NumBfus; ++i)
      idsfs[i] = gb.GetBits(6);

    for (var bandNum = 0; bandNum < QmfBands; ++bandNum) {
      for (var bfuNum = Atrac1Tables.BfuBands[bandNum]; bfuNum < Atrac1Tables.BfuBands[bandNum + 1]; ++bfuNum) {
        var numSpecs = Atrac1Tables.SpecsPerBfu[bfuNum];
        var idwl = idwls[bfuNum];
        var wordLen = (idwl != 0 ? 1 : 0) + idwl;
        var scaleFactor = Atrac1Tables.SfTable[idsfs[bfuNum]];
        bitsUsed += wordLen * numSpecs;

        if (bitsUsed > SuMaxBits)
          throw new InvalidDataException("ATRAC1: bitstream overflow.");

        var pos = su.Log2BlockCount[bandNum] != 0
          ? Atrac1Tables.BfuStartShort[bfuNum]
          : Atrac1Tables.BfuStartLong[bfuNum];

        if (wordLen != 0) {
          var maxQuant = 1.0f / ((1 << (wordLen - 1)) - 1);
          for (var i = 0; i < numSpecs; ++i)
            spec[pos + i] = gb.GetSignedBits(wordLen) * scaleFactor * maxQuant;
        } else {
          Array.Clear(spec, pos, numSpecs);
        }
      }
    }
  }

  // ── IMDCT ──────────────────────────────────────────────────────────────────────

  private void ImdctBlock(SoundUnit su) {
    var refPos = 0;
    var pos = 0;
    for (var bandNum = 0; bandNum < QmfBands; ++bandNum) {
      var bandSamples = Atrac1Tables.SamplesPerBand[bandNum];
      var log2BlockCount = su.Log2BlockCount[bandNum];
      var numBlocks = 1 << log2BlockCount;

      int blockSize, nbits;
      if (numBlocks == 1) {
        blockSize = bandSamples >> log2BlockCount;
        nbits = Atrac1Tables.MdctLongNbits[bandNum] - log2BlockCount;
        if (nbits is not (5 or 7 or 8))
          throw new InvalidDataException("ATRAC1: invalid IMDCT size.");
      } else {
        blockSize = 32;
        nbits = 5;
      }

      var startPos = 0;
      // prev_buf initially points 16 samples before the end of the previous frame's band.
      var prevBuf = su.Spectrum[1];
      var prevOff = refPos + bandSamples - 16;
      for (var j = 0; j < numBlocks; ++j) {
        Imdct(this._spec, pos, su.Spectrum[0], refPos + startPos, nbits, bandNum);

        // Overlap-and-window into the band buffer (length 16 sine window).
        VectorFmulWindow(this._bands[bandNum], startPos, prevBuf, prevOff,
          su.Spectrum[0], refPos + startPos, Atrac1Tables.Sine32, 16);

        prevBuf = su.Spectrum[0];
        prevOff = refPos + startPos + 16;
        startPos += blockSize;
        pos += blockSize;
      }

      if (numBlocks == 1)
        Array.Copy(su.Spectrum[0], refPos + 16, this._bands[bandNum], 32, 240);

      refPos += bandSamples;
    }

    (su.Spectrum[0], su.Spectrum[1]) = (su.Spectrum[1], su.Spectrum[0]);
  }

  /// <summary>
  /// Half-length inverse MDCT of <c>1&lt;&lt;nbits</c> coefficients
  /// from <paramref name="input"/> into <paramref name="output"/>, with the optional spectrum
  /// reversal the QMF bands need. Mirrors FFmpeg's <c>at1_imdct</c> /
  /// <c>av_tx(AV_TX_FLOAT_MDCT, inverse, N, scale=-1/32768)</c>: for frame size N the inverse
  /// produces N samples via <c>out[n] = scale·Σ_k in[k]·cos((π/N)(n+½+N/2)(k+½))</c>.
  /// </summary>
  private static void Imdct(float[] input, int inputOffset, float[] output, int outputOffset,
      int nbits, int revSpec) {
    var transfSize = 1 << nbits;
    var n = transfSize;
    const float scale = -1.0f / (1 << 15);

    var coeffs = new float[n];
    Array.Copy(input, inputOffset, coeffs, 0, n);
    if (revSpec != 0)
      for (var i = 0; i < n / 2; ++i)
        (coeffs[i], coeffs[n - 1 - i]) = (coeffs[n - 1 - i], coeffs[i]);

    var factor = Math.PI / n;
    for (var p = 0; p < n; ++p) {
      double sum = 0.0;
      var a = factor * (p + 0.5 + n / 2.0);
      for (var k = 0; k < n; ++k)
        sum += coeffs[k] * Math.Cos(a * (k + 0.5));
      output[outputOffset + p] = (float)(scale * sum);
    }
  }

  /// <summary>
  /// FFmpeg <c>vector_fmul_window_c</c>: a windowed overlap-add of two length-<paramref name="len"/>
  /// segments, writing <c>2·len</c> outputs to <paramref name="dst"/> at <paramref name="dstOff"/>.
  /// </summary>
  private static void VectorFmulWindow(float[] dst, int dstOff, float[] src0, int src0Off,
      float[] src1, int src1Off, float[] win, int len) {
    // dst += len; win += len; src0 += len; then i runs -len..-1, j runs len-1..0.
    for (var k = 0; k < len; ++k) {
      var i = k;             // 0..len-1 maps to negative-index slot (len-1-... after rebase)
      var j = len - 1 - k;
      var s0 = src0[src0Off + i];
      var s1 = src1[src1Off + j];
      var wi = win[i];
      var wj = win[j];
      dst[dstOff + i] = s0 * wj - s1 * wi;
      dst[dstOff + j + len] = s0 * wi + s1 * wj;
    }
  }

  // ── inverse QMF synthesis ───────────────────────────────────────────────────────

  private void SubbandSynthesis(SoundUnit su, float[] pOut) {
    var temp = new float[256];
    var iqmfTemp = new float[512 + 46];

    // Combine low and middle bands.
    Iqmf(this._bands[0], this._bands[1], 128, temp, su.FstQmfDelay, iqmfTemp);

    // Delay the high band by 39 samples.
    Array.Copy(su.LastQmfDelay, 256, su.LastQmfDelay, 0, 39);
    Array.Copy(this._bands[2], 0, su.LastQmfDelay, 39, 256);

    // Combine (low + middle) with the high band.
    Iqmf(temp, su.LastQmfDelay, 256, pOut, su.SndQmfDelay, iqmfTemp);
  }

  /// <summary>
  /// Inverse QMF synthesis (FFmpeg <c>ff_atrac_iqmf</c>): identical filter to the one
  /// the ATRAC3 decoder (<c>Codec.Atrac3</c>) uses, replicated here so the codec is self-contained.
  /// </summary>
  private static void Iqmf(float[] inlo, float[] inhi, int nIn, float[] pOut, float[] delayBuf, float[] temp) {
    Array.Copy(delayBuf, 0, temp, 0, 46);

    for (var i = 0; i < nIn; i += 2) {
      temp[46 + 2 * i + 0] = inlo[i] + inhi[i];
      temp[46 + 2 * i + 1] = inlo[i] - inhi[i];
      temp[46 + 2 * i + 2] = inlo[i + 1] + inhi[i + 1];
      temp[46 + 2 * i + 3] = inlo[i + 1] - inhi[i + 1];
    }

    var p1 = 0;
    var outPos = 0;
    for (var j = nIn; j != 0; --j) {
      float s1 = 0.0f, s2 = 0.0f;
      for (var i = 0; i < 48; i += 2) {
        s1 += temp[p1 + i] * Atrac1Tables.QmfWindow[i];
        s2 += temp[p1 + i + 1] * Atrac1Tables.QmfWindow[i + 1];
      }
      pOut[outPos] = s2;
      pOut[outPos + 1] = s1;
      p1 += 2;
      outPos += 2;
    }

    Array.Copy(temp, nIn * 2, delayBuf, 0, 46);
  }

  // ── per-channel state ────────────────────────────────────────────────────────────

  private sealed class SoundUnit {
    public readonly int[] Log2BlockCount = new int[QmfBands];
    public int NumBfus;
    public readonly float[] Spec1 = new float[SuSamples];
    public readonly float[] Spec2 = new float[SuSamples];
    public readonly float[][] Spectrum;
    public readonly float[] FstQmfDelay = new float[46];
    public readonly float[] SndQmfDelay = new float[46];
    public readonly float[] LastQmfDelay = new float[256 + 39];

    public SoundUnit() {
      this.Spectrum = [this.Spec1, this.Spec2];
    }
  }

  // ── bit reader (big-endian, MSB-first, matching FFmpeg get_bits) ──────────────────

  private ref struct BitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    public BitReader(ReadOnlySpan<byte> data) {
      this._data = data;
      this._bitPos = 0;
    }

    public int GetBits(int n) {
      var value = 0;
      for (var i = 0; i < n; ++i) {
        var byteIndex = this._bitPos >> 3;
        var bit = byteIndex < this._data.Length
          ? (this._data[byteIndex] >> (7 - (this._bitPos & 7))) & 1
          : 0;
        value = (value << 1) | bit;
        ++this._bitPos;
      }
      return value;
    }

    /// <summary>Reads <paramref name="n"/> bits as a two's-complement signed value.</summary>
    public int GetSignedBits(int n) {
      var v = this.GetBits(n);
      var shift = 32 - n;
      return (v << shift) >> shift;
    }

    public void SkipBits(int n) => this._bitPos += n;
  }
}

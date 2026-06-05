#pragma warning disable CS1591
namespace Codec.Ra288;

/// <summary>
/// RealAudio 2.0 (28.8K) "28_8" decoder — a faithful, decode-only port of FFmpeg's
/// <c>libavcodec/ra288.c</c> together with the shared G.728 hybrid-window helper
/// (<c>g728_template.c</c>), the Levinson-Durbin recursion (<c>lpc_functions.h</c>) and the CELP
/// LPC synthesis filter (<c>celp_filters.c</c>). It is a G.728-derived backward-adaptive LD-CELP
/// speech coder: every 38-byte frame decodes to 160 samples (32 sub-blocks × 5 samples) of 8000 Hz
/// mono audio.
/// <para>Per sub-block the (little-endian) bitstream carries a 3-bit gain index and a 6/7-bit
/// codebook index; the excitation is gain-compensated against a backward-adapted log-gain LPC,
/// then run through a 36th-order speech LPC synthesis filter. Both the 36-tap speech LPC and the
/// 10-tap gain LPC are re-estimated every eight sub-blocks from the signal history via the G.728
/// hybrid window + Levinson-Durbin, with bandwidth broadening applied.</para>
/// <para>Decode-only — there is no encoder. All filter history (speech / gain history, the
/// recursive autocorrelation parts, the current LPC coefficients) is carried across frames exactly
/// as the reference does, so a multi-frame buffer decodes identically to feeding the frames one at
/// a time.</para>
/// </summary>
public sealed class Ra288Codec {

  private const int FrameSize = 38;          // coded bytes per frame
  private const int BlockSize = 5;           // samples per sub-block
  private const int BlocksPerFrame = 32;
  private const int SamplesPerFrame = BlockSize * BlocksPerFrame; // 160
  private const double Atten = 0.5625;
  private const int MaxBackwardFilterOrder = 36;
  private const int MaxBackwardFilterLen = 40;
  private const int MaxBackwardFilterNonRec = 35;

  /// <summary>Coded bytes per frame (38).</summary>
  public const int CodedFrameSize = FrameSize;

  // Per-stream state (mirrors RA288Context).
  private readonly float[] _spLpc = new float[MaxBackwardFilterOrder];   // speech LPC (spec: A)
  private readonly float[] _gainLpc = new float[10];                     // gain LPC (spec: GB)
  private readonly float[] _spHist = new float[111];                     // speech history (spec: SB)
  private readonly float[] _spRec = new float[37];                       // speech gain autocorrelation (spec: REXP)
  private readonly float[] _gainHist = new float[38];                    // log-gain history (spec: SBLG)
  private readonly float[] _gainRec = new float[11];                     // recursive gain autocorrelation (spec: REXPLG)

  /// <summary>
  /// Decodes back-to-back 38-byte frames to mono 8000 Hz signed-16-bit PCM. A ragged tail shorter
  /// than a full frame is ignored. Output length is <c>(input.Length / 38) × 160</c> samples.
  /// </summary>
  public short[] Decode(ReadOnlySpan<byte> frames) {
    var count = frames.Length / FrameSize;
    if (count == 0)
      return [];

    var output = new short[count * SamplesPerFrame];
    var outPos = 0;
    for (var f = 0; f < count; ++f) {
      this.DecodeFrame(frames.Slice(f * FrameSize, FrameSize), output, outPos);
      outPos += SamplesPerFrame;
    }
    return output;
  }

  private void DecodeFrame(ReadOnlySpan<byte> buf, short[] output, int outPos) {
    var gb = new LeBitReader(buf);

    for (var i = 0; i < BlocksPerFrame; ++i) {
      var gain = Ra288Tables.AmpTable[gb.GetBits(3)];
      var cbCoef = gb.GetBits(6 + (i & 1));

      this.DecodeBlock(gain, cbCoef);

      // Emit the freshly synthesised 5 samples (sp_hist[70 + 36 .. +5]). FFmpeg emits these as
      // normalised float (AV_SAMPLE_FMT_FLT); convert to signed-16 the same way the resampler does.
      for (var j = 0; j < BlockSize; ++j) {
        var v = (int)MathF.Round(this._spHist[70 + 36 + j] * 32768.0f);
        output[outPos + j] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
      }
      outPos += BlockSize;

      if ((i & 7) == 3) {
        this.BackwardFilter(this._spHist, this._spRec, Ra288Tables.SynWindow,
          this._spLpc, Ra288Tables.SynBwTab, 36, 40, 35, 70);
        this.BackwardFilter(this._gainHist, this._gainRec, Ra288Tables.GainWindow,
          this._gainLpc, Ra288Tables.GainBwTab, 10, 8, 20, 28);
      }
    }
  }

  // ── sub-block decode (ra288.c: decode) ──────────────────────────────────────────

  private void DecodeBlock(float gain, int cbCoef) {
    var blockOff = 70 + 36;          // current block in sp_hist
    var gainBlockOff = 28;           // current block in gain_hist
    var buffer = new float[BlockSize];

    // memmove sp_hist[70..] left by 5, 36 entries.
    Array.Copy(this._spHist, 75, this._spHist, 70, 36);

    // G.728 block 46.
    var sum = 32.0f;
    for (var i = 0; i < 10; ++i)
      sum -= this._gainHist[gainBlockOff + 9 - i] * this._gainLpc[i];

    // G.728 block 47.
    sum = Math.Clamp(sum, 0f, 60f);

    // G.728 block 48: exp(sum * 0.1151292546497) == pow(10, sum/20).
    var sumsum = Math.Exp(sum * 0.1151292546497) * gain * (1.0 / (1 << 23));

    for (var i = 0; i < BlockSize; ++i)
      buffer[i] = (float)(Ra288Tables.CodeTable[cbCoef][i] * sumsum);

    var energy = ScalarProduct(buffer, 0, buffer, 0, BlockSize);
    energy = Math.Max(energy, 5.0f / (1 << 24));

    // Shift and store the gain history.
    Array.Copy(this._gainHist, gainBlockOff + 1, this._gainHist, gainBlockOff, 9);
    this._gainHist[gainBlockOff + 9] =
      (float)(10 * Math.Log10(energy) + (10 * Math.Log10((1 << 24) / 5.0) - 32));

    CelpLpSynthesisFilter(this._spHist, blockOff, this._spLpc, buffer, BlockSize, 36);
  }

  // ── backward LPC adaptation (ra288.c: backward_filter) ──────────────────────────

  private void BackwardFilter(float[] hist, float[] rec, float[] window,
      float[] lpc, float[] tab, int order, int n, int nonRec, int moveSize) {
    var temp = new float[MaxBackwardFilterOrder + 1];

    DoHybridWindow(order, n, nonRec, temp, hist, rec, window);

    if (ComputeLpcCoefs(temp, order, lpc))
      for (var i = 0; i < order; ++i)
        lpc[i] *= tab[i];

    Array.Copy(hist, n, hist, 0, moveSize);
  }

  /// <summary>
  /// G.728 hybrid window filtering (blocks 36 and 49). Mirrors <c>do_hybrid_window</c>: window the
  /// history, compute the recursive (<paramref name="rec"/>) and non-recursive autocorrelations and
  /// combine them with the 0.5625 attenuation and the white-noise correcting factor.
  /// </summary>
  private static void DoHybridWindow(int order, int n, int nonRec, float[] outp,
      float[] hist, float[] rec, float[] window) {
    var workLen = MaxBackwardFilterOrder + MaxBackwardFilterLen + MaxBackwardFilterNonRec;
    var work = new float[workLen];
    var buffer1 = new float[MaxBackwardFilterOrder + 1];
    var buffer2 = new float[MaxBackwardFilterOrder + 1];

    var len = order + n + nonRec;
    for (var i = 0; i < len; ++i)
      work[i] = window[i] * hist[i];

    Convolve(buffer1, work, order, n, order);
    Convolve(buffer2, work, order + n, nonRec, order);

    for (var i = 0; i <= order; ++i) {
      rec[i] = (float)(rec[i] * Atten) + buffer1[i];
      outp[i] = rec[i] + buffer2[i];
    }

    outp[0] *= 257.0f / 256.0f;
  }

  /// <summary><c>convolve</c> from g728_template.c: <c>tgt[k] = Σ src[srcOff+i]·src[srcOff-k+i]</c>.</summary>
  private static void Convolve(float[] tgt, float[] src, int srcOff, int len, int n) {
    for (; n >= 0; --n)
      tgt[n] = ScalarProduct(src, srcOff, src, srcOff - n, len);
  }

  /// <summary>
  /// Levinson-Durbin recursion (<c>compute_lpc_coefs</c> with <c>i=0, normalize=1, fail=1</c>,
  /// step <c>r = -autoc[i]/32</c>). Returns <see langword="true"/> on success (LPC coefficients
  /// written to <paramref name="lpc"/>), <see langword="false"/> if the filter is degenerate.
  /// </summary>
  private static bool ComputeLpcCoefs(float[] autoc, int maxOrder, float[] lpc) {
    var err = autoc[0];
    var lpcLast = new float[maxOrder];

    if (autoc[maxOrder - 1] == 0 || err <= 0)
      return false;

    for (var i = 0; i < maxOrder; ++i) {
      var r = -autoc[i] / 32f;

      for (var j = 0; j < i; j++)
        r -= lpcLast[j] * autoc[i - j - 1];

      if (err != 0)
        r /= err;
      err *= 1.0f - r * r;

      lpc[i] = r;

      for (var j = 0; j < (i + 1) >> 1; ++j) {
        var f = lpcLast[j];
        var b = lpcLast[i - 1 - j];
        lpc[j] = f + r * b;
        lpc[i - 1 - j] = b + r * f;
      }

      if (err < 0)
        return false;

      Array.Copy(lpc, lpcLast, i + 1);
    }
    return true;
  }

  /// <summary>
  /// CELP LPC synthesis filter (readable form of <c>ff_celp_lp_synthesis_filterf</c>):
  /// <c>out[n] = in[n] - Σ_{i=1..L} filterCoeffs[i-1]·out[n-i]</c>, writing
  /// <paramref name="bufferLength"/> samples to <paramref name="outBuf"/> at
  /// <paramref name="outOff"/> using the <paramref name="filterLength"/> history values that precede
  /// it.
  /// </summary>
  private static void CelpLpSynthesisFilter(float[] outBuf, int outOff, float[] filterCoeffs,
      float[] inBuf, int bufferLength, int filterLength) {
    for (var n = 0; n < bufferLength; ++n) {
      var v = inBuf[n];
      for (var i = 1; i <= filterLength; ++i)
        v -= filterCoeffs[i - 1] * outBuf[outOff + n - i];
      outBuf[outOff + n] = v;
    }
  }

  private static float ScalarProduct(float[] a, int aOff, float[] b, int bOff, int len) {
    var sum = 0.0f;
    for (var i = 0; i < len; ++i)
      sum += a[aOff + i] * b[bOff + i];
    return sum;
  }

  // ── little-endian bit reader (FFmpeg BITSTREAM_READER_LE) ─────────────────────────

  /// <summary>
  /// FFmpeg little-endian bit reader (<c>get_bits</c> under <c>BITSTREAM_READER_LE</c>): bits are
  /// consumed least-significant-first within the byte stream, low bits before high bits.
  /// </summary>
  private ref struct LeBitReader {
    private readonly ReadOnlySpan<byte> _data;
    private int _bitPos;

    public LeBitReader(ReadOnlySpan<byte> data) {
      this._data = data;
      this._bitPos = 0;
    }

    public int GetBits(int n) {
      var value = 0;
      for (var i = 0; i < n; ++i) {
        var byteIndex = this._bitPos >> 3;
        var bit = byteIndex < this._data.Length
          ? (this._data[byteIndex] >> (this._bitPos & 7)) & 1
          : 0;
        value |= bit << i;
        ++this._bitPos;
      }
      return value;
    }
  }
}

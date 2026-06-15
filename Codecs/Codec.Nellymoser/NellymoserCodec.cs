#pragma warning disable CS1591

namespace Codec.Nellymoser;

/// <summary>
/// Nellymoser "Asao" decoder — the fixed mono speech/music codec used by Flash
/// (FLV) audio. Faithful port of FFmpeg's <c>libavcodec/nellymoserdec.c</c> plus the
/// shared tables and bit-allocation in <c>libavcodec/nellymoser.c</c> (decode-only).
/// <para>
/// Each 64-byte block decodes to 256 mono samples via two 128-point inverse-MDCT
/// half-windows with 50% (64-sample) sine-window overlap-add. Per block: a 6-bit
/// index into <c>nelly_init_table</c> seeds the band gain, then 22 5-bit deltas via
/// <c>nelly_delta_table</c> spread across the 23 bands (<c>nelly_band_sizes_table</c>);
/// the power budget is distributed by the verbatim <c>ff_nelly_get_sample_bits</c>
/// allocator; each spectral line is dequantised through <c>nelly_dequantization_table</c>
/// (or noise-filled with a randomised sign when it gets no bits).
/// </para>
/// <para>
/// Ported verbatim: all four tables, the <c>headroom</c>/<c>sum_bits</c>/
/// <c>get_sample_bits</c> allocator, the lagged-Fibonacci RNG seeding the noise sign,
/// and the per-block gain/dequant walk. The IMDCT is a direct O(n²) evaluation of
/// FFmpeg's MDCT phase (size-256 transform, <c>imdct_half</c> middle slice), which is
/// numerically equivalent to the reference FFT-based transform for these 128 points.
/// </para>
/// </summary>
public static class NellymoserCodec {

  private const int BlockLen = NellymoserTables.BlockLen;     // 64 bytes in
  private const int Samples = NellymoserTables.Samples;       // 256 samples out
  private const int BufLen = NellymoserTables.BufLen;         // 128
  private const int FillLen = NellymoserTables.FillLen;       // 124
  private const int Bands = NellymoserTables.Bands;           // 23
  private const int HeaderBits = NellymoserTables.HeaderBits; // 116
  private const int DetailBits = NellymoserTables.DetailBits; // 198
  private const int BitCap = NellymoserTables.BitCap;         // 6
  private const int BaseOff = NellymoserTables.BaseOff;       // 4228
  private const int BaseShift = NellymoserTables.BaseShift;   // 19

  // scale_bias = 1/(32768*8); folded with the 32768 output scaling below.
  private const float ScaleBias = 1.0f / (32768.0f * 8.0f);

  // ff_sine_128[i] = sin((i + 0.5) * (pi / (2 * 128))).
  private static readonly float[] Sine128 = BuildSineWindow(BufLen);

  private static float[] BuildSineWindow(int n) {
    var w = new float[n];
    for (var i = 0; i < n; ++i)
      w[i] = (float)Math.Sin((i + 0.5) * (Math.PI / (2.0 * n)));
    return w;
  }

  /// <summary>
  /// Decodes a raw Nellymoser block stream (a whole number of 64-byte blocks) to
  /// mono 16-bit signed PCM. Each block yields 256 samples. A ragged tail (length
  /// not a multiple of 64) is rejected by returning the samples decoded from the
  /// whole blocks only. <paramref name="sampleRate"/> only labels the output and
  /// does not affect decoding.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> blocks, int sampleRate) {
    _ = sampleRate; // label only
    var blockCount = blocks.Length / BlockLen;
    if (blockCount <= 0)
      return [];

    var state = new DecodeState();
    var output = new short[Samples * blockCount];
    var floats = new float[Samples];
    var outPos = 0;
    for (var b = 0; b < blockCount; ++b) {
      DecodeBlock(state, blocks.Slice(b * BlockLen, BlockLen), floats);
      for (var i = 0; i < Samples; ++i) {
        // FFmpeg outputs AV_SAMPLE_FMT_FLT; convert to 16-bit with rounding + clip.
        var v = (int)MathF.Round(floats[i] * 32768.0f);
        output[outPos++] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
      }
    }
    return output;
  }

  private sealed class DecodeState {
    public readonly NellymoserLfg Random = new(0);
    public readonly float[] ImdctPrev = new float[BufLen];
    public readonly float[] ImdctOut = new float[BufLen];
  }

  private static void DecodeBlock(DecodeState s, ReadOnlySpan<byte> block, float[] audio) {
    var buf = new float[FillLen];
    var pows = new float[FillLen];
    var bits = new int[BufLen];

    var gb = new NellymoserBitReader(block);

    var ptr = 0;
    var val = NellymoserTables.InitTable[gb.GetBits(6)];
    var fval = (float)val;
    for (var i = 0; i < Bands; ++i) {
      if (i > 0)
        fval += NellymoserTables.DeltaTable[gb.GetBits(5)];
      var pval = -MathF.Pow(2.0f, fval / 2048.0f) * ScaleBias;
      for (var j = 0; j < NellymoserTables.BandSizes[i]; ++j) {
        buf[ptr] = fval;
        pows[ptr] = pval;
        ++ptr;
      }
    }

    GetSampleBits(buf, bits);

    for (var i = 0; i < 2; ++i) {
      var aptr = audio.AsSpan(i * BufLen, BufLen);

      gb = new NellymoserBitReader(block);
      gb.SkipBits(HeaderBits + i * DetailBits);

      for (var j = 0; j < FillLen; ++j) {
        if (bits[j] <= 0) {
          aptr[j] = (float)(Math.Sqrt(0.5) ) * pows[j];
          if ((s.Random.Get() & 1) != 0)
            aptr[j] *= -1.0f;
        } else {
          var v = (int)gb.GetBits(bits[j]);
          aptr[j] = NellymoserTables.DequantizationTable[(1 << bits[j]) - 1 + v] * pows[j];
        }
      }
      for (var j = FillLen; j < BufLen; ++j)
        aptr[j] = 0.0f;

      ImdctHalf(aptr, s.ImdctOut);
      // vector_fmul_window(aptr, imdct_prev + BUF_LEN/2, imdct_out, sine128, BUF_LEN/2)
      VectorFmulWindow(aptr, s.ImdctPrev.AsSpan(BufLen / 2), s.ImdctOut, Sine128, BufLen / 2);
      // FFSWAP(imdct_out, imdct_prev): keep prev = the half just produced.
      s.ImdctOut.AsSpan(0, BufLen).CopyTo(s.ImdctPrev);
    }
  }

  // ── vector_fmul_window (float_dsp.c) ─────────────────────────────────────────
  // dst[len+i]=s0*wj-s1*wi ; dst[len+j]=s0*wi+s1*wj over i=-len..-1, j=len-1..0
  // re-expressed with non-negative indexing into the BUF_LEN-sized arrays.
  private static void VectorFmulWindow(Span<float> dst, ReadOnlySpan<float> src0,
      ReadOnlySpan<float> src1, ReadOnlySpan<float> win, int len) {
    for (var i = 0; i < len; ++i) {
      var j = len - 1 - i;
      var s0 = src0[i];
      var s1 = src1[j];
      var wi = win[i];
      var wj = win[j];
      dst[i] = s0 * wj - s1 * wi;
      dst[len + j] = s0 * wi + s1 * wj;
    }
  }

  // ── inverse MDCT (direct O(n^2), size-256 transform, imdct_half slice) ───────
  // imdct_half writes output[k] = fullImdct[n4 + k] for k in [0, n2), with
  // n = 256, n2 = 128, n4 = 64. The full IMDCT uses FFmpeg's MDCT phase:
  //   full[i] = sum_{k=0..n2-1} input[k] * cos( (pi/(2n)) * (2i+1+n2) * (2k+1) ).
  private static void ImdctHalf(ReadOnlySpan<float> input, float[] output) {
    const int n = 2 * BufLen;   // 256
    const int n2 = BufLen;      // 128
    const int n4 = BufLen / 2;  // 64
    var scale = Math.PI / (2.0 * n);
    for (var k = 0; k < n2; ++k) {
      var i = n4 + k; // index into the full IMDCT
      var phase = (2 * i + 1 + n2) * scale;
      var acc = 0.0;
      for (var m = 0; m < n2; ++m)
        acc += input[m] * Math.Cos(phase * (2 * m + 1));
      output[k] = (float)acc;
    }
  }

  // ── bit allocation (nellymoser.c) ───────────────────────────────────────────

  private static int Headroom(ref int la) {
    if (la == 0)
      return 31;
    var l = 30 - Log2(Math.Abs(la));
    la *= 1 << l;
    return l;
  }

  /// <summary>av_log2: index of the most-significant set bit (0 for input 0).</summary>
  private static int Log2(int v) {
    var r = 0;
    var u = (uint)v;
    while ((u >>= 1) != 0)
      ++r;
    return r;
  }

  private static int SignedShift(int value, int shift)
    => shift >= 0 ? value << shift : value >> -shift;

  private static int SumBits(short[] buf, int shift, int off) {
    var ret = 0;
    for (var i = 0; i < FillLen; ++i) {
      var b = buf[i] - off;
      b = ((b >> (shift - 1)) + 1) >> 1;
      ret += Math.Clamp(b, 0, BitCap);
    }
    return ret;
  }

  private static void GetSampleBits(float[] buf, int[] bits) {
    var sbuf = new short[BufLen];

    var maxf = 0.0f;
    for (var i = 0; i < FillLen; ++i)
      maxf = Math.Max(maxf, buf[i]);
    var max = (int)maxf;

    var shift = -16;
    shift += Headroom(ref max);

    var sum = 0;
    for (var i = 0; i < FillLen; ++i) {
      sbuf[i] = (short)SignedShift((int)buf[i], shift);
      sbuf[i] = (short)((3 * sbuf[i]) >> 2);
      sum += sbuf[i];
    }

    shift += 11;
    var shiftSaved = shift;
    sum -= DetailBits << shift;
    shift += Headroom(ref sum);
    var smallOff = (BaseOff * (sum >> 16)) >> 15;
    shift = shiftSaved - (BaseShift + shift - 31);

    smallOff = SignedShift(smallOff, shift);

    var bitsum = SumBits(sbuf, shiftSaved, smallOff);

    var j = 0;
    if (bitsum != DetailBits) {
      var off = bitsum - DetailBits;

      for (shift = 0; Math.Abs(off) <= 16383; ++shift)
        off *= 2;

      off = (off * BaseOff) >> 15;
      shift = shiftSaved - (BaseShift + shift - 15);

      off = SignedShift(off, shift);

      var lastOff = smallOff;
      var lastBitsum = bitsum;
      for (j = 1; j < 20; ++j) {
        lastOff = smallOff;
        smallOff += off;
        lastBitsum = bitsum;

        bitsum = SumBits(sbuf, shiftSaved, smallOff);

        if ((bitsum - DetailBits) * (lastBitsum - DetailBits) <= 0)
          break;
      }

      int bigOff, bigBitsum, smallBitsum;
      if (bitsum > DetailBits) {
        bigOff = smallOff;
        smallOff = lastOff;
        bigBitsum = bitsum;
        smallBitsum = lastBitsum;
      } else {
        bigOff = lastOff;
        bigBitsum = lastBitsum;
        smallBitsum = bitsum;
      }

      while (bitsum != DetailBits && j <= 19) {
        off = (bigOff + smallOff) >> 1;
        bitsum = SumBits(sbuf, shiftSaved, off);
        if (bitsum > DetailBits) {
          bigOff = off;
          bigBitsum = bitsum;
        } else {
          smallOff = off;
          smallBitsum = bitsum;
        }
        ++j;
      }

      if (Math.Abs(bigBitsum - DetailBits) >= Math.Abs(smallBitsum - DetailBits))
        bitsum = smallBitsum;
      else {
        smallOff = bigOff;
        bitsum = bigBitsum;
      }
    }

    int idx;
    for (idx = 0; idx < FillLen; ++idx) {
      var tmp = sbuf[idx] - smallOff;
      tmp = ((tmp >> (shiftSaved - 1)) + 1) >> 1;
      bits[idx] = Math.Clamp(tmp, 0, BitCap);
    }

    if (bitsum > DetailBits) {
      var tmp = 0;
      idx = 0;
      while (tmp < DetailBits) {
        tmp += bits[idx];
        ++idx;
      }

      bits[idx - 1] -= tmp - DetailBits;
      for (; idx < FillLen; ++idx)
        bits[idx] = 0;
    }
  }
}

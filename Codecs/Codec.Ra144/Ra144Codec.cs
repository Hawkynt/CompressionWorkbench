#pragma warning disable CS1591
namespace Codec.Ra144;

/// <summary>
/// RealAudio 1.0 "14_4"/lpcJ decoder — a faithful port of FFmpeg's
/// <c>libavcodec/ra144.c</c> + <c>ra144dec.c</c>. The codec is a 14.4 kbit/s CELP-style
/// speech coder: every 20-byte block decodes to 160 signed 16-bit samples at 8000 Hz mono.
/// <para>Per block the bitstream carries ten LPC reflection-coefficient indices (bit
/// widths {6,5,5,4,4,3,3,3,3,2}), a 5-bit frame energy, then four subframes of
/// {7-bit adaptive-codebook lag, 8-bit gain, 7-bit fixed-codebook-1 index, 7-bit
/// fixed-codebook-2 index}. Synthesis interpolates the LPC coefficients across the four
/// subframes, builds an excitation from the adaptive codebook plus the two fixed
/// codebooks scaled by the gain/energy tables, then runs the LPC synthesis filter.</para>
/// <para>Decode-only — there is no encoder. State (adaptive codebook, previous-frame LPC
/// coefficients, previous energy) is carried across blocks exactly as the reference does,
/// so a multi-block buffer decodes identically to feeding the blocks one at a time.</para>
/// </summary>
public static class Ra144Codec {

  private const int NBlocks = 4;       // subblocks per block
  private const int BlockSize = 40;    // subblock size in samples
  private const int BufferSize = 146;  // adaptive-codebook size
  private const int FrameSize = 20;    // encoded block size in bytes
  private const int LpcOrder = 10;

  // Reflection-coefficient bit widths, one per LPC coefficient (ra144dec.c "sizes").
  private static readonly int[] ReflSizes = [6, 5, 5, 4, 4, 3, 3, 3, 3, 2];

  /// <summary>
  /// Decodes back-to-back 20-byte lpcJ blocks to interleaved (mono) 16-bit PCM. A ragged
  /// tail shorter than a full block is ignored. Output length is
  /// <c>(input.Length / 20) * 160</c> samples.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> blocks) {
    var frames = blocks.Length / FrameSize;
    if (frames == 0)
      return [];

    var ctx = new Context();
    var output = new short[frames * NBlocks * BlockSize];
    var outPos = 0;

    for (var f = 0; f < frames; ++f) {
      var block = blocks.Slice(f * FrameSize, FrameSize);
      DecodeBlock(ctx, block, output, outPos);
      outPos += NBlocks * BlockSize;
    }
    return output;
  }

  /// <summary>Mutable per-stream decoder state (mirrors <c>RA144Context</c>).</summary>
  private sealed class Context {
    public uint OldEnergy;
    // lpc_coef[0] = current frame, lpc_coef[1] = previous frame.
    public readonly int[][] LpcCoef = [new int[LpcOrder], new int[LpcOrder]];
    public readonly uint[] LpcReflRms = [0, 0];
    // Adaptive codebook (+2 guard, matching the reference's [146+2]).
    public readonly short[] AdaptCb = new short[BufferSize + 2];
    // Current subblock padded by the previous subblock's last 10 values.
    public readonly short[] CurrSblock = new short[50];
    public readonly short[] BufferA = new short[BlockSize];
  }

  private static void DecodeBlock(Context ctx, ReadOnlySpan<byte> buf, short[] samples, int outPos) {
    var gb = new BitReader(buf);

    var lpcRefl = new int[LpcOrder];
    for (var i = 0; i < LpcOrder; ++i)
      lpcRefl[i] = Ra144Tables.LpcReflCb[i][gb.GetBits(ReflSizes[i])];

    EvalCoefs(ctx.LpcCoef[0], lpcRefl);
    ctx.LpcReflRms[0] = Rms(lpcRefl);

    var energy = (uint)Ra144Tables.EnergyTab[gb.GetBits(5)];

    var blockCoefs = new short[NBlocks][];
    for (var i = 0; i < NBlocks; ++i) blockCoefs[i] = new short[LpcOrder];
    var reflRms = new uint[NBlocks];

    reflRms[0] = Interp(ctx, blockCoefs[0], 1, 1, ctx.OldEnergy);
    reflRms[1] = Interp(ctx, blockCoefs[1], 2,
      energy <= ctx.OldEnergy ? 1 : 0,
      (uint)(TSqrt(energy * ctx.OldEnergy) >> 12));
    reflRms[2] = Interp(ctx, blockCoefs[2], 3, 0, energy);
    reflRms[3] = RescaleRms(ctx.LpcReflRms[0], energy);

    IntToInt16(blockCoefs[3], ctx.LpcCoef[0]);

    for (var i = 0; i < NBlocks; ++i) {
      // Subframe bitstream: 7b adaptive-cb lag, 8b gain, 7b cb1 idx, 7b cb2 idx.
      var cbaIdx = gb.GetBits(7);
      var gain = gb.GetBits(8);
      var cb1Idx = gb.GetBits(7);
      var cb2Idx = gb.GetBits(7);

      SubblockSynthesis(ctx, blockCoefs[i], cbaIdx, cb1Idx, cb2Idx, (int)reflRms[i], gain);

      for (var j = 0; j < BlockSize; ++j)
        samples[outPos++] = (short)ClipInt16(ctx.CurrSblock[j + 10] * (1 << 2));
    }

    ctx.OldEnergy = energy;
    ctx.LpcReflRms[1] = ctx.LpcReflRms[0];
    (ctx.LpcCoef[0], ctx.LpcCoef[1]) = (ctx.LpcCoef[1], ctx.LpcCoef[0]);
  }

  // ── synthesis (faithful port of ra144.c) ─────────────────────────────────────

  private static void SubblockSynthesis(Context ctx, short[] lpcCoefs,
    int cbaIdx, int cb1Idx, int cb2Idx, int gval, int gain) {
    var m = new int[3];

    if (cbaIdx != 0) {
      cbaIdx += BlockSize / 2 - 1;
      CopyAndDup(ctx.BufferA, ctx.AdaptCb, cbaIdx);
      m[0] = (int)((uint)Irms(ctx.BufferA) * (uint)gval >> 12);
    } else {
      m[0] = 0;
    }
    m[1] = (Ra144Tables.Cb1Base[cb1Idx] * gval) >> 8;
    m[2] = (Ra144Tables.Cb2Base[cb2Idx] * gval) >> 8;

    // memmove adapt_cb left by one subblock.
    Array.Copy(ctx.AdaptCb, BlockSize, ctx.AdaptCb, 0, BufferSize - BlockSize);

    var blockStart = BufferSize - BlockSize;
    AddWav(ctx.AdaptCb, blockStart, gain, cbaIdx, m,
      cbaIdx != 0 ? ctx.BufferA : null,
      Ra144Tables.Cb1Vects[cb1Idx], Ra144Tables.Cb2Vects[cb2Idx]);

    // Carry the last 10 samples of the previous subblock into the padding region.
    Array.Copy(ctx.CurrSblock, BlockSize, ctx.CurrSblock, 0, LpcOrder);

    if (CelpLpSynthesisFilter(ctx.CurrSblock, LpcOrder, lpcCoefs,
        ctx.AdaptCb, blockStart, BlockSize, LpcOrder))
      Array.Clear(ctx.CurrSblock, 0, LpcOrder + BlockSize);
  }

  private static void AddWav(short[] dest, int destOff, int n, int skipFirst, int[] m,
    short[]? s1, sbyte[] s2, sbyte[] s3) {
    var v = new int[3];
    v[0] = 0;
    for (var i = skipFirst != 0 ? 1 : 0; i < 3; ++i)
      v[i] = (int)((uint)Ra144Tables.GainValTab[n][i] * (uint)m[i] >> Ra144Tables.GainExpTab[n]);

    if (v[0] != 0) {
      for (var i = 0; i < BlockSize; ++i)
        dest[destOff + i] = (short)((int)((uint)s1![i] * (uint)v[0] + (uint)(s2[i] * v[1]) + (uint)(s3[i] * v[2])) >> 12);
    } else {
      for (var i = 0; i < BlockSize; ++i)
        dest[destOff + i] = (short)((s2[i] * v[1] + s3[i] * v[2]) >> 12);
    }
  }

  /// <summary>
  /// Copy the last <paramref name="offset"/> values of <paramref name="source"/> to
  /// <paramref name="target"/>, repeating them if they don't fill the block.
  /// </summary>
  private static void CopyAndDup(short[] target, short[] source, int offset) {
    var srcStart = BufferSize - offset;
    var first = Math.Min(BlockSize, offset);
    Array.Copy(source, srcStart, target, 0, first);
    if (offset < BlockSize)
      Array.Copy(source, srcStart, target, offset, BlockSize - offset);
  }

  /// <summary>Evaluate LPC filter coefficients from reflection coefficients.</summary>
  private static void EvalCoefs(int[] coefs, int[] refl) {
    var buffer = new int[LpcOrder];
    var b1 = buffer;
    var b2 = coefs;

    for (var i = 0; i < LpcOrder; ++i) {
      b1[i] = refl[i] * 16;
      for (var j = 0; j < i; ++j)
        b1[j] = (int)((int)(refl[i] * (uint)b2[i - j - 1]) >> 12) + b2[j];
      (b1, b2) = (b2, b1);
    }

    // After the swaps, b2 holds the final coefficients; copy into the caller's array.
    for (var i = 0; i < LpcOrder; ++i)
      coefs[i] = b2[i] >> 4;
  }

  /// <summary>
  /// Evaluate the reflection coefficients from the filter coefficients. Returns true if
  /// any reflection coefficient is out of range (signalling an unstable filter).
  /// </summary>
  private static bool EvalRefl(int[] refl, short[] coefs) {
    var buffer1 = new int[LpcOrder];
    var buffer2 = new int[LpcOrder];
    var bp1 = buffer1;
    var bp2 = buffer2;

    for (var i = 0; i < LpcOrder; ++i)
      buffer2[i] = coefs[i];

    refl[LpcOrder - 1] = bp2[LpcOrder - 1];

    if ((uint)bp2[LpcOrder - 1] + 0x1000 > 0x1fff)
      return true;

    for (var i = LpcOrder - 2; i >= 0; --i) {
      var b = 0x1000 - ((bp2[i + 1] * bp2[i + 1]) >> 12);
      if (b == 0) b = -2;
      b = 0x1000000 / b;
      for (var j = 0; j <= i; ++j)
        bp1[j] = (int)((bp2[j] - ((int)(refl[i + 1] * (uint)bp2[i - j]) >> 12)) * (uint)b) >> 12;

      if ((uint)bp1[i] + 0x1000 > 0x1fff)
        return true;

      refl[i] = bp1[i];
      (bp1, bp2) = (bp2, bp1);
    }
    return false;
  }

  private static void IntToInt16(short[] outp, int[] inp) {
    for (var i = 0; i < LpcOrder; ++i)
      outp[i] = (short)inp[i];
  }

  private static uint Rms(int[] data) {
    var res = 0x10000u;
    var b = LpcOrder;
    for (var i = 0; i < LpcOrder; ++i) {
      res = (uint)(((0x1000000 - data[i] * data[i]) >> 12) * (long)res >> 12);
      if (res == 0) return 0;
      while (res <= 0x3fff) { b++; res <<= 2; }
    }
    return (uint)(TSqrt(res) >> b);
  }

  private static uint Interp(Context ctx, short[] outp, int a, int copyOld, uint energy) {
    var work = new int[LpcOrder];
    var b = NBlocks - a;

    for (var i = 0; i < LpcOrder; ++i)
      outp[i] = (short)((a * ctx.LpcCoef[0][i] + b * ctx.LpcCoef[1][i]) >> 2);

    if (EvalRefl(work, outp)) {
      IntToInt16(outp, ctx.LpcCoef[copyOld]);
      return RescaleRms(ctx.LpcReflRms[copyOld], energy);
    }
    return RescaleRms(Rms(work), energy);
  }

  private static uint RescaleRms(uint rms, uint energy) => (rms * energy) >> 10;

  /// <summary>Inverse root mean square of a 40-sample subblock.</summary>
  private static int Irms(short[] data) {
    var sum = 0u;
    for (var i = 0; i < BlockSize; ++i)
      sum += (uint)(data[i] * data[i]);
    if (sum == 0) return 0;
    return (int)(0x20000000 / (uint)(TSqrt(sum) >> 8));
  }

  /// <summary>Evaluate sqrt(x &lt;&lt; 24); x must fit in 20 bits. Mirrors <c>ff_t_sqrt</c>.</summary>
  private static int TSqrt(uint x) {
    var s = 2;
    while (x > 0xfff) { s++; x >>= 2; }
    return (int)(FfSqrt(x << 20) << s);
  }

  /// <summary>Integer square root via <c>ff_sqrt_tab</c> (mathtables.c).</summary>
  private static uint FfSqrt(uint a) {
    uint b;
    if (a < 255) return (uint)((Ra144Tables.SqrtTab[a + 1] - 1) >> 4);
    if (a < (1 << 12)) b = (uint)(Ra144Tables.SqrtTab[a >> 4] >> 2);
    else if (a < (1 << 14)) b = (uint)(Ra144Tables.SqrtTab[a >> 6] >> 1);
    else if (a < (1 << 16)) b = Ra144Tables.SqrtTab[a >> 8];
    else {
      var s = Log2_16Bit(a >> 16) >> 1;
      var c = a >> (s + 2);
      b = Ra144Tables.SqrtTab[c >> (s + 8)];
      b = c / b + (b << s);
    }
    return b - (a < b * b ? 1u : 0u);
  }

  private static int Log2_16Bit(uint v) {
    var n = 0;
    if ((v & 0xff00) != 0) { v >>= 8; n += 8; }
    if ((v & 0xf0) != 0) { v >>= 4; n += 4; }
    if ((v & 0xc) != 0) { v >>= 2; n += 2; }
    if ((v & 0x2) != 0) n += 1;
    return n;
  }

  /// <summary>
  /// CELP LPC synthesis filter (port of <c>ff_celp_lp_synthesis_filter</c> with
  /// <c>shift = 0</c>, <c>rounder = 0xfff</c>, stop-on-overflow). Writes into
  /// <paramref name="outBuf"/> starting at <paramref name="outOff"/> (which has
  /// <see cref="LpcOrder"/> history values preceding it). Returns true on overflow.
  /// </summary>
  private static bool CelpLpSynthesisFilter(short[] outBuf, int outOff, short[] filterCoeffs,
    short[] inBuf, int inOff, int bufferLength, int filterLength) {
    const int rounder = 0xfff;
    for (var n = 0; n < bufferLength; ++n) {
      var sum = (uint)rounder;
      for (var i = 1; i <= filterLength; ++i)
        sum -= (uint)(filterCoeffs[i - 1] * outBuf[outOff + n - i]);

      var sum1 = ((int)sum >> 12) + inBuf[inOff + n];
      var clipped = ClipInt16(sum1);
      if (clipped != sum1)
        return true;
      outBuf[outOff + n] = (short)clipped;
    }
    return false;
  }

  private static int ClipInt16(int v) => v switch {
    > short.MaxValue => short.MaxValue,
    < short.MinValue => short.MinValue,
    _ => v,
  };

  /// <summary>Big-endian MSB-first bit reader matching FFmpeg's <c>get_bits</c>.</summary>
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
  }
}

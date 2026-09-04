// Converted from the GSM 06.10 RPE-LTP reference implementation:
//
//   Copyright 1992, 1993, 1994 by Jutta Degener and Carsten Bormann,
//   Technische Universitaet Berlin
//
//   Any use of this software is permitted provided that this notice is not
//   removed and that neither the authors nor the Technische Universitaet Berlin
//   are deemed to have made any representations as to the suitability of this
//   software for any purpose nor are held responsible for any defects of
//   this software.  THERE IS ABSOLUTELY NO WARRANTY FOR THIS SOFTWARE.
//
//   As a matter of courtesy, the authors request to be informed about uses
//   this software has found, about bugs in this software, and about any
//   improvements that may be of general interest.
//
//   Berlin, 28.11.1994
//   Jutta Degener
//   Carsten Bormann

namespace Codec.Gsm610;

/// <summary>
/// GSM 06.10 full-rate speech codec (ETSI EN 300 961), RPE-LTP at 13 kbit/s.
/// <para>
/// Each 33-byte frame carries 160 × 16-bit PCM samples at 8 kHz. Both directions are the
/// bit-exact fixed-point algorithm of the specification, converted from the reference
/// implementation by Jutta Degener and Carsten Bormann (Technische Universität Berlin,
/// 1992–1994; the notice their licence requires be kept is at the top of this file):
/// pre-processing, LPC analysis with the Schur recursion, short-term lattice filters with
/// LAR interpolation, long-term prediction, RPE grid selection and APCM quantisation, and
/// the de-emphasis post-processing. Streams interoperate with libgsm / toast and ffmpeg.
/// </para>
/// <para>
/// The on-disk frame is the "toast"/<c>.gsm</c> layout: signature nibble <c>0xD</c>, then
/// the 260 parameter bits packed MSB-first. The WAV49 65-byte double-frame variant used by
/// Microsoft's WAVE tag 0x0031 is unpacked by the container reader before reaching this codec.
/// </para>
/// </summary>
public static partial class Gsm610Codec {

  /// <summary>Size of one encoded GSM 06.10 frame in bytes.</summary>
  public const int FrameBytes = 33;

  /// <summary>Number of PCM samples produced per decoded frame.</summary>
  public const int FrameSamples = 160;

  /// <summary>
  /// Decodes a buffer of GSM 06.10 frames to interleaved 16-bit PCM.
  /// </summary>
  /// <param name="gsm">Concatenated 33-byte frames, one per channel per frame-group.</param>
  /// <param name="channels">Number of interleaved channels (1 or 2 typical).</param>
  public static short[] Decode(ReadOnlySpan<byte> gsm, int channels) {
    if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
    if (gsm.Length % (FrameBytes * channels) != 0)
      throw new ArgumentException("GSM 06.10 input is not a whole number of frame-groups.", nameof(gsm));

    var groupCount = gsm.Length / (FrameBytes * channels);
    var pcm = new short[groupCount * FrameSamples * channels];
    var decoders = new Gsm610State[channels];
    for (var c = 0; c < channels; ++c) decoders[c] = new Gsm610State();

    Span<short> frameOut = stackalloc short[FrameSamples];
    for (var g = 0; g < groupCount; ++g) {
      for (var c = 0; c < channels; ++c) {
        var off = (g * channels + c) * FrameBytes;
        decoders[c].DecodeFrame(gsm.Slice(off, FrameBytes), frameOut);
        for (var i = 0; i < FrameSamples; ++i)
          pcm[(g * FrameSamples + i) * channels + c] = frameOut[i];
      }
    }
    return pcm;
  }

  /// <summary>
  /// Decodes a buffer of raw 33-byte GSM 06.10 frames (single mono stream) to 16-bit
  /// PCM. This is the "toast"/<c>.gsm</c> on-disk layout — a bare concatenation of
  /// frames with no container. Each frame's first byte carries the signature nibble
  /// <c>0xD</c> in its high four bits (the magic byte ranges <c>0xD0..0xDF</c>).
  /// </summary>
  /// <param name="gsm">Concatenated 33-byte frames.</param>
  public static short[] DecodeRaw(ReadOnlySpan<byte> gsm) => Decode(gsm, channels: 1);

  /// <summary>
  /// Reports whether <paramref name="gsm"/> is a whole number of 33-byte frames whose
  /// per-frame signature nibbles are all <c>0xD</c> — the cheap structural check a
  /// headerless <c>.gsm</c> reader uses before committing to a decode.
  /// </summary>
  public static bool LooksLikeRawFrames(ReadOnlySpan<byte> gsm) {
    if (gsm.Length == 0 || gsm.Length % FrameBytes != 0)
      return false;
    for (var off = 0; off < gsm.Length; off += FrameBytes)
      if ((gsm[off] & 0xF0) != 0xD0)
        return false;
    return true;
  }

  /// <summary>MSB-first bit reader over one frame.</summary>
  private ref struct BitReader {
    private readonly ReadOnlySpan<byte> _buf;
    private int _bitPos;
    public BitReader(ReadOnlySpan<byte> buf) { this._buf = buf; this._bitPos = 0; }
    public int Read(int bits) {
      var v = 0;
      for (var i = 0; i < bits; ++i) {
        var bit = (this._buf[this._bitPos >> 3] >> (7 - (this._bitPos & 7))) & 1;
        v = (v << 1) | bit;
        this._bitPos++;
      }
      return v;
    }
  }

  /// <summary>
  /// The 260 parameters of one frame: LARc[8], and per sub-segment Nc, bc, Mc, xmaxc and
  /// xMc[13]. Bit widths follow table 1.1 of the specification.
  /// </summary>
  private static void UnpackFrame(ReadOnlySpan<byte> frame, Span<short> larc, Span<short> nc, Span<short> bc,
    Span<short> mc, Span<short> xmaxc, Span<short> xmc) {
    var br = new BitReader(frame);
    var signature = br.Read(4);
    if (signature != 0xD)
      throw new InvalidDataException($"Invalid GSM 06.10 frame signature 0x{signature:X1}; expected 0xD.");
    larc[0] = (short)br.Read(6);
    larc[1] = (short)br.Read(6);
    larc[2] = (short)br.Read(5);
    larc[3] = (short)br.Read(5);
    larc[4] = (short)br.Read(4);
    larc[5] = (short)br.Read(4);
    larc[6] = (short)br.Read(3);
    larc[7] = (short)br.Read(3);
    for (var k = 0; k < 4; ++k) {
      nc[k] = (short)br.Read(7);
      bc[k] = (short)br.Read(2);
      mc[k] = (short)br.Read(2);
      xmaxc[k] = (short)br.Read(6);
      for (var i = 0; i < 13; ++i)
        xmc[k * 13 + i] = (short)br.Read(3);
    }
  }

  private static void PackFrame(Span<byte> frame, ReadOnlySpan<short> larc, ReadOnlySpan<short> nc,
    ReadOnlySpan<short> bc, ReadOnlySpan<short> mc, ReadOnlySpan<short> xmaxc, ReadOnlySpan<short> xmc) {
    frame.Clear();
    var bw = new BitWriter(frame);
    bw.Write(0xD, 4);
    bw.Write(larc[0], 6);
    bw.Write(larc[1], 6);
    bw.Write(larc[2], 5);
    bw.Write(larc[3], 5);
    bw.Write(larc[4], 4);
    bw.Write(larc[5], 4);
    bw.Write(larc[6], 3);
    bw.Write(larc[7], 3);
    for (var k = 0; k < 4; ++k) {
      bw.Write(nc[k], 7);
      bw.Write(bc[k], 2);
      bw.Write(mc[k], 2);
      bw.Write(xmaxc[k], 6);
      for (var i = 0; i < 13; ++i)
        bw.Write(xmc[k * 13 + i], 3);
    }
  }

  /// <summary>
  /// Saturating 16/32-bit arithmetic of the specification (section 4.1 / add.c).
  /// </summary>
  private static class Arith {
    public const short MinWord = short.MinValue;
    public const short MaxWord = short.MaxValue;

    public static short Add(int a, int b) => Saturate(a + b);
    public static short Sub(int a, int b) => Saturate(a - b);
    public static short Saturate(int x) => x > MaxWord ? MaxWord : x < MinWord ? MinWord : (short)x;

    /// <summary>Signed 16-bit multiply with the result scaled down by 15 bits.</summary>
    public static short Mult(short a, short b)
      => a == MinWord && b == MinWord ? MaxWord : (short)((a * b) >> 15);

    /// <summary>Rounded signed 16-bit multiply with the result scaled down by 15 bits.</summary>
    public static short MultR(short a, short b)
      => a == MinWord && b == MinWord ? MaxWord : (short)((a * b + 16384) >> 15);

    public static short Abs(short a) => a < 0 ? a == MinWord ? MaxWord : (short)-a : a;

    public static int LAdd(int a, int b) {
      var sum = (long)a + b;
      return sum > int.MaxValue ? int.MaxValue : sum < int.MinValue ? int.MinValue : (int)sum;
    }

    /// <summary>Number of left shifts that normalises a non-zero 32-bit value.</summary>
    public static short Norm(int a) {
      if (a < 0) {
        if (a <= -1073741824) return 0;
        a = ~a;
      }
      return (short)(System.Numerics.BitOperations.LeadingZeroCount((uint)a) - 1);
    }

    public static short Asl(short a, int n) {
      if (n >= 16) return 0;
      if (n <= -16) return (short)(a < 0 ? -1 : 0);
      if (n < 0) return Asr(a, -n);
      return (short)(a << n);
    }

    public static short Asr(short a, int n) {
      if (n >= 16) return (short)(a < 0 ? -1 : 0);
      if (n <= -16) return 0;
      if (n < 0) return (short)(a << -n);
      return (short)(a >> n);
    }

    /// <summary>Integer division of the specification (4.2.5), <c>denum &gt;= num &gt;= 0</c>.</summary>
    public static short Div(short num, short denum) {
      if (num == 0) return 0;
      int lNum = num, lDenum = denum;
      short div = 0;
      for (var k = 15; k-- > 0;) {
        div <<= 1;
        lNum <<= 1;
        if (lNum >= lDenum) {
          lNum -= lDenum;
          div++;
        }
      }
      return div;
    }
  }

  /// <summary>Tables 4.1–4.6 of the specification.</summary>
  private static class Tables {
    public static readonly short[] A = [20480, 20480, 20480, 20480, 13964, 15360, 8534, 9036];
    public static readonly short[] B = [0, 0, 2048, -2560, 94, -1792, -341, -1144];
    public static readonly short[] Mic = [-32, -32, -16, -16, -8, -8, -4, -4];
    public static readonly short[] Mac = [31, 31, 15, 15, 7, 7, 3, 3];
    public static readonly short[] InvA = [13107, 13107, 13107, 13107, 19223, 17476, 31454, 29708];
    public static readonly short[] Dlb = [6554, 16384, 26214, 32767];
    public static readonly short[] Qlb = [3277, 11469, 21299, 32767];
    public static readonly short[] H = [-134, -374, 0, 2054, 5741, 8192, 5741, 2054, 0, -374, -134];
    public static readonly short[] NrFac = [29128, 26215, 23832, 21846, 20165, 18725, 17476, 16384];
    public static readonly short[] Fac = [18431, 20479, 22527, 24575, 26623, 28671, 30719, 32767];
  }

  /// <summary>
  /// Per-stream codec state (struct gsm_state). One instance serves either direction; the
  /// encoder and decoder halves of the specification share the reconstructed short-term
  /// residual history <c>dp0</c>, the LAR interpolation memory and the lattice filter taps.
  /// </summary>
  private sealed class Gsm610State {
    private readonly short[] _dp0 = new short[280];
    private readonly short[] _e = new short[50];
    private short _z1;
    private int _lz2;
    private short _mp;
    private readonly short[] _u = new short[8];
    private readonly short[][] _larpp = [new short[8], new short[8]];
    private int _j;
    private short _nrp = 40;
    private readonly short[] _v = new short[9];
    private short _msr;

    // ── 4.3 decoder ──────────────────────────────────────────────────────────────

    public void DecodeFrame(ReadOnlySpan<byte> frame, Span<short> pcm) {
      if (frame.Length != FrameBytes)
        throw new ArgumentException($"A GSM 06.10 frame must contain exactly {FrameBytes} bytes.", nameof(frame));
      Span<short> larc = stackalloc short[8];
      Span<short> nc = stackalloc short[4];
      Span<short> bc = stackalloc short[4];
      Span<short> mc = stackalloc short[4];
      Span<short> xmaxc = stackalloc short[4];
      Span<short> xmc = stackalloc short[52];
      UnpackFrame(frame, larc, nc, bc, mc, xmaxc, xmc);

      Span<short> erp = stackalloc short[40];
      Span<short> wt = stackalloc short[160];
      for (var j = 0; j < 4; ++j) {
        RpeDecoding(xmaxc[j], mc[j], xmc.Slice(j * 13, 13), erp);
        this.LongTermSynthesisFiltering(nc[j], bc[j], erp);
        for (var k = 0; k < 40; ++k) wt[j * 40 + k] = this._dp0[120 + k];
      }
      this.ShortTermSynthesisFilter(larc, wt, pcm);
      this.Postprocessing(pcm);
    }

    private void Postprocessing(Span<short> s) {
      var msr = this._msr;
      for (var k = 0; k < 160; ++k) {
        var tmp = Arith.MultR(msr, 28180);
        msr = Arith.Add(s[k], tmp);                         // de-emphasis
        s[k] = (short)(Arith.Add(msr, msr) & 0xFFF8);        // truncation and upscaling
      }
      this._msr = msr;
    }

    // ── 4.2 encoder ──────────────────────────────────────────────────────────────

    public void EncodeFrame(ReadOnlySpan<short> pcm, Span<byte> frame) {
      Span<short> larc = stackalloc short[8];
      Span<short> nc = stackalloc short[4];
      Span<short> bc = stackalloc short[4];
      Span<short> mc = stackalloc short[4];
      Span<short> xmaxc = stackalloc short[4];
      Span<short> xmc = stackalloc short[52];
      Span<short> so = stackalloc short[160];

      this.Preprocess(pcm, so);
      this.LpcAnalysis(so, larc);
      this.ShortTermAnalysisFilter(larc, so);

      var e = this._e.AsSpan(5, 40);
      Span<short> dpp = stackalloc short[40];
      for (var k = 0; k < 4; ++k) {
        var dpOffset = 120 + k * 40;
        this.LongTermPredictor(so.Slice(k * 40, 40), dpOffset, e, dpp, out nc[k], out bc[k]);
        this.RpeEncoding(out xmaxc[k], out mc[k], xmc.Slice(k * 13, 13));
        // 4.2.18: update of the reconstructed short-term residual signal.
        for (var i = 0; i < 40; ++i)
          this._dp0[dpOffset + i] = Arith.Add(e[i], dpp[i]);
      }
      Array.Copy(this._dp0, 160, this._dp0, 0, 120);

      PackFrame(frame, larc, nc, bc, mc, xmaxc, xmc);
    }

    // 4.2.0 – 4.2.3: downscaling, offset compensation, pre-emphasis.
    private void Preprocess(ReadOnlySpan<short> s, Span<short> so) {
      var z1 = this._z1;
      var lz2 = this._lz2;
      var mp = this._mp;
      for (var k = 0; k < 160; ++k) {
        var downscaled = (short)((s[k] >> 3) << 2);

        // Offset compensation (high-pass), non-recursive part.
        var s1 = (short)(downscaled - z1);
        z1 = downscaled;

        // Recursive part with 31-by-16-bit multiplication.
        var ls2 = s1 << 15;
        var msp = (short)(lz2 >> 15);
        var lsp = (short)(lz2 - (msp << 15));
        ls2 += Arith.MultR(lsp, 32735);
        var ltemp = msp * 32735;
        lz2 = Arith.LAdd(ltemp, ls2);

        ltemp = Arith.LAdd(lz2, 16384);

        // Pre-emphasis.
        msp = Arith.MultR(mp, -28180);
        mp = (short)(ltemp >> 15);
        so[k] = Arith.Add(mp, msp);
      }
      this._z1 = z1;
      this._lz2 = lz2;
      this._mp = mp;
    }

    // 4.2.4 – 4.2.7: autocorrelation, Schur recursion, LAR transform, quantisation.
    private void LpcAnalysis(Span<short> s, Span<short> larc) {
      Span<int> lAcf = stackalloc int[9];
      Autocorrelation(s, lAcf);
      ReflectionCoefficients(lAcf, larc);
      TransformationToLogAreaRatios(larc);
      QuantizationAndCoding(larc);
    }

    private static void Autocorrelation(Span<short> s, Span<int> lAcf) {
      short smax = 0;
      for (var k = 0; k < 160; ++k) {
        var temp = Arith.Abs(s[k]);
        if (temp > smax) smax = temp;
      }
      var scalauto = smax == 0 ? 0 : 4 - Arith.Norm(smax << 16);
      if (scalauto > 0) {
        var factor = (short)(16384 >> (scalauto - 1));
        for (var k = 0; k < 160; ++k) s[k] = Arith.MultR(s[k], factor);
      }

      lAcf.Clear();
      for (var i = 0; i < 160; ++i) {
        var si = s[i];
        var maxLag = Math.Min(i, 8);
        for (var k = 0; k <= maxLag; ++k)
          lAcf[k] += si * s[i - k];
      }
      for (var k = 0; k < 9; ++k) lAcf[k] <<= 1;

      if (scalauto > 0)
        for (var k = 0; k < 160; ++k) s[k] = (short)(s[k] << scalauto);
    }

    private static void ReflectionCoefficients(ReadOnlySpan<int> lAcf, Span<short> r) {
      if (lAcf[0] == 0) {
        r.Clear();
        return;
      }
      var norm = Arith.Norm(lAcf[0]);
      Span<short> acf = stackalloc short[9];
      Span<short> p = stackalloc short[9];
      Span<short> kk = stackalloc short[9];
      for (var i = 0; i < 9; ++i) acf[i] = (short)((lAcf[i] << norm) >> 16);
      for (var i = 1; i <= 7; ++i) kk[i] = acf[i];
      for (var i = 0; i <= 8; ++i) p[i] = acf[i];

      for (var n = 1; n <= 8; ++n) {
        var temp = Arith.Abs(p[1]);
        if (p[0] < temp) {
          for (var i = n; i <= 8; ++i) r[i - 1] = 0;
          return;
        }
        var rn = Arith.Div(temp, p[0]);
        if (p[1] > 0) rn = (short)-rn;
        r[n - 1] = rn;
        if (n == 8) return;

        temp = Arith.MultR(p[1], rn);
        p[0] = Arith.Add(p[0], temp);
        for (var m = 1; m <= 8 - n; ++m) {
          temp = Arith.MultR(kk[m], rn);
          p[m] = Arith.Add(p[m + 1], temp);
          temp = Arith.MultR(p[m + 1], rn);
          kk[m] = Arith.Add(kk[m], temp);
        }
      }
    }

    private static void TransformationToLogAreaRatios(Span<short> r) {
      for (var i = 0; i < 8; ++i) {
        var temp = Arith.Abs(r[i]);
        if (temp < 22118) temp >>= 1;
        else if (temp < 31130) temp -= 11059;
        else temp = (short)((temp - 26112) << 2);
        r[i] = r[i] < 0 ? (short)-temp : temp;
      }
    }

    private static void QuantizationAndCoding(Span<short> lar) {
      for (var i = 0; i < 8; ++i) {
        var temp = Arith.Mult(Tables.A[i], lar[i]);
        temp = Arith.Add(temp, Tables.B[i]);
        temp = Arith.Add(temp, 256);
        temp = (short)(temp >> 9);
        var mac = Tables.Mac[i];
        var mic = Tables.Mic[i];
        lar[i] = temp > mac ? (short)(mac - mic) : temp < mic ? (short)0 : (short)(temp - mic);
      }
    }

    // 4.2.8 – 4.2.10 / 4.3.4: short-term analysis and synthesis with LAR interpolation.

    private static void DecodeLogAreaRatios(ReadOnlySpan<short> larc, Span<short> larpp) {
      for (var i = 0; i < 8; ++i) {
        var temp1 = (short)(Arith.Add(larc[i], Tables.Mic[i]) << 10);
        temp1 = Arith.Sub(temp1, Tables.B[i] << 1);
        temp1 = Arith.MultR(Tables.InvA[i], temp1);
        larpp[i] = Arith.Add(temp1, temp1);
      }
    }

    private static void Coefficients0To12(ReadOnlySpan<short> prev, ReadOnlySpan<short> cur, Span<short> larp) {
      for (var i = 0; i < 8; ++i) {
        larp[i] = Arith.Add(prev[i] >> 2, cur[i] >> 2);
        larp[i] = Arith.Add(larp[i], prev[i] >> 1);
      }
    }

    private static void Coefficients13To26(ReadOnlySpan<short> prev, ReadOnlySpan<short> cur, Span<short> larp) {
      for (var i = 0; i < 8; ++i)
        larp[i] = Arith.Add(prev[i] >> 1, cur[i] >> 1);
    }

    private static void Coefficients27To39(ReadOnlySpan<short> prev, ReadOnlySpan<short> cur, Span<short> larp) {
      for (var i = 0; i < 8; ++i) {
        larp[i] = Arith.Add(prev[i] >> 2, cur[i] >> 2);
        larp[i] = Arith.Add(larp[i], cur[i] >> 1);
      }
    }

    private static void LarpToRp(Span<short> larp) {
      for (var i = 0; i < 8; ++i) {
        var temp = Arith.Abs(larp[i]);
        var mapped = temp < 11059 ? (short)(temp << 1)
          : temp < 20070 ? (short)(temp + 11059)
          : Arith.Add(temp >> 2, 26112);
        larp[i] = larp[i] < 0 ? (short)-mapped : mapped;
      }
    }

    private void ShortTermAnalysisFiltering(ReadOnlySpan<short> rp, Span<short> s) {
      var u = this._u;
      for (var k = 0; k < s.Length; ++k) {
        var di = s[k];
        var sav = di;
        for (var i = 0; i < 8; ++i) {
          var ui = u[i];
          var rpi = rp[i];
          u[i] = sav;
          var zzz = Arith.MultR(rpi, di);
          sav = Arith.Add(ui, zzz);
          zzz = Arith.MultR(rpi, ui);
          di = Arith.Add(di, zzz);
        }
        s[k] = di;
      }
    }

    private void ShortTermSynthesisFiltering(ReadOnlySpan<short> rrp, ReadOnlySpan<short> wt, Span<short> sr) {
      var v = this._v;
      for (var k = 0; k < wt.Length; ++k) {
        var sri = wt[k];
        for (var i = 7; i >= 0; --i) {
          var tmp = Arith.MultR(rrp[i], v[i]);
          sri = Arith.Sub(sri, tmp);
          tmp = Arith.MultR(rrp[i], sri);
          v[i + 1] = Arith.Add(v[i], tmp);
        }
        sr[k] = v[0] = sri;
      }
    }

    private void ShortTermAnalysisFilter(ReadOnlySpan<short> larc, Span<short> s) {
      var cur = this._larpp[this._j];
      this._j ^= 1;
      var prev = this._larpp[this._j];
      Span<short> larp = stackalloc short[8];

      DecodeLogAreaRatios(larc, cur);
      Coefficients0To12(prev, cur, larp);
      LarpToRp(larp);
      this.ShortTermAnalysisFiltering(larp, s[..13]);
      Coefficients13To26(prev, cur, larp);
      LarpToRp(larp);
      this.ShortTermAnalysisFiltering(larp, s.Slice(13, 14));
      Coefficients27To39(prev, cur, larp);
      LarpToRp(larp);
      this.ShortTermAnalysisFiltering(larp, s.Slice(27, 13));
      cur.AsSpan().CopyTo(larp);
      LarpToRp(larp);
      this.ShortTermAnalysisFiltering(larp, s.Slice(40, 120));
    }

    private void ShortTermSynthesisFilter(ReadOnlySpan<short> larcr, ReadOnlySpan<short> wt, Span<short> s) {
      var cur = this._larpp[this._j];
      this._j ^= 1;
      var prev = this._larpp[this._j];
      Span<short> larp = stackalloc short[8];

      DecodeLogAreaRatios(larcr, cur);
      Coefficients0To12(prev, cur, larp);
      LarpToRp(larp);
      this.ShortTermSynthesisFiltering(larp, wt[..13], s[..13]);
      Coefficients13To26(prev, cur, larp);
      LarpToRp(larp);
      this.ShortTermSynthesisFiltering(larp, wt.Slice(13, 14), s.Slice(13, 14));
      Coefficients27To39(prev, cur, larp);
      LarpToRp(larp);
      this.ShortTermSynthesisFiltering(larp, wt.Slice(27, 13), s.Slice(27, 13));
      cur.AsSpan().CopyTo(larp);
      LarpToRp(larp);
      this.ShortTermSynthesisFiltering(larp, wt.Slice(40, 120), s.Slice(40, 120));
    }

    // 4.2.11 – 4.2.12 / 4.3.2: long-term predictor.

    private void LongTermPredictor(ReadOnlySpan<short> d, int dpOffset, Span<short> e, Span<short> dpp,
      out short ncOut, out short bcOut) {
      var dp0 = this._dp0;

      // Optimum scaling of d[0..39].
      short dmax = 0;
      for (var k = 0; k < 40; ++k) {
        var temp = Arith.Abs(d[k]);
        if (temp > dmax) dmax = temp;
      }
      short scal;
      if (dmax == 0) scal = 0;
      else {
        var norm = Arith.Norm(dmax << 16);
        scal = norm > 6 ? (short)0 : (short)(6 - norm);
      }
      Span<short> wt = stackalloc short[40];
      for (var k = 0; k < 40; ++k) wt[k] = (short)(d[k] >> scal);

      // Maximum cross-correlation → LTP lag.
      var lMax = 0;
      short nc = 40;
      for (var lambda = 40; lambda <= 120; ++lambda) {
        var lResult = 0;
        for (var k = 0; k < 40; ++k)
          lResult += wt[k] * dp0[dpOffset + k - lambda];
        if (lResult > lMax) {
          nc = (short)lambda;
          lMax = lResult;
        }
      }
      ncOut = nc;

      lMax <<= 1;
      lMax >>= 6 - scal;

      // Power of the reconstructed short-term residual at the chosen lag.
      var lPower = 0;
      for (var k = 0; k < 40; ++k) {
        var lTemp = dp0[dpOffset + k - nc] >> 3;
        lPower += lTemp * lTemp;
      }
      lPower <<= 1;

      short bc;
      if (lMax <= 0) bc = 0;
      else if (lMax >= lPower) bc = 3;
      else {
        var norm = Arith.Norm(lPower);
        var r = (short)((lMax << norm) >> 16);
        var sPow = (short)((lPower << norm) >> 16);
        for (bc = 0; bc <= 2; ++bc)
          if (r <= Arith.Mult(sPow, Tables.Dlb[bc])) break;
      }
      bcOut = bc;

      // 4.2.12 long-term analysis filtering.
      var bp = Tables.Qlb[bc];
      for (var k = 0; k < 40; ++k) {
        dpp[k] = Arith.MultR(bp, dp0[dpOffset + k - nc]);
        e[k] = Arith.Sub(d[k], dpp[k]);
      }
    }

    private void LongTermSynthesisFiltering(short ncr, short bcr, ReadOnlySpan<short> erp) {
      var dp0 = this._dp0;
      var nr = ncr < 40 || ncr > 120 ? this._nrp : ncr;
      this._nrp = nr;
      var brp = Tables.Qlb[bcr];
      for (var k = 0; k < 40; ++k) {
        var drpp = Arith.MultR(brp, dp0[120 + k - nr]);
        dp0[120 + k] = Arith.Add(erp[k], drpp);
      }
      Array.Copy(dp0, 40, dp0, 0, 120);
    }

    // 4.2.13 – 4.2.17 / 4.3.1: RPE encoding and decoding.

    private void RpeEncoding(out short xmaxc, out short mc, Span<short> xmc) {
      Span<short> x = stackalloc short[40];
      Span<short> xm = stackalloc short[13];
      Span<short> xmp = stackalloc short[13];
      this.WeightingFilter(x);
      RpeGridSelection(x, xm, out mc);
      ApcmQuantization(xm, xmc, out var mant, out var exp, out xmaxc);
      ApcmInverseQuantization(xmc, mant, exp, xmp);
      RpeGridPositioning(mc, xmp, this._e.AsSpan(5, 40));
    }

    private static void RpeDecoding(short xmaxcr, short mcr, ReadOnlySpan<short> xmcr, Span<short> erp) {
      Span<short> xmp = stackalloc short[13];
      ApcmXmaxcToExpMant(xmaxcr, out var exp, out var mant);
      ApcmInverseQuantization(xmcr, mant, exp, xmp);
      RpeGridPositioning(mcr, xmp, erp);
    }

    /// <summary>4.2.13: the input <c>e[-5..44]</c> lives in <c>_e[0..49]</c>, edges kept zero.</summary>
    private void WeightingFilter(Span<short> x) {
      var e = this._e;
      for (var k = 0; k < 40; ++k) {
        // The taps accumulate as plain products, not as the doubled GSM_L_MULT of the
        // rest of the specification; the rounding constant is likewise a half-LSB at 13 bits.
        long lResult = 8192 >> 1;
        for (var i = 0; i < 11; ++i)
          lResult += (long)e[k + i] * Tables.H[i];
        lResult >>= 13;
        x[k] = lResult < Arith.MinWord ? Arith.MinWord : lResult > Arith.MaxWord ? Arith.MaxWord : (short)lResult;
      }
    }

    private static void RpeGridSelection(ReadOnlySpan<short> x, Span<short> xm, out short mcOut) {
      long em = 0;
      short mc = 0;
      for (var m = 0; m < 4; ++m) {
        long lResult = 0;
        for (var i = 0; i < 13; ++i) {
          long lTemp = x[m + 3 * i] >> 2;
          lResult += lTemp * lTemp;
        }
        lResult <<= 1;
        if (lResult > em) {
          mc = (short)m;
          em = lResult;
        }
      }
      for (var i = 0; i < 13; ++i) xm[i] = x[mc + 3 * i];
      mcOut = mc;
    }

    private static void ApcmXmaxcToExpMant(short xmaxc, out short expOut, out short mantOut) {
      short exp = 0;
      if (xmaxc > 15) exp = (short)((xmaxc >> 3) - 1);
      var mant = (short)(xmaxc - (exp << 3));
      if (mant == 0) {
        exp = -4;
        mant = 7;
      } else {
        while (mant <= 7) {
          mant = (short)(mant << 1 | 1);
          exp--;
        }
        mant -= 8;
      }
      expOut = exp;
      mantOut = mant;
    }

    private static void ApcmQuantization(ReadOnlySpan<short> xm, Span<short> xmc, out short mantOut, out short expOut,
      out short xmaxcOut) {
      short xmax = 0;
      for (var i = 0; i < 13; ++i) {
        var temp = Arith.Abs(xm[i]);
        if (temp > xmax) xmax = temp;
      }

      short exp = 0;
      var t = (short)(xmax >> 9);
      var itest = 0;
      for (var i = 0; i <= 5; ++i) {
        itest |= t <= 0 ? 1 : 0;
        t >>= 1;
        if (itest == 0) exp++;
      }
      var shift = exp + 5;
      var xmaxc = Arith.Add(xmax >> shift, exp << 3);

      ApcmXmaxcToExpMant(xmaxc, out exp, out var mant);

      var temp1 = 6 - exp;
      var temp2 = Tables.NrFac[mant];
      for (var i = 0; i < 13; ++i) {
        var temp = (short)(xm[i] << temp1);
        temp = Arith.Mult(temp, temp2);
        temp = (short)(temp >> 12);
        xmc[i] = (short)(temp + 4);
      }

      mantOut = mant;
      expOut = exp;
      xmaxcOut = xmaxc;
    }

    private static void ApcmInverseQuantization(ReadOnlySpan<short> xmc, short mant, short exp, Span<short> xmp) {
      var temp1 = Tables.Fac[mant];
      var temp2 = Arith.Sub(6, exp);
      var temp3 = Arith.Asl(1, Arith.Sub(temp2, 1));
      for (var i = 0; i < 13; ++i) {
        var temp = (short)((xmc[i] << 1) - 7);
        temp <<= 12;
        temp = Arith.MultR(temp1, temp);
        temp = Arith.Add(temp, temp3);
        xmp[i] = Arith.Asr(temp, temp2);
      }
    }

    private static void RpeGridPositioning(short mc, ReadOnlySpan<short> xmp, Span<short> ep) {
      ep.Clear();
      for (var i = 0; i < 13; ++i)
        ep[mc + 3 * i] = xmp[i];
    }
  }
}

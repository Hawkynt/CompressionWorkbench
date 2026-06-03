#pragma warning disable CS1591
namespace Codec.G72x;

/// <summary>
/// ITU-T G.726 ADPCM at 32 kbit/s — historically standardised as G.721 — operating on
/// 4-bit codewords. This is a faithful port of the CCITT / Sun Microsystems reference
/// implementation (the shared <c>g72x.c</c> core plus the <c>g721.c</c> 32 kbit/s
/// layer): a backward-adaptive quantiser with locked fast/slow scale-factor adaptation
/// (<c>y(k)</c>) and an adaptive predictor of two poles plus six zeros operating in the
/// reference's fixed-point log / floating-state domain.
/// <para>
/// The reference algorithm carries audio as 14-bit two's-complement linear samples (the
/// encoder right-shifts the input by two, the decoder left-shifts its output by two).
/// The public helpers therefore consume and produce ordinary 16-bit linear PCM. Decoding
/// is bit-exact with the reference decoder; encoding shares the identical predictor /
/// quantiser state machine so an encode→decode round-trip reconstructs the waveform to
/// within ADPCM's inherent quantisation error.
/// </para>
/// </summary>
public static class G72xCodec {

  /// <summary>Per-channel adaptation state for the G.72x state machine.</summary>
  private sealed class State {
    public int Yl;                            // Locked/slow scale factor
    public int Yu;                            // Fast scale factor
    public int Dms;                           // Short-term average of F(I)
    public int Dml;                           // Long-term average of F(I)
    public int Ap;                            // Adaptation-speed control
    public readonly int[] A = new int[2];     // Pole predictor coefficients
    public readonly int[] B = new int[6];     // Zero predictor coefficients
    public readonly int[] Pk = new int[2];    // Signs of last two (dq+sez) sums
    public readonly int[] Dq = new int[6];    // Last six quantised diffs (FP form)
    public readonly int[] Sr = new int[2];    // Last two reconstructed signals (FP form)
    public int Td;                            // Tone-detect flag

    public State() {
      this.Yl = 34816;
      this.Yu = 544;
      for (var i = 0; i < 2; ++i) this.Sr[i] = 32;
      for (var i = 0; i < 6; ++i) this.Dq[i] = 32;
    }
  }

  // ── Tables (g72x.c power2; g721.c quantiser / adaptation tables) ──────────
  private static readonly short[] Power2 = [1, 2, 4, 8, 0x10, 0x20, 0x40, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000, 0x2000, 0x4000];
  private static readonly short[] Qtab721 = [-124, 80, 178, 246, 300, 349, 400];
  private static readonly short[] DqlntabT = [-2048, 4, 135, 213, 273, 323, 373, 425, 425, 373, 323, 273, 213, 135, 4, -2048];
  private static readonly short[] WitabT = [-12, 18, 41, 64, 112, 198, 355, 1122, 1122, 355, 198, 112, 64, 41, 18, -12];
  private static readonly short[] FitabT = [0, 0, 0, 0x200, 0x200, 0x200, 0x600, 0xE00, 0xE00, 0x600, 0x200, 0x200, 0x200, 0, 0, 0];

  // ── Public API ────────────────────────────────────────────────────────────

  /// <summary>
  /// Decodes G.721 (G.726 @ 32 kbit/s) 4-bit codewords to 16-bit linear PCM. <c>.au</c>
  /// packs the high nibble first within each byte (MSB-aligned).
  /// </summary>
  public static short[] DecodeG721(ReadOnlySpan<byte> data) {
    var s = new State();
    var output = new short[data.Length * 2];
    var n = 0;
    foreach (var b in data) {
      output[n++] = Clamp16(Decoder((b >> 4) & 0x0F, s));
      output[n++] = Clamp16(Decoder(b & 0x0F, s));
    }
    return output;
  }

  /// <summary>
  /// Encodes 16-bit linear PCM to G.721 (G.726 @ 32 kbit/s) 4-bit codewords, packing two
  /// codewords per byte high-nibble first to match <see cref="DecodeG721"/>. An odd
  /// trailing sample re-uses the previous codeword in the low nibble.
  /// </summary>
  public static byte[] EncodeG721(ReadOnlySpan<short> pcm) {
    var s = new State();
    var bytes = new byte[(pcm.Length + 1) / 2];
    var bi = 0;
    for (var i = 0; i < pcm.Length; i += 2) {
      var hi = Encoder(pcm[i], s) & 0x0F;
      var lo = i + 1 < pcm.Length ? Encoder(pcm[i + 1], s) & 0x0F : hi;
      bytes[bi++] = (byte)((hi << 4) | lo);
    }
    return bytes;
  }

  private static short Clamp16(int v) =>
    v > 32767 ? (short)32767 : v < -32768 ? (short)-32768 : (short)v;

  // ── g721_encoder ────────────────────────────────────────────────────────────
  private static int Encoder(int sl, State s) {
    sl >>= 2;                                          // 16-bit → 14-bit reference domain

    var sezi = PredictorZero(s);
    var sez = sezi >> 1;
    var se = (sezi + PredictorPole(s)) >> 1;           // Estimated signal

    var d = sl - se;                                   // Estimation difference

    var y = StepSize(s);
    var i = Quantize(d, y, Qtab721, 7);                // 4-bit codeword

    var dq = Reconstruct(i & 8, DqlntabT[i], y);

    var sr = (dq < 0) ? (se - (dq & 0x3FFF)) : (se + dq);
    var dqsez = sr + sez - se;

    Update(4, y, WitabT[i] << 5, FitabT[i], dq, sr, dqsez, s);
    return i;
  }

  // ── g721_decoder ────────────────────────────────────────────────────────────
  private static int Decoder(int i, State s) {
    i &= 0x0F;
    var sezi = PredictorZero(s);
    var sez = sezi >> 1;
    var sei = sezi + PredictorPole(s);
    var se = sei >> 1;                                 // Estimated signal

    var y = StepSize(s);
    var dq = Reconstruct(i & 0x08, DqlntabT[i], y);

    var sr = (dq < 0) ? (se - (dq & 0x3FFF)) : (se + dq);
    var dqsez = sr - se + sez;

    Update(4, y, WitabT[i] << 5, FitabT[i], dq, sr, dqsez, s);
    return sr << 2;                                    // 14-bit → 16-bit reference domain
  }

  // ── predictor_zero ────────────────────────────────────────────────────────
  private static int PredictorZero(State s) {
    var sezi = Fmult(s.B[0] >> 2, s.Dq[0]);
    for (var i = 1; i < 6; ++i)
      sezi += Fmult(s.B[i] >> 2, s.Dq[i]);
    return sezi;
  }

  // ── predictor_pole ──────────────────────────────────────────────────────────
  private static int PredictorPole(State s) =>
    Fmult(s.A[1] >> 2, s.Sr[1]) + Fmult(s.A[0] >> 2, s.Sr[0]);

  // ── step_size ───────────────────────────────────────────────────────────────
  private static int StepSize(State s) {
    if (s.Ap >= 256)
      return s.Yu;
    var y = s.Yl >> 6;
    var dif = s.Yu - y;
    var al = s.Ap >> 2;
    if (dif > 0)
      y += (dif * al) >> 6;
    else if (dif < 0)
      y += (dif * al + 0x3F) >> 6;
    return y;
  }

  // ── quantize ──────────────────────────────────────────────────────────────────
  private static int Quantize(int d, int y, short[] table, int size) {
    var dqm = Math.Abs(d);
    var expon = Quan(dqm >> 1, Power2, 15);
    var mant = ((dqm << 7) >> expon) & 0x7F;
    var dl = (expon << 7) + mant;

    var dln = dl - (y >> 2);

    var i = Quan(dln, table, size);
    if (d < 0)
      return (size << 1) + 1 - i;
    if (i == 0)
      return (size << 1) + 1;
    return i;
  }

  // ── reconstruct ─────────────────────────────────────────────────────────────
  private static int Reconstruct(int sign, int dqln, int y) {
    var dql = dqln + (y >> 2);                          // ADDA
    if (dql < 0)
      return sign != 0 ? -0x8000 : 0;
    var dex = (dql >> 7) & 15;                          // ANTILOG
    var dqt = 128 + (dql & 127);
    var dq = (dqt << 7) >> (14 - dex);
    return sign != 0 ? (dq - 0x8000) : dq;
  }

  // ── update ────────────────────────────────────────────────────────────────────
  private static void Update(int codeSize, int y, int wi, int fi, int dq, int sr, int dqsez, State s) {
    var pk0 = (dqsez < 0) ? 1 : 0;
    var mag = dq & 0x7FFF;                              // prediction-difference magnitude

    // TRANS — transition (tone/data) detection threshold.
    var ylint = s.Yl >> 15;
    var ylfrac = (s.Yl >> 10) & 0x1F;
    var thr1 = (32 + ylfrac) << ylint;
    var thr2 = (ylint > 9) ? (31 << 10) : thr1;
    var dqthr = (thr2 + (thr2 >> 1)) >> 1;
    int tr;
    if (s.Td == 0) tr = 0;
    else if (mag <= dqthr) tr = 0;
    else tr = 1;

    // Quantiser scale-factor adaptation (FUNCTW & FILTD/E & DELAY, LIMB).
    s.Yu = y + ((wi - y) >> 5);
    if (s.Yu < 544) s.Yu = 544;
    else if (s.Yu > 5120) s.Yu = 5120;
    s.Yl += s.Yu + ((-s.Yl) >> 6);

    var a2p = 0;
    if (tr == 1) {
      s.A[0] = 0; s.A[1] = 0;
      for (var k = 0; k < 6; ++k) s.B[k] = 0;
    } else {
      var pks1 = pk0 ^ s.Pk[0];                          // UPA2

      // Update predictor pole a[1].
      a2p = s.A[1] - (s.A[1] >> 7);
      if (dqsez != 0) {
        var fa1 = (pks1 != 0) ? s.A[0] : -s.A[0];
        if (fa1 < -8191) a2p -= 0x100;
        else if (fa1 > 8191) a2p += 0xFF;
        else a2p += fa1 >> 5;

        if ((pk0 ^ s.Pk[1]) != 0) {                      // LIMC
          if (a2p <= -12160) a2p = -12288;
          else if (a2p >= 12416) a2p = 12288;
          else a2p -= 0x80;
        } else if (a2p <= -12416) a2p = -12288;
        else if (a2p >= 12160) a2p = 12288;
        else a2p += 0x80;
      }
      s.A[1] = a2p;                                       // TRIGB & DELAY

      // Update predictor pole a[0] (UPA1).
      s.A[0] -= s.A[0] >> 8;
      if (dqsez != 0) {
        if (pks1 == 0) s.A[0] += 192;
        else s.A[0] -= 192;
      }

      // LIMD — stability limit on a[0].
      var a1ul = 15360 - a2p;
      if (s.A[0] < -a1ul) s.A[0] = -a1ul;
      else if (s.A[0] > a1ul) s.A[0] = a1ul;

      // UPB — update predictor zeros b[0..5].
      for (var cnt = 0; cnt < 6; ++cnt) {
        if (codeSize == 5) s.B[cnt] -= s.B[cnt] >> 9;     // 40 kbit/s G.723
        else s.B[cnt] -= s.B[cnt] >> 8;                   // G.721 and 24 kbit/s G.723
        if ((dq & 0x7FFF) != 0) {
          if (((dq ^ s.Dq[cnt]) >= 0)) s.B[cnt] += 128;
          else s.B[cnt] -= 128;
        }
      }
    }

    // Shift quantised-difference history; FLOAT A (4-bit exp, 6-bit mantissa).
    for (var cnt = 5; cnt > 0; --cnt) s.Dq[cnt] = s.Dq[cnt - 1];
    if (mag == 0)
      s.Dq[0] = (dq >= 0) ? 0x20 : unchecked((short)0xFC20);
    else {
      var expon = Quan(mag, Power2, 15);
      s.Dq[0] = (dq >= 0)
        ? (short)((expon << 6) + ((mag << 6) >> expon))
        : (short)((expon << 6) + ((mag << 6) >> expon) - 0x400);
    }

    // Shift reconstructed-signal history; FLOAT B.
    s.Sr[1] = s.Sr[0];
    if (sr == 0)
      s.Sr[0] = 0x20;
    else if (sr > 0) {
      var expon = Quan(sr, Power2, 15);
      s.Sr[0] = (short)((expon << 6) + ((sr << 6) >> expon));
    } else if (sr > -32768) {
      var m = -sr;
      var expon = Quan(m, Power2, 15);
      s.Sr[0] = (short)((expon << 6) + ((m << 6) >> expon) - 0x400);
    } else
      s.Sr[0] = unchecked((short)0xFC20);

    // DELAY A.
    s.Pk[1] = s.Pk[0];
    s.Pk[0] = pk0;

    // TONE.
    if (tr == 1) s.Td = 0;
    else if (a2p < -11776) s.Td = 1;
    else s.Td = 0;

    // Adaptation speed control.
    s.Dms += (fi - s.Dms) >> 5;                           // FILTA
    s.Dml += ((fi << 2) - s.Dml) >> 7;                    // FILTB
    if (tr == 1) s.Ap = 256;
    else if (y < 1536) s.Ap += (0x200 - s.Ap) >> 4;       // SUBTC
    else if (s.Td == 1) s.Ap += (0x200 - s.Ap) >> 4;
    else if (Math.Abs((s.Dms << 2) - s.Dml) >= (s.Dml >> 3)) s.Ap += (0x200 - s.Ap) >> 4;
    else s.Ap += (-s.Ap) >> 4;
  }

  // ── fmult ────────────────────────────────────────────────────────────────────
  private static int Fmult(int an, int srn) {
    var anmag = (an > 0) ? an : ((-an) & 0x1FFF);
    var anexp = Quan(anmag, Power2, 15) - 6;
    var anmant = (anmag == 0) ? 32 : ((anexp >= 0) ? (anmag >> anexp) : (anmag << -anexp));
    var wanexp = anexp + ((srn >> 6) & 0xF) - 13;

    var wanmant = (anmant * (srn & 0x3F)) >> 4;
    var retval = (wanexp >= 0) ? ((wanmant << wanexp) & 0x7FFF) : (wanmant >> -wanexp);

    return ((an ^ srn) < 0) ? -retval : retval;
  }

  // ── quan — linear search returning first index where val < table[i] ──────────
  private static int Quan(int val, short[] table, int size) {
    int i;
    for (i = 0; i < size; ++i)
      if (val < table[i])
        break;
    return i;
  }
}

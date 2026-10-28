#pragma warning disable CS1591
namespace Codec.G722;

/// <summary>
/// ITU-T G.722 sub-band ADPCM at 64 kbit/s (mode 1: six low-band bits and two high-band bits
/// per octet), 16-bit signed linear PCM at 16 kHz, mono. Implemented from
/// <c>docs/G722-SPEC.md</c>, which restates ITU-T Recommendation G.722 (09/2012) sections 3, 4
/// and 6; block and variable names in comments (<c>DETL</c>, <c>UPPOL2</c>, ...) are the
/// Recommendation's.
/// <para>
/// Like the common 16-bit software codecs, this one feeds 16-bit samples straight into the
/// transmit QMF instead of the Recommendation's 14-bit interface and compensates with the QMF
/// output shifts (<c>&gt;&gt; 14</c> on transmit, <c>&gt;&gt; 11</c> on receive), so streams
/// interoperate with FFmpeg's <c>g722</c> encoder and decoder.
/// </para>
/// <para>
/// One octet carries two samples: bits 7–6 are the high-band code, bits 5–0 the low-band code.
/// Every call starts from the reset state; nothing carries across calls.
/// </para>
/// </summary>
public static class G722Codec {

  // ── Constant tables (ITU-T G.722, copied exactly) ────────────────────────

  /// <summary>Half of the mirrored 24-tap QMF coefficient set.</summary>
  private static readonly int[] Qmf = [3, -11, 12, 32, -210, 951, 3876, -805, 362, -156, 53, -11];

  /// <summary>Low-band quantiser decision levels (block 1L).</summary>
  private static readonly int[] Q6 = [
    0, 35, 72, 110, 150, 190, 233, 276, 323, 370, 422, 473, 530, 587, 650, 714,
    786, 858, 940, 1023, 1121, 1219, 1339, 1458, 1612, 1765, 1980, 2195, 2557, 2919, 0, 0,
  ];

  /// <summary>Low-band 6-bit code for a negative difference at level m.</summary>
  private static readonly int[] Iln = [
    0, 63, 62, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19,
    18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 0,
  ];

  /// <summary>Low-band 6-bit code for a non-negative difference at level m.</summary>
  private static readonly int[] Ilp = [
    0, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 51, 50, 49, 48, 47,
    46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33, 32, 0,
  ];

  /// <summary>Low-band log scale-factor multipliers (LOGSCL).</summary>
  private static readonly int[] Wl = [-60, -30, 58, 172, 334, 538, 1198, 3042];

  /// <summary>4-bit low-band code → <see cref="Wl"/> index.</summary>
  private static readonly int[] Rl42 = [0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3, 2, 1, 0];

  /// <summary>Inverse-log table used by SCALEL / SCALEH.</summary>
  private static readonly int[] Ilb = [
    2048, 2093, 2139, 2186, 2233, 2282, 2332, 2383, 2435, 2489, 2543, 2599, 2656, 2714, 2774, 2834,
    2896, 2960, 3025, 3091, 3158, 3228, 3298, 3371, 3444, 3520, 3597, 3676, 3756, 3838, 3922, 4008,
  ];

  /// <summary>4-bit low-band inverse quantiser (block 2L).</summary>
  private static readonly int[] Qm4 = [
    0, -20456, -12896, -8968, -6288, -4240, -2584, -1200,
    20456, 12896, 8968, 6288, 4240, 2584, 1200, 0,
  ];

  /// <summary>2-bit high-band inverse quantiser.</summary>
  private static readonly int[] Qm2 = [-7408, -1616, 7408, 1616];

  /// <summary>6-bit low-band inverse quantiser for mode-1 reconstruction (block 5L).</summary>
  private static readonly int[] Qm6 = [
    -136, -136, -136, -136, -24808, -21904, -19008, -16704,
    -14984, -13512, -12280, -11192, -10232, -9360, -8576, -7856,
    -7192, -6576, -6000, -5456, -4944, -4464, -4008, -3576,
    -3168, -2776, -2400, -2032, -1688, -1360, -1040, -728,
    24808, 21904, 19008, 16704, 14984, 13512, 12280, 11192,
    10232, 9360, 8576, 7856, 7192, 6576, 6000, 5456,
    4944, 4464, 4008, 3576, 3168, 2776, 2400, 2032,
    1688, 1360, 1040, 728, 432, 136, -432, -136,
  ];

  /// <summary>High-band 2-bit code for a negative / non-negative difference at level m.</summary>
  private static readonly int[] Ihn = [0, 1, 0];
  private static readonly int[] Ihp = [0, 3, 2];

  /// <summary>High-band log scale-factor multipliers.</summary>
  private static readonly int[] Wh = [0, -214, 798];

  /// <summary>2-bit high-band code → <see cref="Wh"/> index.</summary>
  private static readonly int[] Rh2 = [2, 1, 2, 1];

  // ── Band parameters ──────────────────────────────────────────────────────

  internal const int LowNbLimit = 18432;
  internal const int HighNbLimit = 22528;
  private const int LowScaleBase = 8;
  private const int HighScaleBase = 10;
  private const int LowDetFloor = 32;
  private const int HighDetFloor = 8;
  private const int HighThreshold = 564;
  private const int QmfDelay = 24;

  // ── Public API ───────────────────────────────────────────────────────────

  /// <summary>Encodes 16 kHz PCM to G.722 octets; a trailing odd sample is dropped.</summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var output = new byte[pcm.Length / 2];
    var low = Band.CreateLow();
    var high = Band.CreateHigh();
    Span<int> delay = stackalloc int[QmfDelay];
    delay.Clear();

    for (var n = 0; n < output.Length; ++n) {
      PushPair(delay, pcm[2 * n], pcm[2 * n + 1]);
      FilterPair(delay, out var oddTaps, out var evenTaps);
      var xl = (evenTaps + oddTaps) >> 14;
      var xh = (evenTaps - oddTaps) >> 14;

      var il = QuantiseLow(Saturate(xl - low.S), low.Det);
      low.Adapt(il >> 2);

      var ih = QuantiseHigh(Saturate(xh - high.S), high.Det);
      high.Adapt(ih);

      output[n] = (byte)((ih << 6) | il);
    }

    return output;
  }

  /// <summary>Decodes G.722 octets to 16 kHz PCM, two samples per octet.</summary>
  public static short[] Decode(ReadOnlySpan<byte> data) {
    var sampleCount = (long)data.Length * 2;
    if (sampleCount > Array.MaxLength)
      throw new NotSupportedException(
        $"G.722: {data.Length} octets decode to {sampleCount} samples, which exceeds the {Array.MaxLength}-element array limit.");

    var output = new short[sampleCount];
    var low = Band.CreateLow();
    var high = Band.CreateHigh();
    Span<int> delay = stackalloc int[QmfDelay];
    delay.Clear();

    for (var n = 0; n < data.Length; ++n) {
      var octet = data[n];
      var il = octet & 0x3F;
      var ih = (octet >> 6) & 3;

      // Block 5L: reconstruct from the state before this octet's adaptation.
      var rl = Math.Clamp(low.S + ((low.Det * Qm6[il]) >> 15), -16384, 16383);
      low.Adapt(il >> 2);

      // Block 5H.
      var rh = Math.Clamp(high.S + ((high.Det * Qm2[ih]) >> 15), -16384, 16383);
      high.Adapt(ih);

      // Receive QMF.
      PushPair(delay, rl + rh, rl - rh);
      FilterPair(delay, out var oddTaps, out var evenTaps);
      output[2 * n] = (short)Saturate(evenTaps >> 11);
      output[2 * n + 1] = (short)Saturate(oddTaps >> 11);
    }

    return output;
  }

  // ── QMF ──────────────────────────────────────────────────────────────────

  /// <summary>Shifts the delay line down by two and appends <paramref name="first"/>, <paramref name="second"/>.</summary>
  private static void PushPair(Span<int> delay, int first, int second) {
    delay[2..].CopyTo(delay);
    delay[^2] = first;
    delay[^1] = second;
  }

  /// <summary>
  /// Applies the mirrored QMF: <paramref name="oddTaps"/> weights the even-indexed delay
  /// entries with <c>h[i]</c>, <paramref name="evenTaps"/> the odd-indexed ones with <c>h[11−i]</c>.
  /// </summary>
  private static void FilterPair(ReadOnlySpan<int> delay, out int oddTaps, out int evenTaps) {
    oddTaps = 0;
    evenTaps = 0;
    for (var i = 0; i < Qmf.Length; ++i) {
      oddTaps += delay[2 * i] * Qmf[i];
      evenTaps += delay[2 * i + 1] * Qmf[^(i + 1)];
    }
  }

  // ── Quantisers (blocks 1L, 1H) ───────────────────────────────────────────

  /// <summary>Six-bit low-band code for difference <paramref name="el"/> at scale <paramref name="det"/>.</summary>
  internal static int QuantiseLow(int el, int det) {
    var magnitude = el >= 0 ? el : -(el + 1);
    var level = 30;
    for (var m = 1; m < 30; ++m)
      if (magnitude < (Q6[m] * det) >> 12) {
        level = m;
        break;
      }

    return el >= 0 ? Ilp[level] : Iln[level];
  }

  /// <summary>Two-bit high-band code for difference <paramref name="eh"/> at scale <paramref name="det"/>.</summary>
  internal static int QuantiseHigh(int eh, int det) {
    var magnitude = eh >= 0 ? eh : -(eh + 1);
    var level = magnitude >= (HighThreshold * det) >> 12 ? 2 : 1;
    return eh >= 0 ? Ihp[level] : Ihn[level];
  }

  /// <summary>SCALEL / SCALEH: quantiser scale from a log scale factor.</summary>
  internal static int Scale(int nb, int scaleBase) {
    var mantissa = Ilb[(nb >> 6) & 31];
    var shift = scaleBase - (nb >> 11);
    var w = shift >= 0 ? mantissa >> shift : mantissa << -shift;
    return w << 2;
  }

  internal static int Saturate(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

  // ── Band state ───────────────────────────────────────────────────────────

  /// <summary>Adaptive quantiser and predictor state for one sub-band.</summary>
  internal sealed class Band {

    private readonly int[] _inverseQuantiser;
    private readonly int[] _logMultipliers;
    private readonly int[] _codeToMultiplier;
    private readonly int _nbLimit;
    private readonly int _scaleBase;

    // Pole section: coefficients A[1..2] and histories R/P[0..2]; index 0 of A is unused.
    private readonly int[] _a = new int[3];
    private readonly int[] _r = new int[3];
    private readonly int[] _p = new int[3];

    // Zero section: coefficients B[1..6] and difference history D[0..6]; index 0 of B is unused.
    private readonly int[] _b = new int[7];
    private readonly int[] _d = new int[7];

    private int _sz;

    private Band(int[] inverseQuantiser, int[] logMultipliers, int[] codeToMultiplier, int nbLimit, int scaleBase, int detFloor) {
      this._inverseQuantiser = inverseQuantiser;
      this._logMultipliers = logMultipliers;
      this._codeToMultiplier = codeToMultiplier;
      this._nbLimit = nbLimit;
      this._scaleBase = scaleBase;
      this.Det = detFloor;
    }

    internal static Band CreateLow() => new(Qm4, Wl, Rl42, LowNbLimit, LowScaleBase, LowDetFloor);
    internal static Band CreateHigh() => new(Qm2, Wh, Rh2, HighNbLimit, HighScaleBase, HighDetFloor);

    /// <summary>Signal estimate <c>SL</c> / <c>SH</c>.</summary>
    internal int S { get; private set; }

    /// <summary>Quantiser scale <c>DETL</c> / <c>DETH</c>.</summary>
    internal int Det { get; private set; }

    /// <summary>Log scale factor <c>NBL</c> / <c>NBH</c>.</summary>
    internal int Nb { get; private set; }

    /// <summary>
    /// Runs the inverse quantiser, the scale-factor adaptation and the predictor update for
    /// <paramref name="code"/> (the 4-bit truncated low-band code, or the 2-bit high-band code).
    /// </summary>
    internal void Adapt(int code) {
      var difference = (this.Det * this._inverseQuantiser[code]) >> 15;

      this.Nb = Math.Clamp(((this.Nb * 127) >> 7) + this._logMultipliers[this._codeToMultiplier[code]], 0, this._nbLimit);
      this.Det = Scale(this.Nb, this._scaleBase);

      this.UpdatePredictor(difference);
    }

    private static int Sign(int value) => value >> 15;

    private void UpdatePredictor(int d) {
      var a = this._a;
      var b = this._b;
      var r = this._r;
      var p = this._p;
      var dh = this._d;

      dh[0] = d;
      r[0] = Saturate(this.S + d);
      p[0] = Saturate(this._sz + d);

      var p0Sign = Sign(p[0]);
      var samePrev = p0Sign == Sign(p[1]);

      // UPPOL2
      var w1 = Saturate(a[1] << 2);
      var w2 = Math.Min(samePrev ? -w1 : w1, short.MaxValue);
      var w3 = (w2 >> 7) + (p0Sign == Sign(p[2]) ? 128 : -128) + ((a[2] * 32512) >> 15);
      var nextA2 = Math.Clamp(w3, -12288, 12288);

      // UPPOL1
      var nextA1 = Saturate((samePrev ? 192 : -192) + ((a[1] * 32640) >> 15));
      var limit = Saturate(15360 - nextA2);
      nextA1 = Math.Clamp(nextA1, -limit, limit);

      // UPZERO + DELAYA for the zero section (walking down so D[i-1] is still unshifted).
      var gain = d == 0 ? 0 : 128;
      var dSign = Sign(d);
      for (var i = 6; i >= 1; --i) {
        b[i] = Saturate((Sign(dh[i]) == dSign ? gain : -gain) + ((b[i] * 32640) >> 15));
        dh[i] = dh[i - 1];
      }

      // DELAYA for the pole section.
      r[2] = r[1];
      r[1] = r[0];
      p[2] = p[1];
      p[1] = p[0];
      a[1] = nextA1;
      a[2] = nextA2;

      // FILTEP
      var sp = Saturate(((a[1] * Saturate(2 * r[1])) >> 15) + ((a[2] * Saturate(2 * r[2])) >> 15));

      // FILTEZ: each product shifted individually, the sum saturated once.
      var zeroSum = 0;
      for (var i = 1; i <= 6; ++i)
        zeroSum += (b[i] * Saturate(2 * dh[i])) >> 15;
      this._sz = Saturate(zeroSum);

      // PREDIC
      this.S = Saturate(sp + this._sz);
    }
  }
}

#pragma warning disable CS1591
namespace Codec.G722;

/// <summary>
/// ITU-T G.722 64 kbit/s sub-band ADPCM. A faithful port of the ITU / SpanDSP reference
/// (the CMU single-channel core): a 24-tap quadrature mirror filter (QMF) splits the
/// 16 kHz input into two 8 kHz sub-bands; the lower band is coded with 6-bit ADPCM and the
/// higher band with 2-bit ADPCM (operating mode 1 — the full 64 kbit/s rate). The decoder
/// mirrors this, running the two band ADPCM decoders and recombining the bands through the
/// QMF synthesis filter.
/// <para>
/// One octet carries one combined codeword (2 high-band bits in the top nibble's two MSBs,
/// 6 low-band bits below): each octet represents two output samples (one QMF frame). The
/// public API therefore consumes / produces ordinary 16-bit linear PCM at 16 kHz, with the
/// encoder emitting one byte per two input samples.
/// </para>
/// </summary>
public static class G722Codec {

  // ── Backward-adaptive predictor / quantiser state for one sub-band. ──────────────
  private sealed class Band {
    public int S;
    public int Sp;
    public int Sz;
    public readonly int[] R = new int[3];
    public readonly int[] A = new int[3];
    public readonly int[] Ap = new int[3];
    public readonly int[] P = new int[3];
    public readonly int[] D = new int[7];
    public readonly int[] B = new int[7];
    public readonly int[] Bp = new int[7];
    public readonly int[] Sg = new int[7];
    public int Nb;
    public int Det;
  }

  // ── Reference quantiser / scale tables (ITU / SpanDSP). ──────────────────────────
  private static readonly int[] Q6 = [
    0, 35, 72, 110, 150, 190, 233, 276, 323, 370, 422, 473, 530, 587, 650, 714,
    786, 858, 940, 1023, 1121, 1219, 1339, 1458, 1612, 1765, 1980, 2195, 2557, 2919, 0, 0,
  ];
  private static readonly int[] Iln = [
    0, 63, 62, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19,
    18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 0,
  ];
  private static readonly int[] Ilp = [
    0, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 51, 50, 49, 48, 47,
    46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33, 32, 0,
  ];
  private static readonly int[] Wl = [-60, -30, 58, 172, 334, 538, 1198, 3042];
  private static readonly int[] Rl42 = [0, 7, 6, 5, 4, 3, 2, 1, 7, 6, 5, 4, 3, 2, 1, 0];
  private static readonly int[] Ilb = [
    2048, 2093, 2139, 2186, 2233, 2282, 2332, 2383, 2435, 2489, 2543, 2599, 2656, 2714, 2774, 2834,
    2896, 2960, 3025, 3091, 3158, 3228, 3298, 3371, 3444, 3520, 3597, 3676, 3756, 3838, 3922, 4008,
  ];
  private static readonly int[] Qm4 = [
    0, -20456, -12896, -8968, -6288, -4240, -2584, -1200,
    20456, 12896, 8968, 6288, 4240, 2584, 1200, 0,
  ];
  private static readonly int[] Qm2 = [-7408, -1616, 7408, 1616];
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
  private static readonly int[] QmfCoeffs = [3, -11, 12, 32, -210, 951, 3876, -805, 362, -156, 53, -11];
  private static readonly int[] Ihn = [0, 1, 0];
  private static readonly int[] Ihp = [0, 3, 2];
  private static readonly int[] Wh = [0, -214, 798];
  private static readonly int[] Rh2 = [2, 1, 2, 1];

  /// <summary>
  /// Encodes 16-bit linear PCM at 16 kHz to a G.722 (64 kbit/s) octet stream, one byte per
  /// two input samples. An odd trailing sample is ignored (a full QMF frame needs two).
  /// </summary>
  public static byte[] Encode(ReadOnlySpan<short> pcm) {
    var lowBand = new Band { Det = 32 };
    var highBand = new Band { Det = 8 };
    var x = new int[24];

    var frames = pcm.Length / 2;
    var output = new byte[frames];
    var oi = 0;

    for (var j = 0; j + 1 < pcm.Length; j += 2) {
      // Apply the transmit QMF (shuffle history down, push two new samples).
      for (var i = 0; i < 22; ++i)
        x[i] = x[i + 2];
      x[22] = pcm[j];
      x[23] = pcm[j + 1];

      var sumOdd = 0;
      var sumEven = 0;
      for (var i = 0; i < 12; ++i) {
        sumOdd += x[2 * i] * QmfCoeffs[i];
        sumEven += x[2 * i + 1] * QmfCoeffs[11 - i];
      }
      var xlow = (sumEven + sumOdd) >> 14;
      var xhigh = (sumEven - sumOdd) >> 14;

      // Block 1L: low-band difference + 6-bit quantiser.
      var el = Saturate(xlow - lowBand.S);
      var wd = el >= 0 ? el : -(el + 1);
      var idx = 1;
      for (; idx < 30; ++idx) {
        var wd1Inner = (Q6[idx] * lowBand.Det) >> 12;
        if (wd < wd1Inner)
          break;
      }
      var ilow = el < 0 ? Iln[idx] : Ilp[idx];

      // Block 2L/3L: inverse quantise + scale-factor adaptation.
      var ril = ilow >> 2;
      var dlow = (lowBand.Det * Qm4[ril]) >> 15;
      var il4 = Rl42[ril];
      var nbLow = ((lowBand.Nb * 127) >> 7) + Wl[il4];
      lowBand.Nb = Math.Clamp(nbLow, 0, 18432);
      ScaleAndAdapt(lowBand, scaleShiftBase: 8);
      Block4(lowBand, dlow);

      // Block 1H: high-band difference + 2-bit quantiser.
      var eh = Saturate(xhigh - highBand.S);
      wd = eh >= 0 ? eh : -(eh + 1);
      var wd1 = (564 * highBand.Det) >> 12;
      var mih = wd >= wd1 ? 2 : 1;
      var ihigh = eh < 0 ? Ihn[mih] : Ihp[mih];

      var dhigh = (highBand.Det * Qm2[ihigh]) >> 15;
      var ih2 = Rh2[ihigh];
      var nbHigh = ((highBand.Nb * 127) >> 7) + Wh[ih2];
      highBand.Nb = Math.Clamp(nbHigh, 0, 22528);
      ScaleAndAdapt(highBand, scaleShiftBase: 10);
      Block4(highBand, dhigh);

      output[oi++] = (byte)((ihigh << 6) | ilow);
    }
    return output;
  }

  /// <summary>
  /// Decodes a G.722 (64 kbit/s) octet stream to 16-bit linear PCM at 16 kHz, producing two
  /// samples per input byte.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> data) {
    var lowBand = new Band { Det = 32 };
    var highBand = new Band { Det = 8 };
    var x = new int[24];

    var output = new short[data.Length * 2];
    var oi = 0;

    foreach (var code in data) {
      var wd1 = code & 0x3F;
      var ihigh = (code >> 6) & 0x03;
      var wd2 = Qm6[wd1];
      wd1 >>= 2;

      // Block 5L: low-band reconstruction.
      wd2 = (lowBand.Det * wd2) >> 15;
      var rlow = Math.Clamp(lowBand.S + wd2, -16384, 16383);

      // Block 2L/3L: inverse quantise + scale-factor adaptation.
      var dlowt = (lowBand.Det * Qm4[wd1]) >> 15;
      var nbLow = ((lowBand.Nb * 127) >> 7) + Wl[Rl42[wd1]];
      lowBand.Nb = Math.Clamp(nbLow, 0, 18432);
      ScaleAndAdapt(lowBand, scaleShiftBase: 8);
      Block4(lowBand, dlowt);

      // Block 2H/5H: high-band reconstruction + scale-factor adaptation.
      var dhigh = (highBand.Det * Qm2[ihigh]) >> 15;
      var rhigh = Math.Clamp(dhigh + highBand.S, -16384, 16383);
      var nbHigh = ((highBand.Nb * 127) >> 7) + Wh[Rh2[ihigh]];
      highBand.Nb = Math.Clamp(nbHigh, 0, 22528);
      ScaleAndAdapt(highBand, scaleShiftBase: 10);
      Block4(highBand, dhigh);

      // Apply the receive QMF: recombine the two bands.
      for (var i = 0; i < 22; ++i)
        x[i] = x[i + 2];
      x[22] = rlow + rhigh;
      x[23] = rlow - rhigh;

      var xout1 = 0;
      var xout2 = 0;
      for (var i = 0; i < 12; ++i) {
        xout2 += x[2 * i] * QmfCoeffs[i];
        xout1 += x[2 * i + 1] * QmfCoeffs[11 - i];
      }
      output[oi++] = (short)Saturate(xout1 >> 11);
      output[oi++] = (short)Saturate(xout2 >> 11);
    }
    return output;
  }

  // ── Block 3 SCALEL/SCALEH: derive the per-band quantiser step (det). ─────────────
  private static void ScaleAndAdapt(Band band, int scaleShiftBase) {
    var wd1 = (band.Nb >> 6) & 31;
    var wd2 = scaleShiftBase - (band.Nb >> 11);
    var wd3 = wd2 < 0 ? Ilb[wd1] << -wd2 : Ilb[wd1] >> wd2;
    band.Det = wd3 << 2;
  }

  // ── Block 4: adaptive predictor update (RECONS/PARREC/UPPOL/UPZERO/FILTEP/FILTEZ). ──
  private static void Block4(Band band, int d) {
    band.D[0] = d;
    band.R[0] = Saturate(band.S + d);
    band.P[0] = Saturate(band.Sz + d);

    // UPPOL2.
    for (var i = 0; i < 3; ++i)
      band.Sg[i] = band.P[i] >> 15;
    var wd1 = Saturate(band.A[1] << 2);
    var wd2 = band.Sg[0] == band.Sg[1] ? -wd1 : wd1;
    if (wd2 > 32767)
      wd2 = 32767;
    var wd3 = (wd2 >> 7) + (band.Sg[0] == band.Sg[2] ? 128 : -128);
    wd3 += (band.A[2] * 32512) >> 15;
    band.Ap[2] = Math.Clamp(wd3, -12288, 12288);

    // UPPOL1.
    band.Sg[0] = band.P[0] >> 15;
    band.Sg[1] = band.P[1] >> 15;
    wd1 = band.Sg[0] == band.Sg[1] ? 192 : -192;
    wd2 = (band.A[1] * 32640) >> 15;
    band.Ap[1] = Saturate(wd1 + wd2);
    wd3 = Saturate(15360 - band.Ap[2]);
    if (band.Ap[1] > wd3)
      band.Ap[1] = wd3;
    else if (band.Ap[1] < -wd3)
      band.Ap[1] = -wd3;

    // UPZERO.
    wd1 = d == 0 ? 0 : 128;
    band.Sg[0] = d >> 15;
    for (var i = 1; i < 7; ++i) {
      band.Sg[i] = band.D[i] >> 15;
      wd2 = band.Sg[i] == band.Sg[0] ? wd1 : -wd1;
      wd3 = (band.B[i] * 32640) >> 15;
      band.Bp[i] = Saturate(wd2 + wd3);
    }

    // DELAYA.
    for (var i = 6; i > 0; --i) {
      band.D[i] = band.D[i - 1];
      band.B[i] = band.Bp[i];
    }
    for (var i = 2; i > 0; --i) {
      band.R[i] = band.R[i - 1];
      band.P[i] = band.P[i - 1];
      band.A[i] = band.Ap[i];
    }

    // FILTEP.
    wd1 = Saturate(band.R[1] + band.R[1]);
    wd1 = (band.A[1] * wd1) >> 15;
    wd2 = Saturate(band.R[2] + band.R[2]);
    wd2 = (band.A[2] * wd2) >> 15;
    band.Sp = Saturate(wd1 + wd2);

    // FILTEZ.
    band.Sz = 0;
    for (var i = 6; i > 0; --i) {
      var wd = Saturate(band.D[i] + band.D[i]);
      band.Sz += (band.B[i] * wd) >> 15;
    }
    band.Sz = Saturate(band.Sz);

    // PREDIC.
    band.S = Saturate(band.Sp + band.Sz);
  }

  /// <summary>Clamps to the 16-bit signed range, matching the reference <c>saturate()</c>.</summary>
  private static int Saturate(int amp) =>
    amp > short.MaxValue ? short.MaxValue : amp < short.MinValue ? short.MinValue : amp;
}

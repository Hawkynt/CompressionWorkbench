#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// AC-3 parametric bit-allocation model (ATSC A/52 §7.2.2). Given a channel's decoded exponents and
/// the frame's allocation parameters (decay / gain / floor / snroffset / delta) it computes a
/// power-spectral-density (PSD) envelope, integrates it into a masking curve and derives the
/// bit-allocation pointer (bap) per mantissa. The algorithm and constants follow the A/52 reference
/// pseudo-code (§7.2.2.1–§7.2.2.7) and FFmpeg's <c>ff_ac3_bit_alloc_calc_mask</c> /
/// <c>ff_ac3_bit_alloc_calc_bap</c>.
/// </summary>
public static class Ac3BitAllocation {

  /// <summary>Parameters that drive the masking curve, shared by every channel in a block.</summary>
  public readonly record struct AllocParams(
    int SlowDecay, int FastDecay, int SlowGain, int DbPerBit, int Floor);

  /// <summary>Resolves the coded allocation parameters (sdcycod/fdcycod/sgaincod/dbpbcod/floorcod) to their table values.</summary>
  public static AllocParams Resolve(int sdcycod, int fdcycod, int sgaincod, int dbpbcod, int floorcod)
    => new(Ac3Tables.SlowDecay[sdcycod], Ac3Tables.FastDecay[fdcycod],
           Ac3Tables.SlowGain[sgaincod], Ac3Tables.DbPerBit[dbpbcod], Ac3Tables.Floor[floorcod]);

  /// <summary>
  /// Computes the bit-allocation pointers for one channel over bins
  /// <paramref name="start"/>..<paramref name="end"/>-1. <paramref name="exp"/> holds the decoded
  /// exponents; <paramref name="bap"/> (length ≥ end) receives the per-bin bap.
  /// <paramref name="fgain"/> is the channel fast gain, <paramref name="snrOffset"/> the combined
  /// coarse/fine SNR offset, <paramref name="fscod"/> the sample-rate code (for the hearing
  /// threshold). <paramref name="deltas"/> applies optional delta bit allocation (deltbae/deltba) as
  /// (lengthInBands, gainCode); pass null for none. Coupling channels start the leak integrators
  /// from <paramref name="cplFastLeak"/>/<paramref name="cplSlowLeak"/>.
  /// </summary>
  public static void ComputeBap(
      byte[] exp, byte[] bap, int start, int end,
      AllocParams p, int fgain, int snrOffset, int fscod, bool isCoupling,
      int cplFastLeak, int cplSlowLeak,
      (int Length, int Delta)[]? deltas) {

    if (end <= start)
      return;

    // 1) PSD per bin: psd = 3072 - (exp << 7).
    var psd = new int[256];
    for (var bin = start; bin < end; ++bin)
      psd[bin] = 3072 - (exp[bin] << 7);

    var bandStart = Ac3Tables.BinToBand[start];
    var bandEnd = Ac3Tables.BinToBand[end - 1];

    // 2) Band-integrated PSD (log-domain add via latab).
    var bndpsd = new int[50];
    for (var band = bandStart; band <= bandEnd; ++band) {
      var s = Math.Max((int)Ac3Tables.BandStart[band], start);
      var e = Math.Min((int)Ac3Tables.BandStart[band + 1], end);
      if (e <= s) { bndpsd[band] = 0; continue; }
      var acc = psd[s];
      for (var bin = s + 1; bin < e; ++bin) {
        var max = Math.Max(acc, psd[bin]);
        var diff = Math.Abs(acc - psd[bin]) >> 1;
        var idx = Math.Min(diff, Ac3Tables.LogAdd.Length - 1);
        acc = max + Ac3Tables.LogAdd[idx];
      }
      bndpsd[band] = acc;
    }

    // 3) Excitation / masking curve (A/52 §7.2.2.4).
    var mask = new int[50];
    ComputeMask(bndpsd, mask, bandStart, bandEnd, p, fgain, fscod, isCoupling, cplFastLeak, cplSlowLeak);

    // 4) Delta bit allocation adjustment (A/52 §7.2.2.6).
    if (deltas is { Length: > 0 }) {
      var band = bandStart;
      foreach (var (length, delta) in deltas) {
        var deltaValue = delta >= 4 ? (delta - 8) * 128 : delta * 128;
        for (var i = 0; i < length && band <= bandEnd; ++i, ++band)
          mask[band] += deltaValue;
      }
    }

    // 5) bap per bin from (psd - (mask - snroffset)) (A/52 §7.2.2.7).
    for (var band = bandStart; band <= bandEnd; ++band) {
      var m = mask[band] - snrOffset - p.Floor;
      if (m < 0) m = 0;
      m = (m & 0x1fe0) + p.Floor;
      var bandLow = Math.Max((int)Ac3Tables.BandStart[band], start);
      var bandHigh = Math.Min((int)Ac3Tables.BandStart[band + 1], end);
      for (var bin = bandLow; bin < bandHigh; ++bin) {
        var address = (psd[bin] - m) >> 5;
        address = Math.Clamp(address, 0, 63);
        bap[bin] = BapTab[address];
      }
    }
  }

  // Masking-curve recursion (A/52 §7.2.2.4). Walks band groups in order applying the leaky
  // fast/slow integrators, the low-frequency compensation term and the hearing-threshold floor.
  private static void ComputeMask(
      int[] bndpsd, int[] mask, int bandStart, int bandEnd, AllocParams p,
      int fgain, int fscod, bool isCoupling, int cplFastLeak, int cplSlowLeak) {

    var sgain = p.SlowGain;
    var sdecay = p.SlowDecay;
    var fdecay = p.FastDecay;

    int fastLeak, slowLeak, lowComp;
    var band = bandStart;

    if (isCoupling) {
      fastLeak = cplFastLeak;
      slowLeak = cplSlowLeak;
      lowComp = 0;
    } else {
      // Band 0 initialization.
      lowComp = CalcLowComp(0, bndpsd[band], band + 1 <= bandEnd ? bndpsd[band + 1] : bndpsd[band], band);
      fastLeak = bndpsd[band] + fgain - lowComp;
      slowLeak = bndpsd[band] + sgain;
      var excite0 = fastLeak;
      mask[band] = Math.Max(excite0, bndpsd[band]);
      ++band;
    }

    for (; band <= bandEnd; ++band) {
      if (band < 22) {
        if (!isCoupling) {
          var next = band + 1 <= bandEnd ? bndpsd[band + 1] : bndpsd[band];
          lowComp = CalcLowComp(lowComp, bndpsd[band], next, band);
        }
        fastLeak = Math.Max(fastLeak - fdecay, bndpsd[band] + fgain - lowComp);
        slowLeak = Math.Max(slowLeak - sdecay, bndpsd[band] + sgain);
        mask[band] = Math.Max(fastLeak, slowLeak);
      } else {
        fastLeak = Math.Max(fastLeak - fdecay, bndpsd[band] + fgain);
        slowLeak = Math.Max(slowLeak - sdecay, bndpsd[band] + sgain);
        mask[band] = Math.Max(fastLeak, slowLeak);
      }
    }

    // Hearing-threshold floor.
    var hthFs = Math.Clamp(fscod, 0, 2);
    for (var b = bandStart; b <= bandEnd; ++b) {
      var hth = Ac3Tables.HearingThreshold[Math.Min(b, 49), hthFs];
      if (mask[b] < hth)
        mask[b] = hth;
    }
  }

  // A/52 §7.2.2.4 lowcomp adjustment (calc_lowcomp).
  private static int CalcLowComp(int a, int b0, int b1, int bin) {
    if (bin < 7) {
      if (b0 + 256 == b1) a = 384;
      else if (b0 > b1) a = Math.Max(0, a - 64);
    } else if (bin < 20) {
      if (b0 + 256 == b1) a = 320;
      else if (b0 > b1) a = Math.Max(0, a - 64);
    } else {
      a = Math.Max(0, a - 128);
    }
    return a;
  }

  /// <summary>
  /// bap lookup table (A/52 Table 7.21 / FFmpeg <c>ff_ac3_bap_tab</c>). Indexed by the clamped
  /// (psd-mask)/32 address (0..63) → bit-allocation pointer 0..15.
  /// </summary>
  public static readonly byte[] BapTab = [
    0, 1, 1, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6,
    6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 10,
    10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14,
    14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15, 15,
  ];
}

#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// AC-3 parametric bit-allocation model (ATSC A/52 §7.2.2). Given a channel's decoded exponents and
/// the frame's allocation parameters (decay / gain / knee / floor / snroffset / delta) it computes a
/// power-spectral-density (PSD) envelope, integrates it into a masking curve and derives the
/// bit-allocation pointer (bap) per mantissa. Every step is the spec's fixed-point integer
/// arithmetic verbatim (§7.2.2.2 – §7.2.2.7); encoder and decoder must produce identical baps, so
/// there is no latitude here at all.
/// </summary>
public static class Ac3BitAllocation {

  private const int CriticalBands = 50;

  /// <summary>Parameters that drive the masking curve, shared by every channel in a block.</summary>
  public readonly record struct AllocParams(
    int SlowDecay, int FastDecay, int SlowGain, int DbPerBit, int Floor);

  /// <summary>One delta-bit-allocation segment (A/52 §7.2.2.6): band offset, band count, gain code.</summary>
  public readonly record struct DeltaSegment(int Offset, int Length, int Value);

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
  /// threshold). <paramref name="deltas"/> applies optional delta bit allocation; pass null for
  /// none. The coupling channel (<paramref name="isCoupling"/>) skips the low-frequency excitation
  /// bootstrap and starts its leak integrators from <paramref name="cplFastLeak"/> /
  /// <paramref name="cplSlowLeak"/> instead.
  /// </summary>
  public static void ComputeBap(
      byte[] exp, byte[] bap, int start, int end,
      AllocParams p, int fgain, int snrOffset, int fscod, bool isCoupling,
      int cplFastLeak, int cplSlowLeak,
      DeltaSegment[]? deltas,
      byte[]? bapTable = null) {

    if (end <= start)
      return;

    // A/52 §7.2.2.1.1: when every SNR offset in the block is zero the combined offset lands on
    // -960 and the spec short-circuits the whole routine to bap 0.
    if (snrOffset == -960) {
      Array.Clear(bap, start, end - start);
      return;
    }

    // E-AC-3 AHT channels map the masking address through ff_eac3_hebap_tab instead of the standard
    // A/52 baptab; the masking-curve maths is otherwise identical.
    var table = bapTable ?? BapTab;

    // §7.2.2.2 — exponent mapping into PSD.
    var psd = new int[256];
    for (var bin = start; bin < end; ++bin)
      psd[bin] = 3072 - (exp[bin] << 7);

    // §7.2.2.3 — band integration by log-addition.
    var bndpsd = new int[CriticalBands];
    var bandStart = Ac3Tables.BinToBand[start];
    var bandEnd = Ac3Tables.BinToBand[end - 1] + 1;
    {
      var j = start;
      for (var band = bandStart; band < bandEnd; ++band) {
        var lastBin = Math.Min(Ac3Tables.BandStart[band] + Ac3Tables.BandSize[band], end);
        var acc = psd[j];
        ++j;
        for (; j < lastBin; ++j)
          acc = LogAdd(acc, psd[j]);
        bndpsd[band] = acc;
      }
    }

    // §7.2.2.4 / §7.2.2.5 — excitation function and masking curve.
    var mask = new int[CriticalBands];
    ComputeMask(bndpsd, mask, bandStart, bandEnd, p, fgain, fscod, isCoupling, cplFastLeak, cplSlowLeak);

    // §7.2.2.6 — delta bit allocation.
    if (deltas != null) {
      var band = 0;
      foreach (var seg in deltas) {
        band += seg.Offset;
        var delta = (seg.Value >= 4 ? seg.Value - 3 : seg.Value - 4) << 7;
        for (var i = 0; i < seg.Length && band < CriticalBands; ++i, ++band)
          mask[band] += delta;
      }
    }

    // §7.2.2.7 — masking curve to bap.
    {
      var i = start;
      for (var band = bandStart; band < bandEnd; ++band) {
        var lastBin = Math.Min(Ac3Tables.BandStart[band] + Ac3Tables.BandSize[band], end);
        var m = mask[band] - snrOffset - p.Floor;
        if (m < 0)
          m = 0;
        m = (m & 0x1fe0) + p.Floor;
        for (; i < lastBin; ++i) {
          var address = Math.Clamp((psd[i] - m) >> 5, 0, 63);
          bap[i] = table[address];
        }
      }
    }
  }

  // A/52 §7.2.2.3 logadd(): the larger operand plus a table term keyed on half the difference.
  private static int LogAdd(int a, int b) {
    var address = Math.Min(Math.Abs(a - b) >> 1, 255);
    return Math.Max(a, b) + Ac3Tables.LogAdd[address];
  }

  // A/52 §7.2.2.4 excitation function plus §7.2.2.5 masking curve. Full-bandwidth and LFE channels
  // bootstrap the leak integrators over bands 0..6 and search for the band where the envelope stops
  // falling; the coupling channel starts from the transmitted leak values instead.
  private static void ComputeMask(
      int[] bndpsd, int[] mask, int bandStart, int bandEnd, AllocParams p,
      int fgain, int fscod, bool isCoupling, int cplFastLeak, int cplSlowLeak) {

    var excite = new int[CriticalBands];
    int fastLeak, slowLeak;
    int begin;

    if (!isCoupling) {
      // Bands 0 and 1 are seeded before the loop, then bands 2..6 look one band ahead for the first
      // rising edge. The lfe channel stops one band short (bndend == 7), so its last band must not
      // read bndpsd[7].
      var lowComp = CalcLowComp(0, bndpsd[0], bndpsd[1], 0);
      excite[0] = bndpsd[0] - fgain - lowComp;
      lowComp = CalcLowComp(lowComp, bndpsd[1], bndpsd[2], 1);
      excite[1] = bndpsd[1] - fgain - lowComp;

      begin = 7;
      fastLeak = 0;
      slowLeak = 0;
      for (var band = 2; band < 7; ++band) {
        if (bandEnd != 7 || band != 6)
          lowComp = CalcLowComp(lowComp, bndpsd[band], bndpsd[band + 1], band);
        fastLeak = bndpsd[band] - fgain;
        slowLeak = bndpsd[band] - p.SlowGain;
        excite[band] = fastLeak - lowComp;
        if ((bandEnd != 7 || band != 6) && bndpsd[band] <= bndpsd[band + 1]) {
          begin = band + 1;
          break;
        }
      }

      var limit = Math.Min(bandEnd, 22);
      for (var band = begin; band < limit; ++band) {
        if (bandEnd != 7 || band != 6)
          lowComp = CalcLowComp(lowComp, bndpsd[band], bndpsd[band + 1], band);
        fastLeak = Math.Max(fastLeak - p.FastDecay, bndpsd[band] - fgain);
        slowLeak = Math.Max(slowLeak - p.SlowDecay, bndpsd[band] - p.SlowGain);
        excite[band] = Math.Max(fastLeak - lowComp, slowLeak);
      }
      begin = 22;
    } else {
      begin = bandStart;
      fastLeak = (cplFastLeak << 8) + 768;
      slowLeak = (cplSlowLeak << 8) + 768;
    }

    for (var band = begin; band < bandEnd; ++band) {
      fastLeak = Math.Max(fastLeak - p.FastDecay, bndpsd[band] - fgain);
      slowLeak = Math.Max(slowLeak - p.SlowDecay, bndpsd[band] - p.SlowGain);
      excite[band] = Math.Max(fastLeak, slowLeak);
    }

    // §7.2.2.5 — knee compensation below dbknee, then the hearing threshold floor.
    var hthFs = Math.Clamp(fscod, 0, 2);
    for (var band = bandStart; band < bandEnd; ++band) {
      var e = excite[band];
      if (bndpsd[band] < p.DbPerBit)
        e += (p.DbPerBit - bndpsd[band]) >> 2;
      mask[band] = Math.Max(e, Ac3Tables.HearingThreshold[band, hthFs]);
    }
  }

  // A/52 §7.2.2.4 calc_lowcomp().
  private static int CalcLowComp(int a, int b0, int b1, int band) {
    if (band < 7) {
      if (b0 + 256 == b1)
        a = 384;
      else if (b0 > b1)
        a = Math.Max(0, a - 64);
    } else if (band < 20) {
      if (b0 + 256 == b1)
        a = 320;
      else if (b0 > b1)
        a = Math.Max(0, a - 64);
    } else {
      a = Math.Max(0, a - 128);
    }
    return a;
  }

  /// <summary>
  /// bap lookup table (A/52 Table 7.16, baptab[]). Indexed by the clamped (psd-mask)/32 address
  /// (0..63) → bit-allocation pointer 0..15.
  /// </summary>
  public static readonly byte[] BapTab = [
    0, 1, 1, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6,
    6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 10,
    10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14,
    14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15, 15,
  ];
}

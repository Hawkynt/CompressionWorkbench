#pragma warning disable CS1591

namespace Codec.Ac3;

/// <summary>
/// AC-3 exponent coding (ATSC A/52 §7.1.3). Exponents are differentially coded: an absolute first
/// exponent followed by grouped delta exponents. Three grouping granularities are selected by the
/// exponent strategy — D15 (1 mantissa per exponent), D25 (2 per exponent), D45 (4 per exponent).
/// Each grouped 7-bit word packs three delta exponents, each in the range -2..+2 (coded 0..4).
/// </summary>
public static class Ac3Exponents {

  /// <summary>Exponent strategy codes (A/52 §5.4.2.4).</summary>
  public enum Strategy { Reuse = 0, D15 = 1, D25 = 2, D45 = 3 }

  /// <summary>Mantissas-per-exponent group step for a strategy: D15→1, D25→2, D45→4.</summary>
  public static int GroupSize(Strategy s) => s switch {
    Strategy.D15 => 1, Strategy.D25 => 2, Strategy.D45 => 4, _ => 0,
  };

  /// <summary>
  /// Decodes <paramref name="nGroups"/> grouped delta-exponent words from <paramref name="r"/> into
  /// the exponent array <paramref name="exp"/> starting at bin <paramref name="start"/>, given the
  /// already-read absolute first exponent <paramref name="absExp"/> and the grouping
  /// <paramref name="strategy"/>. Returns the exclusive end bin.
  /// <para>
  /// The absolute exponent occupies exactly one bin (A/52 §7.1.3: <c>exp[0] = absexp</c>), whatever
  /// the grouping; only the differentially coded exponents that follow are replicated across the
  /// pair or quad. Filling the first group with the absolute value instead shifts the whole envelope
  /// up by one or three bins for D25 / D45.
  /// </para>
  /// </summary>
  public static int Decode(Ac3BitReader r, byte[] exp, int start, int absExp, int nGroups, Strategy strategy) {
    var step = GroupSize(strategy);
    var prev = absExp;
    var bin = start;
    exp[bin++] = (byte)prev;

    for (var g = 0; g < nGroups; ++g) {
      var word = (int)r.ReadBits(7);
      // Each 7-bit word holds three delta codes: word = ((d0*5)+d1)*5+d2, d in 0..4.
      var d0 = word / 25;
      var d1 = (word / 5) % 5;
      var d2 = word % 5;
      foreach (var code in stackalloc[] { d0, d1, d2 }) {
        prev = Math.Clamp(prev + code - 2, 0, 24);
        for (var i = 0; i < step && bin < exp.Length; ++i)
          exp[bin++] = (byte)prev;
      }
    }
    return bin;
  }

  /// <summary>
  /// Decodes the coupling channel's exponent set. The coupling channel's absolute exponent is a
  /// decoding reference only, not a real exponent, so it consumes no bin: the expanded exponents
  /// start directly at <paramref name="start"/> (A/52 §7.1.3, <c>cplexp[n + cplstrtmant] = exp[n+1]</c>).
  /// </summary>
  public static void DecodeCoupling(Ac3BitReader r, byte[] exp, int start, int absExp, int nGroups, Strategy strategy) {
    var step = GroupSize(strategy);
    var prev = absExp;
    var bin = start;
    for (var g = 0; g < nGroups; ++g) {
      var word = (int)r.ReadBits(7);
      var d0 = word / 25;
      var d1 = (word / 5) % 5;
      var d2 = word % 5;
      foreach (var code in stackalloc[] { d0, d1, d2 }) {
        prev = Math.Clamp(prev + code - 2, 0, 24);
        for (var i = 0; i < step && bin < exp.Length; ++i)
          exp[bin++] = (byte)prev;
      }
    }
  }

  /// <summary>
  /// Computes the number of grouped exponent words for an independent or coupled channel covering
  /// <paramref name="nExp"/> mantissas: A/52 §7.1.3 gives <c>(endmant-1)/3</c>, <c>(endmant+2)/6</c>
  /// and <c>(endmant+8)/12</c> for D15 / D25 / D45, all truncating — the same value as
  /// <c>ceil((nExp - 1) / (3 * grpsize))</c>.
  /// </summary>
  public static int GroupCount(int nExp, Strategy strategy) {
    var step = GroupSize(strategy);
    if (step == 0 || nExp <= 1) return 0;
    var perWord = 3 * step;
    return (nExp - 1 + perWord - 1) / perWord;
  }
}

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
  public enum Strategy {   /// <summary>
  /// Specifies the reuse option.
  /// </summary>
Reuse = 0,   /// <summary>
  /// Specifies the d 15 option.
  /// </summary>
D15 = 1,   /// <summary>
  /// Specifies the d 25 option.
  /// </summary>
D25 = 2,   /// <summary>
  /// Specifies the d 45 option.
  /// </summary>
D45 = 3 }

  /// <summary>Mantissas-per-exponent group step for a strategy: D15→1, D25→2, D45→4.</summary>
  public static int GroupSize(Strategy s) => s switch {
    Strategy.D15 => 1, Strategy.D25 => 2, Strategy.D45 => 4, _ => 0,
  };

  /// <summary>
  /// Decodes <paramref name="nGroups"/> grouped delta-exponent words from <paramref name="r"/> into
  /// the exponent array <paramref name="exp"/> starting at bin <paramref name="start"/>, given the
  /// already-read absolute first exponent <paramref name="absExp"/> and the grouping
  /// <paramref name="strategy"/>. Returns the exclusive end bin. The result is the full per-bin
  /// exponent array (each group of mantissas shares the group exponent).
  /// </summary>
  public static int Decode(Ac3BitReader r, byte[] exp, int start, int absExp, int nGroups, Strategy strategy) {
    var step = GroupSize(strategy);
    var prev = absExp;
    var bin = start;
    exp[bin++] = (byte)prev;
    // Fill the remaining bins of the first group (D25/D45 repeat the absolute exponent).
    for (var i = 1; i < step; ++i)
      exp[bin++] = (byte)prev;

    for (var g = 0; g < nGroups; ++g) {
      var word = (int)r.ReadBits(7);
      // Each 7-bit word holds three delta codes: word = ((d0*5)+d1)*5+d2, d in 0..4.
      var d0 = word / 25;
      var d1 = (word / 5) % 5;
      var d2 = word % 5;
      foreach (var code in stackalloc[] { d0, d1, d2 }) {
        prev += code - 2;        // delta exponent in -2..+2
        prev = Math.Clamp(prev, 0, 24);
        for (var i = 0; i < step; ++i)
          exp[bin++] = (byte)prev;
      }
    }
    return bin;
  }

  /// <summary>
  /// Computes the number of grouped exponent words for a channel covering <paramref name="nExp"/>
  /// mantissa exponents under <paramref name="strategy"/>: one absolute exponent then
  /// <c>ceil((nExp - 1) / (3 * step))</c> grouped 7-bit words (A/52 §7.1.3).
  /// </summary>
  public static int GroupCount(int nExp, Strategy strategy) {
    var step = GroupSize(strategy);
    if (step == 0 || nExp <= 1) return 0;
    var perWord = 3 * step;
    return (nExp - 1 + perWord - 1) / perWord;
  }
}

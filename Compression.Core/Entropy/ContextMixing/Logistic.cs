namespace Compression.Core.Entropy.ContextMixing;

/// <summary>
/// Fast table-based logistic transforms used by PAQ/lpaq-style context mixing.
/// Probabilities are 12-bit fixed point in the range [0, 4095] (i.e. p/4096),
/// and the stretch domain is the integer logit clamped to [-2047, 2047].
/// </summary>
/// <remarks>
/// <para>
/// <c>stretch(p) = ln(p / (1 - p))</c> maps a probability into the logit
/// (stretch) domain where context-mixing predictions are combined linearly.
/// <c>squash(x) = 1 / (1 + e^-x)</c> is its inverse, mapping a mixed logit
/// back into a probability.
/// </para>
/// <para>
/// Both are precomputed once into lookup tables so the per-bit hot path uses
/// only array indexing — no transcendental calls. The squash table spans the
/// logit range [-2047, 2047]; stretch is built by inverting it so the two are
/// exact mutual inverses on the 12-bit grid (guaranteeing deterministic,
/// loss-free round-trips between coder runs).
/// </para>
/// </remarks>
public static class Logistic {
  /// <summary>Number of probability bits (12 → probabilities scaled to 4096).</summary>
  public const int ProbabilityBits = 12;

  /// <summary>The probability scale (4096); a probability p is stored as round(p * 4096).</summary>
  public const int ProbabilityScale = 1 << ProbabilityBits;

  /// <summary>Inclusive lower bound of the stretch (logit) domain.</summary>
  public const int MinStretch = -2047;

  /// <summary>Inclusive upper bound of the stretch (logit) domain.</summary>
  public const int MaxStretch = 2047;

  // squash[x - MinStretch] = probability in [1, 4095] for logit x.
  private static readonly short[] _SquashTable = BuildSquashTable();

  // stretch[p] = logit in [MinStretch, MaxStretch] for probability p in [0, 4095].
  private static readonly short[] _StretchTable = BuildStretchTable(_SquashTable);

  /// <summary>
  /// Converts a logit (stretch-domain value) back into a 12-bit probability.
  /// </summary>
  /// <param name="logit">The stretch-domain value; clamped to [MinStretch, MaxStretch].</param>
  /// <returns>Probability of bit 1 in [1, 4095] out of 4096.</returns>
  public static int Squash(int logit) {
    if (logit <= Logistic.MinStretch)
      return 1;
    if (logit >= Logistic.MaxStretch)
      return Logistic.ProbabilityScale - 1;

    return Logistic._SquashTable[logit - Logistic.MinStretch];
  }

  /// <summary>
  /// Converts a 12-bit probability into the stretch (logit) domain.
  /// </summary>
  /// <param name="probability">Probability of bit 1 in [0, 4095].</param>
  /// <returns>The logit in [MinStretch, MaxStretch].</returns>
  public static int Stretch(int probability) {
    var p = Math.Clamp(probability, 0, Logistic.ProbabilityScale - 1);
    return Logistic._StretchTable[p];
  }

  private static short[] BuildSquashTable() {
    var span = Logistic.MaxStretch - Logistic.MinStretch + 1;
    var table = new short[span];
    for (var i = 0; i < span; ++i) {
      var x = (Logistic.MinStretch + i) / 256.0; // logit in natural units
      var p = 1.0 / (1.0 + Math.Exp(-x));
      var scaled = (int)Math.Round(p * Logistic.ProbabilityScale);
      table[i] = (short)Math.Clamp(scaled, 1, Logistic.ProbabilityScale - 1);
    }

    return table;
  }

  private static short[] BuildStretchTable(short[] squash) {
    // Invert squash: for each probability find the smallest logit whose squash
    // reaches it. This makes stretch a true inverse on the 12-bit grid.
    var table = new short[Logistic.ProbabilityScale];
    var pos = 0;
    for (var x = Logistic.MinStretch; x <= Logistic.MaxStretch; ++x) {
      var p = squash[x - Logistic.MinStretch];
      while (pos <= p && pos < Logistic.ProbabilityScale)
        table[pos++] = (short)x;
    }

    while (pos < Logistic.ProbabilityScale)
      table[pos++] = Logistic.MaxStretch;

    return table;
  }
}

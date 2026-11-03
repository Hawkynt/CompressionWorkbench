namespace Compression.Core.Entropy.ContextMixing.Mcm;

/// <summary>
/// Constant tables of mcm 0.84's context-mixing coder, copied exactly from the reference
/// (<c>docs/MCM-ON-DISK.md</c>, "Tables"), plus the tables derived from them at start-up.
/// </summary>
internal static partial class McmTables {

  /// <summary>Squash stems for stretched −2048, −1920, …, 0 (the upper half mirrors them).</summary>
  private static ReadOnlySpan<int> SquashStemsLowerHalf => [1, 2, 4, 6, 19, 25, 38, 71, 82, 128, 210, 323, 497, 778, 1142, 1526, 2047];

  /// <summary>Probability (1..4095) for each stretched value −2048..2047, indexed by value + 2048.</summary>
  internal static readonly short[] Squash;

  /// <summary>The 32 768-entry squash lookup that tolerates any mixer output (index = value &amp; 0x7FFF).</summary>
  internal static readonly short[] SquashAny;

  /// <summary>Stretched value for every probability 0..4095.</summary>
  internal static readonly short[] Stretch;

  /// <summary>Renumbered bit-history transitions: next state = <c>StateNext[state * 2 + bit]</c>.</summary>
  internal static readonly byte[] StateNext;

  static McmTables() {
    Span<int> stems = stackalloc int[33];
    SquashStemsLowerHalf.CopyTo(stems);
    for (var i = 17; i < 33; ++i)
      stems[i] = 4096 - stems[32 - i];

    Squash = new short[4096];
    for (var pos = 0; pos < 4096; ++pos) {
      int index = pos / 128, fraction = pos % 128;
      var value = (stems[index] * (128 - fraction) + stems[index + 1] * fraction + 64) / 128;
      Squash[pos] = (short)Math.Clamp(value, 1, 4095);
    }

    Stretch = new short[4096];
    var previous = 0;
    for (var x = -2048; x < 2048; ++x) {
      int probability = Squash[x + 2048];
      for (var j = previous; j < probability; ++j)
        Stretch[j] = (short)x;
      previous = probability;
    }
    for (var j = previous; j < 4096; ++j)
      Stretch[j] = 2047;

    SquashAny = new short[32768];
    for (var i = 0; i < SquashAny.Length; ++i) {
      var value = i >= 16383 ? i - 32768 : i;
      SquashAny[i] = value <= -2048 ? (short)1 : value >= 2048 ? (short)4095 : Squash[value + 2048];
    }

    StateNext = RenumberStates();
  }

  /// <summary>
  /// Depth-first renumbering of <see cref="StateTransitionsBase"/> from state 0 (zero branch first),
  /// numbering from 1; new state 0 then copies new state 1's transitions.
  /// </summary>
  private static byte[] RenumberStates() {
    var baseTable = StateTransitionsBase.ToArray();
    var newNumber = new int[256];
    Array.Fill(newNumber, -1);
    var transitions = new List<(int Zero, int One)> { (1, 1) };

    int Visit(int state) {
      if (newNumber[state] >= 0)
        return newNumber[state];
      var number = transitions.Count;
      newNumber[state] = number;
      transitions.Add((0, 0));
      var zero = Visit(baseTable[state * 2]);
      var one = Visit(baseTable[state * 2 + 1]);
      transitions[number] = (zero, one);
      return number;
    }

    Visit(0);
    if (transitions.Count > 256)
      throw new InvalidOperationException("The mcm state table does not fit 256 states.");
    var next = new byte[512];
    for (var i = 0; i < transitions.Count; ++i) {
      next[i * 2] = (byte)transitions[i].Zero;
      next[i * 2 + 1] = (byte)transitions[i].One;
    }
    next[0] = next[2];
    next[1] = next[3];
    return next;
  }
}

namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// The constants of the Quantum format, as a cabinet carries it.
/// </summary>
/// <remarks>
/// <para>Quantum was never specified publicly. Everything here was measured against
/// libmspack, which is the reference reader, by putting chosen bitstreams into a
/// cabinet and seeing what came out, and then by writing streams and asking whether
/// it accepts them. The derivation is written up in <c>docs/QUANTUM-ON-DISK.md</c>;
/// no other implementation's source was read.</para>
///
/// <para>Every value here is part of the wire format. A reader that disagrees about
/// any of them does not read Quantum.</para>
/// </remarks>
internal static class QuantumConstants {

  /// <summary>Smallest window a cabinet may name, as a power of two.</summary>
  public const int MinWindowBits = 10;

  /// <summary>Largest window a cabinet may name, as a power of two.</summary>
  public const int MaxWindowBits = 21;

  /// <summary>The selector alphabet: four literal models and three kinds of match.</summary>
  public const int SelectorSymbols = 7;

  /// <summary>Each literal model covers a quarter of the byte range.</summary>
  public const int LiteralSymbolsPerModel = 64;

  /// <summary>Symbols in the match length model.</summary>
  public const int LengthSymbols = 27;

  /// <summary>Added to a symbol's frequency each time a model observes it.</summary>
  public const int ModelIncrement = 8;

  /// <summary>A model rescales once its own total passes this.</summary>
  public const int ModelMaxTotal = 3800;

  /// <summary>Shortest run worth coding as a match.</summary>
  public const int MinMatch = 3;

  /// <summary>Longest match the length slots can carry comfortably.</summary>
  public const int MaxMatch = 250;

  /// <summary>A cabinet data block never holds more than this many plain bytes.</summary>
  public const int MaxBlockSize = 32768;

  /// <summary>
  /// The decoder primes itself with sixteen bits and holds them, so a block has to
  /// carry two bytes past its last real bit or the reader runs off the end of it.
  /// </summary>
  public const int TrailingSlackBytes = 2;

  /// <summary>
  /// How many rescales a model takes before the first that sorts it.
  /// </summary>
  public const int RescalesBeforeSort = 4;

  /// <summary>
  /// Rescales between one sorting rescale and the next. The first comes at the
  /// fourth rescale and the second at the fifty-fourth, read out of libmspack's own
  /// table: it holds the order the fourth left behind through every rescale between.
  /// </summary>
  public const int RescalesBetweenSorts = 50;

  /// <summary>Extra bits carried by each distance slot.</summary>
  public static readonly int[] PositionExtraBits = BuildPositionExtraBits();

  /// <summary>The smallest distance-minus-one each slot stands for.</summary>
  public static readonly int[] PositionBases = Accumulate(PositionExtraBits);

  /// <summary>Extra bits carried by each length slot.</summary>
  public static readonly int[] LengthExtraBits =
    [0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 5];

  /// <summary>The smallest length-minus-five each slot stands for.</summary>
  public static readonly int[] LengthBases = Accumulate(LengthExtraBits);

  /// <summary>
  /// How many distance slots a model holds. It follows the window — only slots whose
  /// base the window can reach — but each selector also has a ceiling of its own:
  /// matches of three see 24 slots, matches of four 36, and longer matches the lot.
  /// </summary>
  /// <param name="selector">The selector, 4, 5 or 6.</param>
  /// <param name="windowBits">The folder's window, as a power of two.</param>
  /// <returns>The number of symbols in that selector's distance model.</returns>
  public static int PositionSlots(int selector, int windowBits) {
    var window = 1 << windowBits;
    var reachable = 0;
    while (reachable < PositionBases.Length && PositionBases[reachable] < window)
      ++reachable;

    var ceiling = selector switch { 4 => 24, 5 => 36, _ => PositionBases.Length };
    return Math.Min(reachable, ceiling);
  }

  /// <summary>Finds the slot that carries a distance, and what is left over.</summary>
  /// <param name="distance">The match distance, one or more.</param>
  /// <returns>The slot and the value its extra bits must spell.</returns>
  public static (int Slot, int Extra) PositionSlot(int distance) {
    var value = distance - 1;
    for (var slot = PositionBases.Length - 1; slot >= 0; --slot)
      if (PositionBases[slot] <= value)
        return (slot, value - PositionBases[slot]);

    throw new ArgumentOutOfRangeException(nameof(distance), distance, "No Quantum slot carries this distance.");
  }

  /// <summary>Finds the slot that carries a match length, and what is left over.</summary>
  /// <param name="length">The match length, five or more.</param>
  /// <returns>The slot and the value its extra bits must spell.</returns>
  public static (int Slot, int Extra) LengthSlot(int length) {
    var value = length - 5;
    for (var slot = LengthBases.Length - 1; slot >= 0; --slot)
      if (LengthBases[slot] <= value)
        return (slot, value - LengthBases[slot]);

    throw new ArgumentOutOfRangeException(nameof(length), length, "No Quantum slot carries this length.");
  }

  /// <summary>The selector that introduces a match of the given length.</summary>
  /// <param name="length">The match length.</param>
  /// <returns>4, 5 or 6.</returns>
  public static int SelectorForLength(int length) => length switch { 3 => 4, 4 => 5, _ => 6 };

  private static int[] BuildPositionExtraBits() {
    // four slots of no extra bits, then two slots at each width up to nineteen
    var bits = new List<int> { 0, 0, 0, 0 };
    for (var width = 1; width <= 19; ++width) {
      bits.Add(width);
      bits.Add(width);
    }

    return [.. bits];
  }

  private static int[] Accumulate(int[] extraBits) {
    var bases = new int[extraBits.Length];
    var run = 0;
    for (var i = 0; i < extraBits.Length; ++i) {
      bases[i] = run;
      run += 1 << extraBits[i];
    }

    return bases;
  }
}

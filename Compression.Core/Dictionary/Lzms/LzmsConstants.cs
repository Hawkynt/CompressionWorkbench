namespace Compression.Core.Dictionary.Lzms;

/// <summary>
/// The tables LZMS is built on, measured against wimlib's own streams.
/// </summary>
/// <remarks>
/// LZMS has no published specification. Everything here was derived by
/// compressing payloads whose factorisation was known in advance and reading
/// the streams back; the derivation is written up in <c>docs/LZMS-ON-DISK.md</c>.
/// </remarks>
internal static class LzmsConstants {

  /// <summary>Recent LZ offsets the format keeps, seeded with 1, 2 and 3.</summary>
  public const int NumRecentLzOffsets = 3;

  /// <summary>Bits of probability resolution the range coder works at.</summary>
  public const int NumProbBits = 6;

  /// <summary>Total probability range.</summary>
  public const int ProbDenominator = 1 << NumProbBits;

  /// <summary>
  /// The bit history a probability starts with: sixteen ones in sixty-four bits,
  /// so the first prediction is 48/64 and not the even split one would guess.
  /// </summary>
  public const ulong InitialRecentBits = 0x0000000055555555UL;

  /// <summary>Literal codes are rebuilt after this many literals.</summary>
  public const int LiteralRebuildInterval = 1024;

  /// <summary>Offset codes are rebuilt after this many offsets.</summary>
  public const int LzOffsetRebuildInterval = 1024;

  /// <summary>Length codes are rebuilt after this many lengths - half the others.</summary>
  public const int LengthRebuildInterval = 512;

  /// <summary>
  /// Delta powers. A delta match names a span as a power of two, so a table of
  /// four-byte entries is power two.
  /// </summary>
  public const int NumDeltaPowers = 8;

  /// <summary>Number of literal symbols.</summary>
  public const int NumLiteralSymbols = 256;

  /// <summary>The length alphabet does not vary with the resource size.</summary>
  public const int NumLengthSlots = 54;

  /// <summary>Shortest match the format can name.</summary>
  public const int MinMatchLength = 2;

  /// <summary>Number of bits of item history the main state is indexed by.</summary>
  public const int MainStateBits = 4;

  /// <summary>
  /// Bits of its own history that pick the probability for the LZ-or-delta bit.
  /// Measured by writing chunks with a growing number of deltas: a history of n
  /// bits carries n+1 of them and then wimlib rejects the resource, and only five
  /// carries every length tried.
  /// </summary>
  public const int MatchKindStateBits = 5;

  /// <summary>Bits of its own history behind the explicit-or-repeat bit of a delta.</summary>
  public const int DeltaExplicitStateBits = 6;

  /// <summary>
  /// Bits of its own history behind the explicit-or-repeat bit of an LZ match, which
  /// is as wide as the delta one. Measured by writing chunks with a growing run of
  /// repeat matches over a run of one byte, where every distance gives the same bytes
  /// so only the coding is under test: a history of n bits carries n+1 of them, and
  /// six carries every length tried, up to thirty, where seven and wider fail at
  /// twelve.
  /// </summary>
  public const int LzExplicitStateBits = 6;

  /// <summary>
  /// Bits of its own history behind each bit of a repeat's unary index, on both
  /// sides. Measured the same way, over a run of one byte, where every distance and
  /// every span reproduce the payload so nothing but the coding is under test.
  /// </summary>
  public const int RepeatIndexStateBits = 6;

  /// <summary>Recent delta references a repeat may name.</summary>
  public const int NumRecentDeltas = 4;

  private static (int Base, int Width)[] Build((int Width, int Count)[] schedule) {
    var slots = new List<(int, int)>();
    var next = 1;
    foreach (var (width, count) in schedule)
      for (var i = 0; i < count; ++i) {
        slots.Add((next, width));
        next += width;
      }
    return slots.ToArray();
  }

  /// <summary>
  /// Offset slots. The widths double as the distances grow, but not at regular
  /// intervals - the run lengths below were measured and reproduce the alphabet
  /// size wimlib uses at every resource size that was checked.
  /// </summary>
  public static readonly (int Base, int Width)[] OffsetSlots = Build([
    (1, 8), (4, 9), (8, 7), (16, 10), (32, 15), (64, 15),
    (128, 20), (256, 20), (512, 30), (1024, 33), (2048, 40),
  ]);

  /// <summary>
  /// Length slots. The last but one is very wide - sixteen extra bits - and the last
  /// is not a length at all: see <see cref="RunToEndLengthSlot"/>.
  /// </summary>
  public static readonly (int Base, int Width)[] LengthSlots = BuildLengthSlots();

  private static (int Base, int Width)[] BuildLengthSlots() {
    var slots = new List<(int, int)>(Build([
      (1, 26), (2, 4), (4, 6), (8, 4), (16, 5), (32, 2),
      (64, 1), (128, 1), (256, 1), (512, 1), (1024, 1),
    ]));
    slots.Add((2219, 1 << 16));
    slots.Add((2219 + (1 << 16), 1 << 16));
    return slots.ToArray();
  }

  /// <summary>
  /// The last length symbol, which does not name a length: a match carrying it runs
  /// to the end of the chunk.
  /// </summary>
  /// <remarks>
  /// It reads as a length of 67755 plus sixteen extra bits, and that reading survives
  /// every payload whose matches are shorter, because nothing else uses the symbol.
  /// Three things say what it really means, and only together: writing it is refused
  /// whenever an item follows it and accepted when it is the last, which is what a
  /// run-to-the-end match would do; wimlib's own chunk for a long repeating text
  /// carries it and decodes byte-exact under this reading and no other; and it makes
  /// that chunk two bytes smaller than the two explicit matches it would otherwise
  /// need, which is exactly the gap that could not be accounted for.
  /// </remarks>
  public const int RunToEndLengthSlot = 53;

  /// <summary>Longest match this writer names outright, the last the slots below cover.</summary>
  public const int MaxMatchLength = 67754;

  /// <summary>
  /// How many offset slots a resource of this size uses: just enough to reach
  /// the largest distance it can hold, which is its size less one.
  /// </summary>
  public static int OffsetSlotCount(int uncompressedSize) {
    // A resource of one byte can hold no distance at all, but the alphabet still
    // has to exist for the code to be built.
    if (uncompressedSize <= 1) return 1;

    var last = uncompressedSize - 1;
    for (var i = 0; i < OffsetSlots.Length; ++i) {
      var (b, w) = OffsetSlots[i];
      if (last >= b && last < b + w) return i + 1;
    }
    throw new ArgumentOutOfRangeException(nameof(uncompressedSize));
  }

  /// <summary>Finds the slot a distance or length falls in.</summary>
  public static int SlotOf((int Base, int Width)[] slots, int value) {
    int lo = 0, hi = slots.Length - 1;
    while (lo < hi) {
      var mid = (lo + hi + 1) / 2;
      if (slots[mid].Base <= value) lo = mid;
      else hi = mid - 1;
    }
    return lo;
  }

  /// <summary>Number of extra bits a slot of this width carries.</summary>
  public static int ExtraBits(int width) {
    var bits = 0;
    while ((1 << bits) < width) ++bits;
    return bits;
  }
}

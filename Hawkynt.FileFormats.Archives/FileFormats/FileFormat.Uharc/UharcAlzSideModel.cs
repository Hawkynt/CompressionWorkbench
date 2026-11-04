namespace FileFormat.Uharc;

/// <summary>
/// ALZ's multimedia predictors, selected by the stream's selector 1..9
/// (docs/UHARC-ON-DISK.md, "ALZ multimedia selectors").
/// </summary>
/// <remarks>
/// While a selector is active, literals are coded as a signed difference from a predicted
/// byte (<see cref="Bias"/>, sign <see cref="Negate"/>) in a context (<see cref="Context"/>)
/// chosen by the predictor. Every output byte, literal or copied, is fed back through
/// <see cref="Observe"/>. Selectors 1..4 predict from the byte 1..4 positions back, 3 adds a
/// gradient predictor for 24-bit pixels, 5 and 6 are adaptive linear predictors over 8-bit
/// samples with a pitch search, 7..9 are per-channel linear predictors for 16-bit and
/// interleaved data.
/// </remarks>
internal sealed class UharcAlzSideModel {

  // Per selector 0..9: record size, first context, and the history shift of the base byte.
  private static readonly int[] Period = [1, 1, 2, 3, 4, 1, 1, 2, 2, 4];
  private static readonly int[] FirstContext = [0, 0x301, 0x321, 0x341, 0x3A1, 0x3E1, 0x3E5, 0x3E9, 0x3F1, 0x3F4];
  private static readonly int[] BaseShift = [0, 0, 8, 16, 24, 0, 0, 0, 0, 0];

  // Selector 1: which half of the context space follows a given delta class.
  private static readonly byte[] PhaseOfClass = [
    0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0,
  ];

  // Selector 3: delta -> context offset (16 is reserved for the gradient predictor).
  private static readonly byte[] TripleDeltaClass = [
    0, 1, 2, 3, 4, 5, 6, 7, 7, 8, 8, 9, 9, 10, 10, 10, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13,
    13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 15,
    15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
    15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
    17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
    17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17, 17,
    17, 17, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 19, 19, 19, 19, 19, 19, 19,
    19, 19, 19, 19, 20, 20, 20, 20, 20, 20, 20, 20, 21, 21, 21, 21, 21, 22, 22, 22, 23, 23, 24, 24, 25, 25, 26, 27, 28, 29, 30, 31,
  ];

  // Selectors 5/6: delta -> |delta| / 4 class for the flatness signature.
  private static readonly byte[] ShortDeltaClass = [
    0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7,
    8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11, 12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 15, 15, 15, 15,
    16, 16, 16, 16, 17, 17, 17, 17, 18, 18, 18, 18, 19, 19, 19, 19, 20, 20, 20, 20, 21, 21, 21, 21, 22, 22, 22, 22, 23, 23, 23, 23,
    24, 24, 24, 24, 25, 25, 25, 25, 26, 26, 26, 26, 27, 27, 27, 27, 28, 28, 28, 28, 29, 29, 29, 29, 30, 30, 30, 30, 31, 31, 31, 31,
    32, 31, 31, 31, 31, 30, 30, 30, 30, 29, 29, 29, 29, 28, 28, 28, 28, 27, 27, 27, 27, 26, 26, 26, 26, 25, 25, 25, 25, 24, 24, 24,
    24, 23, 23, 23, 23, 22, 22, 22, 22, 21, 21, 21, 21, 20, 20, 20, 20, 19, 19, 19, 19, 18, 18, 18, 18, 17, 17, 17, 17, 16, 16, 16,
    16, 15, 15, 15, 15, 14, 14, 14, 14, 13, 13, 13, 13, 12, 12, 12, 12, 11, 11, 11, 11, 10, 10, 10, 10, 9, 9, 9, 9, 8, 8, 8,
    8, 7, 7, 7, 7, 6, 6, 6, 6, 5, 5, 5, 5, 4, 4, 4, 4, 3, 3, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 0, 0, 0,
  ];

  // Selectors 5/6: delta -> |delta| / 16 class for the flatness signature.
  private static readonly byte[] CoarseDeltaClass = [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
    6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
    8, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6,
    6, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
  ];

  // Selectors 5/6: the pitch search runs every (mask + 1) samples, by best candidate.
  private static readonly byte[] ScanInterval = [
    127, 127, 127, 127, 7, 3,
  ];

  // Selector 7: delta -> |delta| / 8 class.
  private static readonly byte[] PairDeltaClass = [
    0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3,
    4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7,
    8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11,
    12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15,
    16, 15, 15, 15, 15, 15, 15, 15, 15, 14, 14, 14, 14, 14, 14, 14, 14, 13, 13, 13, 13, 13, 13, 13, 13, 12, 12, 12, 12, 12, 12, 12,
    12, 11, 11, 11, 11, 11, 11, 11, 11, 10, 10, 10, 10, 10, 10, 10, 10, 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8, 8, 8,
    8, 7, 7, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4,
    4, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0,
  ];

  // Selector 7: delta -> |delta| / 64 class.
  private static readonly byte[] PairCoarseClass = [
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
    1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
  ];

  // Selectors 5/6: cost of a 9-bit residual, ceil(sqrt(|r|)).
  private static readonly byte[] ResidualCost = [
    0, 1, 2, 2, 2, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6,
    6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    8, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
    10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12,
    12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13,
    13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14,
    14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
    15, 15, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16,
    16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 16, 15,
    15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 14, 14, 14, 14,
    14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 13, 13, 13, 13, 13, 13, 13, 13, 13,
    13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 13, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
    12, 12, 12, 12, 12, 12, 12, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 10, 10, 10, 10,
    10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6,
    6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5, 5, 5, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 2, 2, 2, 1,
  ];

  // Selectors 1, 2, 4: delta -> one of 16 classes (Fibonacci-spaced magnitude bands).
  private static readonly byte[] DeltaClass = [
    0, 1, 1, 2, 2, 2, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
    6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7,
    7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8,
    8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9,
    9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
    10, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12, 12, 12, 12, 13, 13, 13, 13, 13, 14, 14, 14, 15, 15,
  ];

  // Selectors 7..9: cost of a prediction error, by its byte value.
  private static readonly byte[] ErrorCost = [
    0, 1, 2, 3, 4, 5, 5, 6, 6, 6, 7, 7, 7, 7, 7, 8, 8, 8, 8, 8, 8, 8, 8, 9, 9, 9, 9, 9, 9, 9, 9, 9,
    9, 9, 9, 9, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11, 11,
    11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 12, 12, 12, 12, 12,
    12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
    12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12,
    12, 12, 12, 12, 12, 12, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
    11, 11, 11, 11, 11, 11, 11, 11, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 9, 9, 9,
    9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8, 8, 8, 8, 7, 7, 7, 7, 7, 6, 6, 6, 5, 5, 4, 3, 2, 1,
  ];

  private int _selector;
  private int _phase;
  private long _written;

  // Linear predictors, one per phase (selectors 5..9 use these).
  private readonly int[,] _history = new int[10, 5];
  private readonly int[,] _weight = new int[10, 5];
  private readonly int[] _last = new int[10];
  private readonly int[] _delta = new int[10];
  private readonly int[,] _trial = new int[10, 11];
  private readonly int[] _predictionCost = new int[10];
  private readonly int[] _lastCost = new int[10];
  private readonly int[] _predictionCostMark = new int[10];
  private readonly int[] _lastCostMark = new int[10];
  private readonly uint[] _count = new uint[10];
  private int _crossDelta;
  private int _crossDeltaOther;
  private int _prediction;

  // Pitch search and candidate predictors (selectors 5 and 6).
  private readonly int[] _lagScore = new int[124];
  private readonly int[] _deltaRing = new int[128];
  private readonly int[] _candidate = new int[6];
  private readonly int[,] _residual = new int[6, 257];
  private int _bestCandidate;
  private int _bestLag;
  private int _closestLag;
  private int _current;
  private int _previous;

  // Recent delta classes per phase; a zero word means "flat" (selectors 5..9).
  private readonly uint[] _signature = new uint[16];
  private readonly uint[] _signatureFine = new uint[16];
  private readonly uint[] _signatureCoarse = new uint[16];
  private readonly byte[] _phaseNegate = new byte[16];

  // Gradient predictor for selector 3.
  private readonly int[] _gradientHits = new int[8];
  private readonly byte[] _gradientPrediction = new byte[8];
  private int _gradientStreak;
  private int _gradientSlot;
  private bool _gradientActive;
  private bool _gradientNegateSaved;

  /// <summary>Gets the active selector (0 = no predictor).</summary>
  public int Selector => this._selector;

  /// <summary>Gets the byte the next literal is coded relative to.</summary>
  public byte Bias { get; private set; }

  /// <summary>Gets whether the next literal is coded as <c>Bias - value</c> rather than <c>value - Bias</c>.</summary>
  public bool Negate { get; private set; }

  /// <summary>Gets the literal context for the next byte.</summary>
  public int Context { get; private set; }

  /// <summary>Gets the escape context for the next byte.</summary>
  public int EscapeContext => 0x402 + this._selector;

  /// <summary>Switches to <paramref name="selector"/> (1..9, or 0 to switch the predictors off).</summary>
  public void Select(int selector) {
    this._selector = selector;
    this.Context = FirstContext[selector];
    this.Negate = false;
    this.Bias = 0;
    this._phase = 0;
    if (selector < 5)
      return;

    Array.Clear(this._history);
    Array.Clear(this._weight);
    Array.Clear(this._last);
    Array.Clear(this._delta);
    Array.Clear(this._trial);
    Array.Clear(this._predictionCost);
    Array.Clear(this._lastCost);
    Array.Clear(this._predictionCostMark);
    Array.Clear(this._lastCostMark);
    Array.Clear(this._count);
    this._crossDelta = 0;
    this._crossDeltaOther = 0;
    this._prediction = 0;
    Array.Clear(this._lagScore);
    Array.Clear(this._deltaRing);
    Array.Clear(this._candidate);
    Array.Clear(this._residual);
    this._current = 0;
    this._previous = 0;
    this._bestCandidate = 0;
    this._bestLag = 0;
    this._closestLag = 2;
    for (var i = 0; i < 16; ++i) {
      this._phaseNegate[i] = 0;
      this._signature[i] = i <= 3 ? uint.MaxValue : 0;
      this._signatureFine[i] = i <= 3 ? uint.MaxValue : 0;
      this._signatureCoarse[i] = i <= 3 ? uint.MaxValue : 0;
    }
  }

  /// <summary>Maps a decoded difference back to the byte it stands for.</summary>
  public byte ToByte(int difference) => this.Negate ? (byte)(this.Bias - difference) : (byte)(this.Bias + difference);

  /// <summary>Maps a byte to the difference it is coded as.</summary>
  public byte ToDifference(int value) => this.Negate ? (byte)(this.Bias - value) : (byte)(value - this.Bias);

  /// <summary>Feeds one output byte to the predictor.</summary>
  /// <param name="value">The byte just written.</param>
  /// <param name="history">The last four output bytes, <paramref name="value"/> in the low byte.</param>
  /// <param name="written">The number of bytes written so far, including <paramref name="value"/>.</param>
  public void Observe(byte value, uint history, long written) {
    this._written = written;
    var difference = this.ToDifference(value);
    if (difference >= 0x80)
      this.Negate = !this.Negate;

    var period = Period[this._selector];
    var phaseBefore = this._phase;
    var phaseAfter = (int)(written % period);
    switch (this._selector) {
      case 1:
        this.Context = 0x301 + (PhaseOfClass[(this.Context - 0x301) & 0xF] << 4) + DeltaClass[difference];
        break;
      case 2:
        this.Context = 0x321 + (phaseBefore << 4) + DeltaClass[difference];
        break;
      case 4:
        this.Context = 0x3A1 + (phaseBefore << 4) + DeltaClass[difference];
        break;
      case 3:
        this.ObserveGradient(value, history, difference, phaseBefore, phaseAfter);
        return;
      case 5:
      case 6:
        this.UpdatePitchPredictor(this._selector == 5 ? (byte)(value ^ 0x80) : value, difference);
        this._phase = phaseAfter;
        this.Bias = (byte)history;
        this.ScorePitchPredictor();
        return;
      case 7: {
        this.UpdateLinear(phaseBefore & 0xF, (byte)(value ^ 0x80), difference);
        this._phase = phaseAfter;
        this.ScoreLinear(phaseAfter);
        this.Bias += 0x80;
        var context = 0x3E9 + phaseAfter;
        if (this._signature[phaseAfter & 0xF] == 0)
          context += 2;
        else if (this._signatureFine[phaseAfter & 0xF] == 0)
          context += 4;
        else if (this._signatureCoarse[phaseAfter & 0xF] == 0)
          context += 6;
        this.Context = context;
        this.Negate = this._phaseNegate[phaseAfter & 0xF] != 0;
        return;
      }
      default:
        this.UpdateLinear(phaseBefore & 0xF, value, difference);
        this._phase = phaseAfter;
        this.ScoreLinear(phaseAfter);
        this.Context = FirstContext[this._selector] + (this._signature[phaseAfter & 0xF] != 0 ? phaseAfter : period);
        this.Negate = this._phaseNegate[phaseAfter & 0xF] != 0;
        return;
    }

    this._phase = phaseAfter;
    this.Bias = (byte)(history >> BaseShift[this._selector]);
  }

  // ── Selector 3: byte three back, or a gradient once it keeps hitting ───

  private void ObserveGradient(byte value, uint history, byte difference, int phaseBefore, int phaseAfter) {
    int classIndex = difference;
    if (this._gradientActive) {
      var reference = (int)(history >> 24) & 0xFF;
      var offset = (value - reference) & 0xFF;
      this.Negate = this._gradientNegateSaved;
      if (this.Negate)
        offset = -offset & 0xFF;
      if (offset >= 0x80)
        this.Negate = !this.Negate;
      classIndex = offset;
    }

    if (value == this._gradientPrediction[this._gradientSlot])
      ++this._gradientHits[this._gradientSlot];
    else
      this._gradientHits[this._gradientSlot] >>= 1;

    this.Context = 0x341 + (phaseBefore << 5) + TripleDeltaClass[classIndex & 0xFF];
    this._phase = phaseAfter;
    this.Bias = (byte)(history >> 16);

    var slot = (int)(this._written % 6);
    this._gradientSlot = slot;
    var confident = this._gradientHits[slot] > 8;
    var prediction = (byte)(this.Bias + (history & 0xFF) - ((history >> 24) & 0xFF));
    this._gradientPrediction[slot] = prediction;
    this._gradientActive = confident && this._gradientStreak < 5;
    if (this._gradientActive) {
      this._gradientNegateSaved = this.Negate;
      this.Context = 0x351 + (phaseBefore << 5);
      this.Bias = prediction;
      this.Negate = false;
    }

    this._gradientStreak = confident ? this._gradientStreak + 1 : 0;
  }

  // ── Selectors 7..9: per-phase adaptive linear prediction ───────────────

  private static int Fold(int value) {
    var v = value & 0xFFF;
    return v <= 0x800 ? v : 0x1000 - v;
  }

  private static int Signed(byte value) => (sbyte)value;

  private void AdaptWeights(int phase) {
    var best = 0;
    var minimum = this._trial[phase, 0];
    this._trial[phase, 0] = 0;
    for (var i = 1; i <= 10; ++i) {
      var v = this._trial[phase, i];
      if (minimum > v) {
        minimum = v;
        best = i;
      }

      this._trial[phase, i] = 0;
    }

    // Trial k > 0 nudges weight (k - 1) / 2 down (odd k) or up (even k), within +-32. The fifth
    // weight is gated on the fourth's bound, which is what the reference decoder does.
    if (best == 0)
      return;

    var index = (best - 1) >> 1;
    var gate = Math.Min(index, 3);
    if ((best & 1) == 1) {
      if (this._weight[phase, gate] > -0x20)
        --this._weight[phase, index];
    } else if (this._weight[phase, gate] < 0x20)
      ++this._weight[phase, index];
  }

  private void AccumulateTrials(int phase, int baseError, int extra) {
    this._trial[phase, 0] += Fold(baseError);
    for (var k = 0; k < 4; ++k) {
      this._trial[phase, 1 + 2 * k] += Fold(baseError - this._history[phase, k]);
      this._trial[phase, 2 + 2 * k] += Fold(baseError + this._history[phase, k]);
    }

    this._trial[phase, 9] += Fold(baseError - extra);
    this._trial[phase, 10] += Fold(baseError + extra);
  }

  private void UpdateLinear(int phase, byte value, byte difference) {
    this._phaseNegate[phase] = this.Negate ? (byte)1 : (byte)0;
    var sample = Signed(value);
    var baseError = Signed((byte)(this._prediction - sample)) << 4;
    this.AccumulateTrials(phase, baseError, this._crossDelta);

    this._predictionCost[phase] += ErrorCost[(byte)(this._prediction - sample)];
    this._lastCost[phase] += ErrorCost[(byte)(this._last[phase] - sample)];
    this._delta[phase] = Signed((byte)(sample - this._last[phase]));
    this._crossDelta = this._delta[phase];
    ++this._count[phase];
    this._last[phase] = sample;

    if ((this._count[phase] & 0xF) == 0) {
      this.AdaptWeights(phase);
      if ((this._count[phase] & 0xFF) == 0) {
        this._predictionCost[phase] -= this._predictionCostMark[phase];
        this._predictionCostMark[phase] = this._predictionCost[phase];
        this._lastCost[phase] -= this._lastCostMark[phase];
        this._lastCostMark[phase] = this._lastCost[phase];
      }
    }

    if (this._selector == 9)
      (this._crossDelta, this._crossDeltaOther) = (this._crossDeltaOther, this._crossDelta);

    this._signature[phase] = (this._signature[phase] << 8) + difference;
    if (this._selector == 7) {
      this._signatureFine[phase] = (this._signatureFine[phase] << 8) + PairDeltaClass[difference];
      this._signatureCoarse[phase] = (this._signatureCoarse[phase] << 8) + PairCoarseClass[difference];
    }
  }

  private void ScoreLinear(int phase) {
    var h1 = this._history[phase, 1];
    var h2 = this._history[phase, 2];
    var change = this._delta[phase] - this._history[phase, 0];
    var score = (this._last[phase] << 4)
      + this._weight[phase, 0] * this._delta[phase]
      + this._weight[phase, 1] * change
      + this._weight[phase, 2] * h1
      + this._weight[phase, 3] * h2
      + this._weight[phase, 4] * this._crossDelta;

    this._history[phase, 3] = h2;
    this._history[phase, 2] = h1;
    this._history[phase, 1] = change;
    this._history[phase, 0] = this._delta[phase];

    score /= 16;
    this._prediction = score;
    this.Bias = this._predictionCost[phase] > this._lastCost[phase] ? (byte)this._last[phase] : (byte)score;
  }

  // ── Selectors 5 and 6: 8-bit samples with pitch search ─────────────────

  private void UpdatePitchPredictor(byte value, byte difference) {
    var position = (int)(this._written - 1);
    var sample = Signed(value);
    var ringDelta = sample - this._current;
    var baseError = Signed((byte)(this._candidate[3] - sample)) << 4;
    this.AccumulateTrials(0, baseError, this._history[0, 4]);

    ++this._count[0];
    this._delta[0] = Signed((byte)(sample - this._last[0]));
    this._last[0] = sample;
    if ((this._count[0] & 0xF) == 0)
      this.AdaptWeights(0);

    this._deltaRing[position & 0x7F] = ringDelta;
    if ((ScanInterval[this._bestCandidate] & position) == 0) {
      this._bestLag = 2;
      this._closestLag = 2;
      var closest = 0xFFFF;
      for (var lag = 2; lag <= 123; ++lag) {
        var d1 = (this._deltaRing[(position - 1) & 0x7F] - this._deltaRing[(position - 1 - lag) & 0x7F]) & 0xFFF;
        var d2 = (this._deltaRing[position & 0x7F] - this._deltaRing[(position - lag) & 0x7F]) & 0xFFF;
        var d3 = (this._deltaRing[(position - 2) & 0x7F] - this._deltaRing[(position - 2 - lag) & 0x7F]) & 0xFFF;
        var sum = Fold(d1) + Fold(d2) + Fold(d3);
        this._lagScore[lag] = (int)((uint)(this._lagScore[lag] * 13) >> 4) + sum;
        if (this._lagScore[lag] < this._lagScore[this._bestLag])
          this._bestLag = lag;
        if (sum < closest) {
          closest = sum;
          this._closestLag = lag;
        }
      }
    }

    Span<int> residual = [
      sample - this._candidate[0], sample - this._candidate[1], sample - this._candidate[2], sample - this._candidate[3],
      ringDelta - this._candidate[4], ringDelta - this._candidate[5],
    ];
    this._bestCandidate = 0;
    var slot = position & 0xFF;
    for (var k = 0; k < 6; ++k) {
      this._residual[k, 256] -= this._residual[k, slot];
      var cost = ResidualCost[residual[k] & 0x1FF];
      this._residual[k, slot] = cost;
      this._residual[k, 256] += cost;
      if (this._residual[k, 256] < this._residual[this._bestCandidate, 256])
        this._bestCandidate = k;
    }

    this._previous = this._current;
    this._current = sample;
    this._signature[0] = (this._signature[0] << 8) + difference;
    this._signatureFine[0] = (this._signatureFine[0] << 8) + ShortDeltaClass[difference];
    this._signatureCoarse[0] = (this._signatureCoarse[0] << 8) + CoarseDeltaClass[difference];
  }

  private void ScorePitchPredictor() {
    this._candidate[0] = 0;
    this._candidate[1] = this._current;
    this._candidate[2] = 2 * this._current - this._previous;

    var pitched = this._deltaRing[(int)(this._written - this._bestLag) & 0x7F];
    var h1 = this._history[0, 1];
    var h2 = this._history[0, 2];
    var change = this._delta[0] - this._history[0, 0];
    this._history[0, 4] = pitched;
    this._history[0, 3] = h2;
    this._history[0, 2] = h1;
    this._history[0, 1] = change;
    this._history[0, 0] = this._delta[0];

    var score = (this._last[0] << 4)
      + this._weight[0, 0] * this._delta[0]
      + this._weight[0, 1] * change
      + this._weight[0, 2] * h1
      + this._weight[0, 3] * h2
      + this._weight[0, 4] * pitched;
    this._candidate[3] = score / 16;
    this._candidate[4] = pitched;
    this._candidate[5] = this._deltaRing[(int)(this._written - this._closestLag) & 0x7F];

    var bias = (byte)this._candidate[this._bestCandidate];
    if (this._bestCandidate >= 4)
      bias += (byte)this._current;

    int context;
    if (this._selector == 5) {
      bias += 0x80;
      context = 0x3E1;
    } else
      context = 0x3E5;

    if (this._signature[0] == 0)
      context += 1;
    else if (this._signatureFine[0] == 0)
      context += 2;
    else if (this._signatureCoarse[0] == 0)
      context += 3;

    this.Bias = bias;
    this.Context = context;
  }
}

namespace Compression.Core.Entropy.Fse;

/// <summary>
/// FSE entropy encoder using tANS (table-based Asymmetric Numeral Systems).
/// Encodes symbols from back to front, producing a backward bitstream.
/// </summary>
/// <remarks>
/// The encoding process is the exact inverse of decoding. During decoding, each
/// state transition reads bits and moves to a new state. During encoding, we
/// determine which decoder state to target and output the corresponding bits.
/// A precomputed lookup table provides O(1) encoding per symbol.
/// </remarks>
public sealed class FseEncoder {
  private readonly int _tableLog;
  private readonly int _tableSize;

  // Precomputed encoding table: for each (symbol, targetState) pair,
  // stores the decoder state, nbBits, and bits to output in separate arrays.
  // For symbols with 0 count, the arrays are null.
  private readonly int[]?[] _encDecoderState;
  private readonly int[]?[] _encNbBits;
  private readonly int[]?[] _encBitsOut;

  /// <summary>
  /// Initializes a new FSE encoder from normalized counts.
  /// </summary>
  /// <param name="normalizedCounts">Normalized frequency array (see <see cref="FseTable.Build"/>).</param>
  /// <param name="maxSymbol">The maximum symbol value present.</param>
  /// <param name="tableLog">The log2 of the table size.</param>
  public FseEncoder(short[] normalizedCounts, int maxSymbol, int tableLog) {
    this._tableLog = tableLog;
    this._tableSize = 1 << tableLog;

    var effectiveCounts = new int[maxSymbol + 1];
    for (var s = 0; s <= maxSymbol; ++s)
      effectiveCounts[s] = normalizedCounts[s] switch {
        -1 => 1,
        > 0 => normalizedCounts[s],
        _ => effectiveCounts[s]
      };

    var decTable = FseTable.Build(normalizedCounts, maxSymbol, tableLog);

    // For each symbol, collect decoder table states (sorted)
    var statesForSymbol = new List<int>[maxSymbol + 1];
    for (var s = 0; s <= maxSymbol; ++s)
      statesForSymbol[s] = [];

    for (var state = 0; state < this._tableSize; ++state)
      statesForSymbol[decTable.Symbol[state]].Add(state);

    for (var s = 0; s <= maxSymbol; ++s)
      statesForSymbol[s].Sort();

    // Build the O(1) encoding lookup table
    this._encDecoderState = new int[]?[maxSymbol + 1];
    this._encNbBits = new int[]?[maxSymbol + 1];
    this._encBitsOut = new int[]?[maxSymbol + 1];

    for (var s = 0; s <= maxSymbol; ++s) {
      if (effectiveCounts[s] == 0)
        continue;

      this._encDecoderState[s] = new int[this._tableSize];
      this._encNbBits[s] = new int[this._tableSize];
      this._encBitsOut[s] = new int[this._tableSize];

      // For each decoder state of this symbol, compute which target states it covers
      foreach (var d in statesForSymbol[s]) {
        var nbBits = decTable.NumBits[d];
        var baseVal = decTable.NewStateBase[d];
        var range = 1 << nbBits;

        // This decoder state covers target states [baseVal, baseVal + range)
        for (var bits = 0; bits < range; ++bits) {
          var targetState = baseVal + bits;
          if (targetState < 0 || targetState >= this._tableSize)
            continue;

          this._encDecoderState[s]![targetState] = d;
          this._encNbBits[s]![targetState] = nbBits;
          this._encBitsOut[s]![targetState] = bits;
        }
      }
    }
  }

  /// <summary>
  /// Normalizes raw frequency counts to sum to <c>1 &lt;&lt; tableLog</c>.
  /// Every symbol with a non-zero count gets at least one table entry
  /// (normalized count of -1 for sub-probability, or a positive value).
  /// </summary>
  /// <param name="counts">Raw frequency counts indexed by symbol.</param>
  /// <param name="maxSymbol">The maximum symbol value to consider.</param>
  /// <param name="tableLog">The log2 of the target table size.</param>
  /// <returns>Normalized counts array where -1 means sub-probability (1 entry), 0 means absent.</returns>
  /// <exception cref="ArgumentException">
  /// No symbols with non-zero frequency, or more distinct symbols than the table has entries.
  /// </exception>
  /// <remarks>
  /// The table has to hold every symbol that occurs -- a symbol with no entry cannot be coded at all
  /// -- so the entries are handed out as one per present symbol first and the rest in proportion to
  /// the counts. Scaling proportionally and repairing the total afterwards does not work: with an
  /// alphabet near the table's size the proportional shares already overspend the table, and taking
  /// the difference back out of one symbol drives it to zero or below.
  /// </remarks>
  public static short[] NormalizeCounts(int[] counts, int maxSymbol, int tableLog) {
    var tableSize = 1 << tableLog;
    var total = 0L;
    var nonZeroCount = 0;

    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] <= 0)
        continue;

      total += counts[s];
      ++nonZeroCount;
    }

    if (total == 0)
      throw new ArgumentException("At least one symbol must have a non-zero count.", nameof(counts));

    if (nonZeroCount > tableSize)
      throw new ArgumentException(
        $"{nonZeroCount} distinct symbols do not fit a table of {tableSize} entries; raise the table log.",
        nameof(tableLog));

    var normalized = new short[maxSymbol + 1];

    // Special case: single symbol gets all entries
    if (nonZeroCount == 1)
      for (var s = 0; s <= maxSymbol; ++s)
        if (counts[s] > 0) {
          normalized[s] = (short)tableSize;
          return normalized;
        }

    // One entry per present symbol is reserved; what is left over is what the counts compete for.
    var budget = tableSize - nonZeroCount;
    var slots = new int[maxSymbol + 1];
    var handedOut = 0;

    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] <= 0)
        continue;

      // Rounded down, so the shares can never overspend the budget.
      var share = (int)((long)counts[s] * budget / total);
      slots[s] = 1 + share;
      handedOut += share;
    }

    // Rounding down leaves entries unspent; they go to the symbols that lost the most to it, which
    // is the largest-remainder method -- deterministic, and it never takes an entry away again.
    DistributeRoundingRemainder(slots, counts, maxSymbol, total, budget, budget - handedOut);

    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] <= 0)
        continue;

      // A symbol whose share of the table is below a single entry still gets one, and says so: the
      // decoder places those at the top of the table before spreading the rest.
      normalized[s] = slots[s] == 1 && (long)counts[s] * tableSize < total ? (short)-1 : (short)slots[s];
    }

    return normalized;
  }

  /// <summary>
  /// Hands the entries that flooring left unspent to the symbols with the largest fractional shares.
  /// </summary>
  private static void DistributeRoundingRemainder(int[] slots, int[] counts, int maxSymbol, long total, int budget, int remaining) {
    if (remaining <= 0)
      return;

    // What flooring discarded, kept as the numerator over the common denominator so the comparison
    // stays exact. Ties go to the more frequent symbol and then to the lower symbol value, which
    // makes the outcome depend on the counts alone rather than on iteration order.
    var candidates = new List<int>(maxSymbol + 1);
    var fractions = new long[maxSymbol + 1];
    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] <= 0)
        continue;

      fractions[s] = (long)counts[s] * budget % total;
      candidates.Add(s);
    }

    candidates.Sort((left, right) => {
      var byFraction = fractions[right].CompareTo(fractions[left]);
      if (byFraction != 0)
        return byFraction;

      var byCount = counts[right].CompareTo(counts[left]);
      return byCount != 0 ? byCount : left.CompareTo(right);
    });

    for (var i = 0; i < remaining; ++i)
      ++slots[candidates[i]];
  }

  /// <summary>
  /// Writes normalized counts to a byte array using a compact header format.
  /// The header stores the tableLog and maxSymbol, followed by the count values
  /// encoded as 16-bit signed integers.
  /// </summary>
  /// <param name="output">The output buffer.</param>
  /// <param name="outputPos">The starting position in the output buffer.</param>
  /// <param name="normalizedCounts">The normalized frequency counts.</param>
  /// <param name="maxSymbol">The maximum symbol value.</param>
  /// <param name="tableLog">The table log value.</param>
  /// <returns>The number of bytes written.</returns>
  public static int WriteNormalizedCounts(byte[] output, int outputPos, short[] normalizedCounts, int maxSymbol, int tableLog) {
    var startPos = outputPos;

    // Write header: tableLog (1 byte) + maxSymbol (2 bytes, little-endian)
    output[outputPos++] = (byte)tableLog;
    output[outputPos++] = (byte)maxSymbol;
    output[outputPos++] = (byte)(maxSymbol >> 8);

    // Write each count as a 16-bit signed value (little-endian)
    for (var s = 0; s <= maxSymbol; ++s) {
      var value = normalizedCounts[s];
      output[outputPos++] = (byte)value;
      output[outputPos++] = (byte)(value >> 8);
    }

    return outputPos - startPos;
  }

  /// <summary>
  /// Encodes a sequence of bytes using FSE and returns the compressed data.
  /// FSE encodes symbols from back to front and produces a backward bitstream.
  /// The output includes a sentinel bit to mark the bitstream boundary.
  /// </summary>
  /// <param name="data">The data to encode.</param>
  /// <returns>The compressed byte array.</returns>
  /// <exception cref="InvalidOperationException">A symbol with zero frequency is encountered.</exception>
  public byte[] Encode(ReadOnlySpan<byte> data) {
    if (data.Length == 0)
      return [];

    var outputBytes = new List<byte>();
    ulong bitContainer = 0;
    var bitCount = 0;

    // Initialize state for the last symbol (which the decoder will output last)
    var lastSymbol = data[^1];
    var lastDecState = this._encDecoderState[lastSymbol]
      ?? throw new InvalidOperationException($"Cannot encode symbol {lastSymbol} with zero frequency.");
    // Use state 0 as initial target; extract the decoder state from the lookup
    var state = lastDecState[0];

    // Encode from second-to-last symbol back to first
    for (var i = data.Length - 2; i >= 0; --i) {
      var symbol = data[i];
      var decState = this._encDecoderState[symbol]
        ?? throw new InvalidOperationException($"Cannot encode symbol {symbol} with zero frequency.");

      var nbBits = this._encNbBits[symbol]![state];
      var bitsToOutput = this._encBitsOut[symbol]![state];

      // Output bits
      if (nbBits > 0) {
        bitContainer |= (ulong)(uint)bitsToOutput << bitCount;
        bitCount += nbBits;
      }

      // Flush complete bytes
      while (bitCount >= 8) {
        outputBytes.Add((byte)(bitContainer & 0xFF));
        bitContainer >>= 8;
        bitCount -= 8;
      }

      // Transition to new state
      state = decState[state];
    }

    // Output the final state (tableLog bits) + sentinel bit
    bitContainer |= (ulong)(state & (this._tableSize - 1)) << bitCount;
    bitCount += this._tableLog;
    bitContainer |= 1UL << bitCount;
    ++bitCount;

    // Flush all remaining bits
    while (bitCount > 0) {
      outputBytes.Add((byte)bitContainer);
      bitContainer >>= 8;
      bitCount -= 8;
    }

    return outputBytes.ToArray();
  }

}

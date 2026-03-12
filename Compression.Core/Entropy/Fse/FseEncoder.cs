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
  /// <exception cref="ArgumentException">No symbols with non-zero frequency.</exception>
  public static short[] NormalizeCounts(int[] counts, int maxSymbol, int tableLog) {
    var tableSize = 1 << tableLog;
    var total = 0L;
    var nonZeroCount = 0;

    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] <= 0)
        continue;

      total += counts[s];
      nonZeroCount++;
    }

    if (total == 0)
      throw new ArgumentException("At least one symbol must have a non-zero count.", nameof(counts));

    var normalized = new short[maxSymbol + 1];

    // Special case: single symbol gets all entries
    if (nonZeroCount == 1)
      for (var s = 0; s <= maxSymbol; ++s)
        if (counts[s] > 0) {
          normalized[s] = (short)tableSize;
          return normalized;
        }

    // First pass: compute proportional counts
    var distributed = 0;
    var largestSymbol = -1;
    var largestCount = 0L;

    for (var s = 0; s <= maxSymbol; ++s) {
      if (counts[s] == 0) {
        normalized[s] = 0;
        continue;
      }

      if (counts[s] > largestCount) {
        largestCount = counts[s];
        largestSymbol = s;
      }

      // Proportional scaling with rounding
      var proportional = ((long)counts[s] * tableSize + total / 2) / total;

      if (proportional < 1) {
        // Sub-probability: gets exactly 1 table slot
        normalized[s] = -1;
        distributed += 1;
      }
      else {
        normalized[s] = (short)proportional;
        distributed += (int)proportional;
      }
    }

    // Adjust: the sum must equal tableSize
    // Give the remainder to the largest symbol
    var remainder = tableSize - distributed;
    if (largestSymbol < 0)
      return normalized;

    normalized[largestSymbol] += (short)remainder;

    // Safety: if the largest symbol ended up at 0 or below, fix it
    if (normalized[largestSymbol] >= 1)
      return normalized;

    normalized[largestSymbol] = 1;
    RedistributeExcess(normalized, maxSymbol, tableSize);

    return normalized;
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
    bitCount++;

    // Flush all remaining bits
    while (bitCount > 0) {
      outputBytes.Add((byte)bitContainer);
      bitContainer >>= 8;
      bitCount -= 8;
    }

    return outputBytes.ToArray();
  }

  /// <summary>
  /// Redistributes excess counts to ensure the sum equals the table size.
  /// </summary>
  private static void RedistributeExcess(short[] normalized, int maxSymbol, int tableSize) {
    var sum = 0;
    for (var s = 0; s <= maxSymbol; ++s)
      switch (normalized[s]) {
        case -1: sum += 1; break;
        case > 0: sum += normalized[s]; break;
      }

    var diff = tableSize - sum;
    if (diff == 0) return;

    var largest = -1;
    short largestVal = 0;
    for (var s = 0; s <= maxSymbol; ++s)
      if (normalized[s] > largestVal) {
        largestVal = normalized[s];
        largest = s;
      }

    if (largest >= 0)
      normalized[largest] += (short)diff;
  }
}

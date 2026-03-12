using System.Numerics;

namespace Compression.Core.Entropy.Fse;

/// <summary>
/// FSE decoding table built from normalized frequency counts.
/// Each entry stores the output symbol, the number of bits to read for the
/// state transition, and the base value for computing the next state.
/// </summary>
public sealed class FseTable {
  /// <summary>Gets the table log (log2 of table size).</summary>
  public int TableLog { get; }

  /// <summary>Gets the number of bits to read for each state transition.</summary>
  public int[] NumBits { get; }

  /// <summary>Gets the symbol output for each state.</summary>
  public byte[] Symbol { get; }

  /// <summary>Gets the base value for computing the next state.</summary>
  public int[] NewStateBase { get; }

  private FseTable(int tableLog, int tableSize) {
    this.TableLog = tableLog;
    this.NumBits = new int[tableSize];
    this.Symbol = new byte[tableSize];
    this.NewStateBase = new int[tableSize];
  }

  /// <summary>
  /// Builds an FSE decoding table from normalized counts.
  /// </summary>
  /// <param name="normalizedCounts">
  /// Normalized frequency array where index is the symbol. A value of -1 means the symbol
  /// has sub-probability (exactly 1 table entry). A value of 0 means the symbol is absent.
  /// Positive values represent the number of table entries for that symbol.
  /// </param>
  /// <param name="maxSymbol">The maximum symbol value present in the data.</param>
  /// <param name="tableLog">The log2 of the table size.</param>
  /// <returns>A fully constructed FSE decoding table.</returns>
  /// <exception cref="ArgumentException">The parameters are invalid.</exception>
  public static FseTable Build(short[] normalizedCounts, int maxSymbol, int tableLog) {
    var tableSize = 1 << tableLog;
    var table = new FseTable(tableLog, tableSize);

    // Step 1: Symbol spreading
    // Symbols with count == -1 get placed at high positions first
    var highThreshold = tableSize - 1;

    // Compute the effective counts (for spreading) and place -1 symbols at the end
    var effectiveCounts = new int[maxSymbol + 1];
    for (var symbol = 0; symbol <= maxSymbol; ++symbol)
      if (normalizedCounts[symbol] == -1) {
        table.Symbol[highThreshold--] = (byte)symbol;
        effectiveCounts[symbol] = 1;
      } else
        effectiveCounts[symbol] = normalizedCounts[symbol];

    // Spread remaining symbols using step = (tableSize >> 1) + (tableSize >> 3) + 3
    // This step is coprime with tableSize for all power-of-2 sizes >= 32
    var step = (tableSize >> 1) + (tableSize >> 3) + 3;
    var mask = tableSize - 1;

    // For very small tables where step might be a multiple of tableSize,
    // fall back to a coprime step
    if ((step & mask) == 0)
      step = (tableSize >> 1) + 1;

    var pos = 0;

    for (var symbol = 0; symbol <= maxSymbol; ++symbol) {
      int count = normalizedCounts[symbol];
      if (count <= 0)
        continue; // skip -1 (already placed) and 0 (absent)

      for (var i = 0; i < count; ++i) {
        table.Symbol[pos] = (byte)symbol;
        // Advance to next position, skipping high-threshold positions
        do {
          pos = (pos + step) & mask;
        }
        while (pos > highThreshold);
      }
    }

    // Step 2: Build decoding entries
    // symbolNext[s] starts at the effective count for that symbol
    var symbolNext = new int[maxSymbol + 1];
    for (var symbol = 0; symbol <= maxSymbol; ++symbol)
      symbolNext[symbol] = effectiveCounts[symbol];

    for (var state = 0; state < tableSize; ++state) {
      var symbol = table.Symbol[state];
      var nextState = symbolNext[symbol]++;
      var numBits = tableLog - BitOperations.Log2((uint)nextState);
      table.NumBits[state] = numBits;
      table.NewStateBase[state] = (nextState << numBits) - tableSize;
    }

    return table;
  }
}

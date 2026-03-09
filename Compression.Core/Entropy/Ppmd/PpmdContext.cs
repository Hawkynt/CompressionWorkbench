namespace Compression.Core.Entropy.Ppmd;

/// <summary>
/// Represents a context node in the PPMd context tree.
/// Each context holds a frequency distribution for symbols that follow the context's
/// byte sequence, along with an escape frequency for novel symbols.
/// Uses PPM Method D escape estimation: the escape frequency equals the number of
/// distinct symbols observed in this context.
/// </summary>
internal sealed class PpmdContext {
  /// <summary>Symbol-to-frequency mapping for this context.</summary>
  private readonly Dictionary<byte, int> _frequencies = new();

  /// <summary>
  /// Gets the escape frequency for this context.
  /// Uses Method D: escape frequency = number of distinct symbols observed.
  /// This ensures contexts with many different symbols have higher escape probability,
  /// while contexts with few unique symbols give more probability to known symbols.
  /// Minimum is 1 so that escape is always possible.
  /// </summary>
  public int EscapeFreq => Math.Max(1, this._frequencies.Count);

  /// <summary>
  /// Gets the total frequency across all symbols plus the escape frequency.
  /// </summary>
  public int TotalFreq {
    get {
      int sum = EscapeFreq;
      foreach (int f in this._frequencies.Values)
        sum += f;

      return sum;
    }
  }

  /// <summary>
  /// Gets the number of distinct symbols observed in this context.
  /// </summary>
  public int SymbolCount => this._frequencies.Count;

  /// <summary>
  /// Gets the frequency table (read-only view).
  /// </summary>
  public IReadOnlyDictionary<byte, int> Frequencies => this._frequencies;

  /// <summary>
  /// Gets the frequency of the specified symbol, or zero if not observed.
  /// </summary>
  /// <param name="symbol">The symbol to look up.</param>
  /// <returns>The frequency of the symbol, or zero.</returns>
  public int GetFreq(byte symbol) => this._frequencies.GetValueOrDefault(symbol, 0);

  /// <summary>
  /// Increments the frequency of the specified symbol. If the symbol has not been
  /// seen before, it is added with frequency 1.
  /// </summary>
  /// <param name="symbol">The symbol whose frequency to increment.</param>
  public void IncrementFreq(byte symbol) => this._frequencies[symbol] = this._frequencies.GetValueOrDefault(symbol, 0) + 1;

  /// <summary>
  /// Sets the frequency of the specified symbol to a given value.
  /// </summary>
  /// <param name="symbol">The symbol whose frequency to set.</param>
  /// <param name="freq">The new frequency value.</param>
  public void SetFreq(byte symbol, int freq) {
    if (freq <= 0)
      this._frequencies.Remove(symbol);
    else
      this._frequencies[symbol] = freq;
  }

  /// <summary>
  /// Rescales all frequencies by halving them. Any frequency that drops to zero
  /// causes the symbol to be removed.
  /// </summary>
  public void Rescale() {
    var toRemove = new List<byte>();
    var keys = new List<byte>(this._frequencies.Keys);

    foreach (byte sym in keys) {
      int newFreq = (this._frequencies[sym] + 1) / 2;
      if (newFreq <= 0)
        toRemove.Add(sym);
      else
        this._frequencies[sym] = newFreq;
    }

    foreach (byte sym in toRemove)
      this._frequencies.Remove(sym);
  }

  /// <summary>
  /// Builds a sorted list of (symbol, cumFreq, freq) entries for encoding/decoding.
  /// Symbols are sorted by byte value for deterministic ordering.
  /// The escape is appended as the last entry.
  /// </summary>
  /// <param name="excludedSymbols">Set of symbols to exclude from the table (already coded at higher orders).</param>
  /// <returns>A list of tuples: (symbol or -1 for escape, cumulative frequency, frequency).</returns>
  public List<(int Symbol, uint CumFreq, uint Freq)> BuildCodingTable(HashSet<byte>? excludedSymbols = null) {
    var result = new List<(int Symbol, uint CumFreq, uint Freq)>();
    uint cumFreq = 0;
    int includedCount = 0;

    // Sort symbols for deterministic ordering
    var sortedSymbols = new List<byte>(this._frequencies.Keys);
    sortedSymbols.Sort();

    foreach (byte sym in sortedSymbols) {
      if (excludedSymbols != null && excludedSymbols.Contains(sym))
        continue;

      uint f = (uint)this._frequencies[sym];
      result.Add((sym, cumFreq, f));
      cumFreq += f;
      ++includedCount;
    }

    // Escape entry: Method D escape = number of included distinct symbols (min 1)
    uint escapeFreq = (uint)Math.Max(1, includedCount);
    result.Add((-1, cumFreq, escapeFreq));
    return result;
  }
}

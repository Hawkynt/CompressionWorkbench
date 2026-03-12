namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Adaptive symbol frequency model for the Quantum compression format.
/// Tracks per-symbol counts and maintains cumulative frequency tables that
/// update as symbols are processed, with periodic rescaling when the total
/// frequency exceeds <see cref="QuantumConstants.RescaleThreshold"/>.
/// </summary>
internal sealed class QuantumModel {
  private readonly int[] _freq;
  private readonly int[] _cumFreq;

  /// <summary>Gets the number of symbols in this model.</summary>
  public int NumSymbols { get; }

  /// <summary>Gets the current total frequency.</summary>
  public int TotalFrequency { get; private set; }

  /// <summary>
  /// Initializes a new <see cref="QuantumModel"/> with uniform initial frequencies.
  /// </summary>
  /// <param name="numSymbols">The number of symbols in the alphabet.</param>
  public QuantumModel(int numSymbols) {
    this.NumSymbols = numSymbols;
    this._freq = new int[numSymbols];
    this._cumFreq = new int[numSymbols + 1];

    for (var i = 0; i < numSymbols; ++i)
      this._freq[i] = 1;

    this.TotalFrequency = numSymbols;
    this.RebuildCumulative();
  }

  /// <summary>
  /// Gets the cumulative frequency for a symbol (lower bound of its range).
  /// </summary>
  /// <param name="symbol">The symbol index.</param>
  /// <returns>The cumulative frequency.</returns>
  public int GetCumulativeFrequency(int symbol) => this._cumFreq[symbol];

  /// <summary>
  /// Gets the frequency of a symbol.
  /// </summary>
  /// <param name="symbol">The symbol index.</param>
  /// <returns>The symbol frequency.</returns>
  public int GetFrequency(int symbol) => this._freq[symbol];

  /// <summary>
  /// Looks up a symbol from a scaled cumulative count.
  /// </summary>
  /// <param name="count">The scaled count (0 &lt;= count &lt; TotalFrequency).</param>
  /// <returns>The symbol index.</returns>
  public int FindSymbol(int count) {
    // Binary search for the symbol whose cumulative range contains count
    int lo = 0, hi = this.NumSymbols - 1;
    while (lo < hi) {
      var mid = (lo + hi) >> 1;
      if (this._cumFreq[mid + 1] <= count)
        lo = mid + 1;
      else
        hi = mid;
    }
    return lo;
  }

  /// <summary>
  /// Updates the model after decoding a symbol, incrementing its frequency
  /// and rescaling if the total exceeds the threshold.
  /// </summary>
  /// <param name="symbol">The decoded symbol.</param>
  public void Update(int symbol) {
    ++this._freq[symbol];
    ++this.TotalFrequency;

    if (this.TotalFrequency >= QuantumConstants.RescaleThreshold)
      this.Rescale();
    else
      this.RebuildCumulative();
  }

  private void Rescale() {
    this.TotalFrequency = 0;
    for (var i = 0; i < this.NumSymbols; ++i) {
      this._freq[i] = (this._freq[i] + 1) >> 1; // halve, minimum 1
      this.TotalFrequency += this._freq[i];
    }

    this.RebuildCumulative();
  }

  private void RebuildCumulative() {
    this._cumFreq[0] = 0;
    for (var i = 0; i < this.NumSymbols; ++i)
      this._cumFreq[i + 1] = this._cumFreq[i] + this._freq[i];
  }
}

namespace Compression.Core.Entropy.Arithmetic;

/// <summary>
/// Adaptive frequency model for use with <see cref="ArithmeticEncoder"/>
/// and <see cref="ArithmeticDecoder"/>. Tracks symbol frequencies and
/// maintains cumulative frequency tables that update as symbols are processed.
/// </summary>
public sealed class AdaptiveModel {
  private readonly int[] _freq;
  private readonly int[] _cumFreq;

  /// <summary>Maximum total frequency before rescaling.</summary>
  private const int MaxTotalFreq = 16384;

  /// <summary>
  /// Initializes a new <see cref="AdaptiveModel"/> with uniform initial frequencies.
  /// </summary>
  /// <param name="numSymbols">The number of symbols in the alphabet.</param>
  public AdaptiveModel(int numSymbols) {
    this.NumSymbols = numSymbols;
    this._freq = new int[numSymbols];
    this._cumFreq = new int[numSymbols + 1];

    // Start with uniform distribution
    for (var i = 0; i < numSymbols; ++i)
      this._freq[i] = 1;

    this.TotalFrequency = numSymbols;
    this.RebuildCumulative();
  }

  /// <summary>Gets the total frequency count.</summary>
  public int TotalFrequency { get; private set; }

  /// <summary>Gets the number of symbols.</summary>
  public int NumSymbols { get; }

  /// <summary>
  /// Gets the cumulative frequency for a symbol (lower bound).
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
  /// Looks up a symbol from a cumulative count value.
  /// </summary>
  /// <param name="count">The cumulative count (from the decoder).</param>
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
  /// Updates the model after encoding/decoding a symbol.
  /// </summary>
  /// <param name="symbol">The symbol that was processed.</param>
  public void Update(int symbol) {
    this._freq[symbol]++;
    this.TotalFrequency++;

    if (this.TotalFrequency >= AdaptiveModel.MaxTotalFreq)
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

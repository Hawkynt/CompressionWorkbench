namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// Adaptive symbol frequency model driving the Quantum arithmetic coder.
/// </summary>
/// <remarks>
/// Every symbol starts with frequency 1. Observing a symbol adds
/// <see cref="QuantumConstants.ModelIncrement"/> to its frequency; once the total
/// exceeds the configured maximum every frequency is halved (never below 1), which
/// both bounds the total and lets the model track changing statistics.
/// </remarks>
internal sealed class QuantumModel {
  private readonly int[] _freq;
  private readonly int _increment;
  private readonly int _maxTotal;

  /// <summary>Gets the number of symbols in this model.</summary>
  public int NumSymbols { get; }

  /// <summary>Gets the current total frequency.</summary>
  public int TotalFrequency { get; private set; }

  /// <summary>
  /// Initializes a new <see cref="QuantumModel"/> with uniform initial frequencies.
  /// </summary>
  /// <param name="numSymbols">The number of symbols in the alphabet.</param>
  /// <param name="maxTotal">The total frequency at which all counts are halved.</param>
  /// <param name="increment">The amount added to a symbol's frequency when observed.</param>
  public QuantumModel(
    int numSymbols,
    int maxTotal = QuantumConstants.ModelMaxTotal,
    int increment = QuantumConstants.ModelIncrement) {
    ArgumentOutOfRangeException.ThrowIfLessThan(numSymbols, 1, nameof(numSymbols));
    ArgumentOutOfRangeException.ThrowIfLessThan(maxTotal, numSymbols, nameof(maxTotal));
    ArgumentOutOfRangeException.ThrowIfLessThan(increment, 1, nameof(increment));

    this.NumSymbols = numSymbols;
    this._increment = increment;
    this._maxTotal = maxTotal;
    this._freq = new int[numSymbols];
    Array.Fill(this._freq, 1);
    this.TotalFrequency = numSymbols;
  }

  /// <summary>Gets the frequency of a symbol.</summary>
  /// <param name="symbol">The symbol index.</param>
  /// <returns>The symbol frequency.</returns>
  public int GetFrequency(int symbol) => this._freq[symbol];

  /// <summary>
  /// Gets the sum of the frequencies of all symbols below <paramref name="symbol"/>,
  /// which is the lower bound of that symbol's coding sub-range.
  /// </summary>
  /// <param name="symbol">The symbol index.</param>
  /// <returns>The cumulative frequency below the symbol.</returns>
  public int CumulativeBelow(int symbol) {
    var sum = 0;
    for (var i = 0; i < symbol; ++i)
      sum += this._freq[i];

    return sum;
  }

  /// <summary>
  /// Finds the symbol whose sub-range contains a scaled cumulative count, by scanning
  /// upwards from symbol 0 so that the result is fully determined by the frequencies.
  /// </summary>
  /// <param name="scaled">The scaled count (0 &lt;= scaled &lt; TotalFrequency).</param>
  /// <param name="cumulativeBelow">Receives the cumulative frequency below the symbol.</param>
  /// <returns>The symbol index.</returns>
  /// <exception cref="InvalidDataException">The count lies outside the model's range.</exception>
  public int FindSymbol(int scaled, out int cumulativeBelow) {
    var cumulative = 0;
    for (var symbol = 0; symbol < this.NumSymbols; ++symbol) {
      var frequency = this._freq[symbol];
      if (cumulative + frequency > scaled) {
        cumulativeBelow = cumulative;
        return symbol;
      }

      cumulative += frequency;
    }

    throw new InvalidDataException(
      $"Scaled count {scaled} is outside the model's total frequency {this.TotalFrequency}.");
  }

  /// <summary>
  /// Updates the model after coding a symbol, incrementing its frequency and halving
  /// all frequencies once the total exceeds the configured maximum.
  /// </summary>
  /// <param name="symbol">The coded symbol.</param>
  public void Update(int symbol) {
    this._freq[symbol] += this._increment;
    this.TotalFrequency += this._increment;

    if (this.TotalFrequency <= this._maxTotal)
      return;

    var total = 0;
    for (var i = 0; i < this.NumSymbols; ++i) {
      var halved = this._freq[i] / 2;
      this._freq[i] = halved == 0 ? 1 : halved;
      total += this._freq[i];
    }

    this.TotalFrequency = total;
  }
}

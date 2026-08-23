namespace Compression.Core.Dictionary.Quantum;

/// <summary>
/// One of Quantum's adaptive models: an alphabet, a frequency for each symbol, and
/// the rule that keeps the frequencies from growing without bound.
/// </summary>
/// <remarks>
/// <para>Symbols sit in a fixed order and are <em>not</em> kept sorted by frequency,
/// which is what the published descriptions of Quantum say and what measuring says
/// they do not do: a model that reorders on every update decodes nothing at all. They
/// are sorted once every so many rescales instead - see <see cref="SortingRescale"/>.</para>
///
/// <para>The rescale is the part worth reading twice. It does not halve the
/// frequencies; it halves the <em>cumulative</em> counts, rounding down, and then
/// walks the result back to strictly decreasing so that no symbol is left with a
/// count of zero and therefore no longer codeable. Halving frequencies individually
/// differs only in rounding, and that difference is enough to make a decode part
/// company with libmspack at the first rescale.</para>
/// </remarks>
internal sealed class QuantumModel {
  private readonly int[] _frequencies;
  private readonly int[] _symbols;
  private int _sortsIn = QuantumConstants.RescalesBeforeSort;

  /// <summary>Gets the number of symbols in this model.</summary>
  public int SymbolCount => this._symbols.Length;

  /// <summary>Gets the sum of every symbol's frequency.</summary>
  public int TotalFrequency { get; private set; }

  /// <summary>Gets how many times this model has rescaled.</summary>
  public int Rescales { get; private set; }

  /// <summary>
  /// Whether the very next symbol coded against this model would carry it into the
  /// rescale that sorts it.
  /// </summary>
  public bool NextUpdateWouldSort
    => this.Rescales == QuantumConstants.RescalesBeforeSort - 1
       && this.TotalFrequency + QuantumConstants.ModelIncrement > QuantumConstants.ModelMaxTotal;

  /// <summary>
  /// Initializes a model whose symbols run from <paramref name="firstSymbol"/> upwards,
  /// each starting at a frequency of one.
  /// </summary>
  /// <param name="symbolCount">The size of the alphabet.</param>
  /// <param name="firstSymbol">The value of the first symbol.</param>
  public QuantumModel(int symbolCount, int firstSymbol = 0) {
    ArgumentOutOfRangeException.ThrowIfLessThan(symbolCount, 1, nameof(symbolCount));

    this._symbols = new int[symbolCount];
    for (var i = 0; i < symbolCount; ++i)
      this._symbols[i] = firstSymbol + i;

    this._frequencies = new int[symbolCount];
    Array.Fill(this._frequencies, 1);
    this.TotalFrequency = symbolCount;
  }

  /// <summary>The symbol sitting at a position in the table.</summary>
  /// <param name="index">The position.</param>
  /// <returns>The symbol.</returns>
  public int SymbolAt(int index) => this._symbols[index];

  /// <summary>The frequency of the symbol sitting at a position.</summary>
  /// <param name="index">The position.</param>
  /// <returns>Its frequency.</returns>
  public int FrequencyAt(int index) => this._frequencies[index];

  /// <summary>Where a symbol sits in the table.</summary>
  /// <param name="symbol">The symbol to find.</param>
  /// <returns>Its position.</returns>
  /// <exception cref="ArgumentOutOfRangeException">The model does not hold it.</exception>
  public int IndexOf(int symbol) {
    var index = Array.IndexOf(this._symbols, symbol);
    if (index < 0)
      throw new ArgumentOutOfRangeException(nameof(symbol), symbol, "This Quantum model does not hold that symbol.");

    return index;
  }

  /// <summary>
  /// The sum of the frequencies of the symbol at <paramref name="index"/> and every
  /// symbol after it. Quantum's coder runs its cumulative counts downwards, so this,
  /// not the sum below, is what bounds a symbol's sub-range.
  /// </summary>
  /// <param name="index">The position, which may be one past the last symbol.</param>
  /// <returns>The cumulative frequency at or after that position.</returns>
  public int CumulativeFrom(int index) {
    var sum = 0;
    for (var i = this._symbols.Length - 1; i >= index; --i)
      sum += this._frequencies[i];

    return sum;
  }

  /// <summary>Finds the symbol whose sub-range holds a scaled count.</summary>
  /// <param name="scaled">The scaled count, below <see cref="TotalFrequency"/>.</param>
  /// <param name="cumulativeAbove">Receives the cumulative frequency after the symbol.</param>
  /// <returns>The position of the symbol.</returns>
  public int FindIndex(int scaled, out int cumulativeAbove) {
    var index = 0;
    var above = this.TotalFrequency;
    while (index < this._symbols.Length) {
      var next = above - this._frequencies[index];
      if (next <= scaled) {
        cumulativeAbove = next;
        return index;
      }

      above = next;
      ++index;
    }

    cumulativeAbove = 0;
    return this._symbols.Length - 1;
  }

  /// <summary>
  /// Records that a symbol was coded, rescaling the model if its total has grown past
  /// <see cref="QuantumConstants.ModelMaxTotal"/>.
  /// </summary>
  /// <param name="index">The position of the coded symbol.</param>
  /// <summary>
  /// The fourth rescale, which is not like the others: each count is halved on its
  /// own, rounded up, and the symbols are then put in descending order of count.
  /// </summary>
  /// <remarks>
  /// Which order equal counts end up in is not a matter of taste - it decides what
  /// every later symbol decodes to - and no rule stated as a comparison reproduces
  /// it. The arrangement below does, exactly, for three tables read back out of
  /// libmspack and for the selector table recorded in <c>docs/QUANTUM-ON-DISK.md</c>:
  /// a bubble sort that counts equal neighbours as out of order, carrying the larger
  /// counts towards the end, and the table then read back to front.
  /// </remarks>
  private void SortingRescale() {
    var count = this._frequencies.Length;
    var total = 0;
    for (var i = 0; i < count; ++i) {
      this._frequencies[i] = (this._frequencies[i] + 1) >> 1;
      total += this._frequencies[i];
    }

    for (var pass = 0; pass < count - 1; ++pass)
      for (var i = 0; i < count - 1 - pass; ++i) {
        if (this._frequencies[i + 1] > this._frequencies[i])
          continue;

        (this._frequencies[i], this._frequencies[i + 1]) = (this._frequencies[i + 1], this._frequencies[i]);
        (this._symbols[i], this._symbols[i + 1]) = (this._symbols[i + 1], this._symbols[i]);
      }

    Array.Reverse(this._frequencies);
    Array.Reverse(this._symbols);
    this.TotalFrequency = total;
  }

  public void Update(int index) {
    this._frequencies[index] += QuantumConstants.ModelIncrement;
    this.TotalFrequency += QuantumConstants.ModelIncrement;
    if (this.TotalFrequency <= QuantumConstants.ModelMaxTotal)
      return;

    ++this.Rescales;
    if (--this._sortsIn == 0) {
      this._sortsIn = QuantumConstants.RescalesBetweenSorts;
      this.SortingRescale();
      return;
    }

    var count = this._frequencies.Length;
    var halved = new int[count + 1];
    for (var i = 0; i < count; ++i)
      halved[i] = this.CumulativeFrom(i) >> 1;

    halved[count] = 0;

    // walk it back to strictly decreasing, so every symbol keeps at least one count
    for (var i = count - 1; i >= 0; --i)
      if (halved[i] <= halved[i + 1])
        halved[i] = halved[i + 1] + 1;

    var total = 0;
    for (var i = 0; i < count; ++i) {
      this._frequencies[i] = halved[i] - halved[i + 1];
      total += this._frequencies[i];
    }

    this.TotalFrequency = total;
  }
}

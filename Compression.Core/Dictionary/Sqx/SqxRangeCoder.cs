namespace Compression.Core.Dictionary.Sqx;

/// <summary>
/// The adaptive frequency model of SQX multimedia blocks. Besides the frequencies it keeps the
/// running sums and the last decoded symbol that the decoder's search starts from; those are part
/// of the state, because the search result depends on them exactly as they are updated here.
/// </summary>
internal sealed class SqxRangeModel {
  private readonly uint[] _freq;
  private readonly uint _symbols, _half, _third, _first, _twoThirds;
  public uint Total;
  private uint _increment, _maxFreq;
  private bool _adapt;
  private uint _halfFreq, _firstFreq, _lastFreq, _lastSymbol, _lastCumFreq;

  public SqxRangeModel(uint symbols, uint increment, uint maxFreq, bool adapt, uint initFreq) {
    this._symbols = symbols;
    this._half = symbols / 2;
    this._third = symbols / 3;
    this._first = symbols * 5 / 22;
    this._twoThirds = symbols * 2 / 3;
    this._freq = new uint[symbols];
    if (initFreq == 0)
      initFreq = 1;
    for (var i = 0; i < symbols; ++i)
      this.Total += this._freq[i] = initFreq;
    for (var i = 0u; i < this._first; ++i)
      this._firstFreq += this._freq[i];
    this._halfFreq = this._firstFreq;
    for (var i = this._first; i < this._half; ++i)
      this._halfFreq += this._freq[i];
    this._lastFreq = this._halfFreq;
    for (var i = this._half; i < this._twoThirds; ++i)
      this._lastFreq += this._freq[i];
    this._increment = increment;
    this._maxFreq = maxFreq;
    this._adapt = adapt;
    if (maxFreq > SqxRangeDecoder.Bottom || this.Total > SqxRangeDecoder.Bottom)
      throw new InvalidDataException("SQX range model parameters out of range.");
  }

  /// <summary>Finds the symbol whose cumulative interval holds <paramref name="target"/>; returns it and its cumulative frequency.</summary>
  public (uint Symbol, uint Cumulative) Find(uint target) {
    uint symbol, cum;
    if (this._lastCumFreq > target) {
      if (target > this._halfFreq || (this._lastSymbol <= this._twoThirds && target >= this._firstFreq)) {
        symbol = this._lastSymbol - 1;
        cum = this._lastCumFreq - this._freq[symbol];
        while (cum > target)
          cum -= this._freq[--symbol];
        this._lastCumFreq = cum;
        this._lastSymbol = symbol;
      } else {
        symbol = 0;
        cum = this._freq[0];
        while (cum <= target)
          cum += this._freq[++symbol];
        cum -= this._freq[symbol];
        if (this._adapt)
          this._lastCumFreq += this._increment;
      }
    } else {
      if (target < this._halfFreq || (this._lastSymbol >= this._third && target <= this._lastFreq)) {
        symbol = this._lastSymbol;
        cum = this._lastCumFreq + this._freq[symbol];
        while (cum <= target)
          cum += this._freq[++symbol];
        cum -= this._freq[symbol];
        this._lastCumFreq = cum;
        this._lastSymbol = symbol;
      } else {
        symbol = this._symbols - 1;
        cum = this.Total - this._freq[symbol];
        while (cum > target)
          cum -= this._freq[--symbol];
      }
    }

    return (symbol, cum);
  }

  public uint Frequency(uint symbol) => this._freq[symbol];

  public void Update(uint symbol) {
    if (!this._adapt)
      return;
    this._freq[symbol] += this._increment;
    if (symbol < this._twoThirds) {
      this._lastFreq += this._increment;
      if (symbol < this._half) {
        this._halfFreq += this._increment;
        if (symbol < this._first)
          this._firstFreq += this._increment;
      }
    }

    this.Total += this._increment;
    if (this.Total <= this._maxFreq)
      return;
    this.Total = 0;
    for (var i = 0; i < this._symbols; ++i)
      this.Total += this._freq[i] -= this._freq[i] >> 1;
    this._halfFreq /= 2;
    this._firstFreq /= 2;
    this._lastFreq /= 2;
    this._lastCumFreq = 0;
    this._lastSymbol = 0;
  }
}

/// <summary>The carryless range decoder of SQX multimedia blocks; it reads whole bytes through the stream's bit reader.</summary>
internal sealed class SqxRangeDecoder {
  public const uint Top = 1u << 24;
  public const uint Bottom = 1u << 16;
  private uint _low, _range, _code;

  public void Start(SqxBitReader reader) {
    this._low = 0;
    this._code = 0;
    this._range = 0xFFFFFFFF;
    for (var i = 0; i < 4; ++i)
      this._code = this._code << 8 | reader.Read(8);
  }

  public uint Decode(SqxRangeModel model, SqxBitReader reader) {
    if (this._range < model.Total)
      throw new InvalidDataException("SQX multimedia block: range coder underflow.");
    this._range /= model.Total;
    var target = (this._code - this._low) / this._range;
    if (target >= model.Total)
      throw new InvalidDataException("SQX multimedia block: range coder out of bounds.");

    var (symbol, cum) = model.Find(target);
    this._low += cum * this._range;
    this._range *= model.Frequency(symbol);
    while (true) {
      if ((this._low ^ (this._low + this._range)) >= Top) {
        if (this._range >= Bottom)
          break;
        this._range = (0u - this._low) & (Bottom - 1);
      }

      this._code = this._code << 8 | reader.Read(8);
      this._range <<= 8;
      this._low <<= 8;
    }

    model.Update(symbol);
    return symbol;
  }
}

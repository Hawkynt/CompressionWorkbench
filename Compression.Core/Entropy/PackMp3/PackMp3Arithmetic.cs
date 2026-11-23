namespace Compression.Core.Entropy.PackMp3;

/// <summary>One coded interval: [<see cref="Low"/>, <see cref="High"/>) out of <see cref="Scale"/>.</summary>
internal struct PmpSymbol {
  public uint Low;
  public uint High;
  public uint Scale;
}

/// <summary>
/// The binary arithmetic coder of packMP3/packJPG (31-bit registers, E1/E2/E3 scaling), converted
/// from <c>aricoder.cpp</c> (packMP3 v1.0g, LGPL-3.0).
/// </summary>
internal sealed class PmpArithmeticCoder {
  private const int UseBits = 31;
  internal const uint Limit100 = 1u << UseBits;
  internal const uint Limit025 = Limit100 / 4;
  internal const uint Limit050 = Limit100 / 4 * 2;
  internal const uint Limit075 = Limit100 / 4 * 3;
  private const int EndSlack = 16;

  private readonly MemoryStream? _output;
  private readonly byte[]? _input;
  private int _inputPosition;
  private byte _bitByte;
  private int _bitCount;

  private uint _code, _low, _high = Limit100 - 1, _step, _pendingBits;

  /// <summary>An encoder writing to <paramref name="output"/>.</summary>
  public PmpArithmeticCoder(MemoryStream output) => this._output = output;

  /// <summary>A decoder reading <paramref name="input"/> from <paramref name="offset"/> on; zeros follow the end.</summary>
  public PmpArithmeticCoder(byte[] input, int offset) {
    this._input = input;
    this._inputPosition = offset;
    for (var i = 0; i < UseBits; ++i) this._code = (this._code << 1) | this.ReadBit();
  }

  /// <summary>Bytes the decoder has taken from its input.</summary>
  public int InputPosition => this._inputPosition;

  public void Encode(in PmpSymbol s) {
    this._step = (this._high - this._low + 1) / s.Scale;
    this._high = this._low + this._step * s.High - 1;
    this._low += this._step * s.Low;

    while (this._low >= Limit050 || this._high < Limit050) {
      if (this._high < Limit050) {
        this.WriteBit(0);
        for (; this._pendingBits > 0; --this._pendingBits) this.WriteBit(1);
      } else {
        this.WriteBit(1);
        this._low &= Limit050 - 1;
        this._high &= Limit050 - 1;
        for (; this._pendingBits > 0; --this._pendingBits) this.WriteBit(0);
      }
      this._low <<= 1;
      this._high = (this._high << 1) + 1;
    }
    while (this._low >= Limit025 && this._high < Limit075) {
      ++this._pendingBits;
      this._low &= Limit025 - 1;
      this._high ^= Limit025 + Limit050;
      this._low <<= 1;
      this._high = (this._high << 1) + 1;
    }
  }

  /// <summary>Ends an encoded stream (the C++ destructor).</summary>
  public void Finish() {
    if (this._low < Limit025) {
      this.WriteBit(0);
      this.WriteBit(1);
      while (this._pendingBits-- > 0) this.WriteBit(1);
    } else
      this.WriteBit(1);
    while (this._bitCount > 0) this.WriteBit(0);
  }

  public uint DecodeCount(in PmpSymbol s) {
    this._step = (this._high - this._low + 1) / s.Scale;
    return (this._code - this._low) / this._step;
  }

  public void Decode(in PmpSymbol s) {
    this._high = this._low + this._step * s.High - 1;
    this._low += this._step * s.Low;

    while (this._low >= Limit050 || this._high < Limit050) {
      if (this._low >= Limit050) {
        this._low &= Limit050 - 1;
        this._high &= Limit050 - 1;
        this._code &= Limit050 - 1;
      }
      this._low <<= 1;
      this._high = (this._high << 1) + 1;
      this._code = (this._code << 1) | this.ReadBit();
      this._pendingBits = 0;
    }
    while (this._low >= Limit025 && this._high < Limit075) {
      ++this._pendingBits;
      this._low &= Limit025 - 1;
      this._high ^= Limit025 + Limit050;
      this._code -= Limit025;
      this._low <<= 1;
      this._high = (this._high << 1) + 1;
      this._code = (this._code << 1) | this.ReadBit();
    }
  }

  private void WriteBit(uint bit) {
    this._bitByte = (byte)((this._bitByte << 1) | (int)bit);
    if (++this._bitCount == 8) {
      this._output!.WriteByte(this._bitByte);
      this._bitCount = 0;
    }
  }

  private uint ReadBit() {
    if (this._bitCount == 0) {
      // The decoder looks at most 31 bits ahead, so a sound stream never needs much beyond its end.
      if (this._inputPosition >= this._input!.Length + EndSlack) throw new InvalidDataException("packMP3: unexpected end of data.");
      this._bitByte = this._inputPosition < this._input.Length ? this._input[this._inputPosition] : (byte)0;
      ++this._inputPosition;
      this._bitCount = 8;
    }
    --this._bitCount;
    return (uint)((this._bitByte >> this._bitCount) & 1);
  }
}

/// <summary>
/// packMP3's multi-symbol context model with escapes to lower orders and symbol exclusion
/// (<c>model_s</c> in <c>aricoder.cpp</c>).
/// </summary>
internal sealed class PmpModelS {
  /// <summary>The escape returned by <see cref="SymbolFromCount"/>.</summary>
  internal const int Escape = (int)PmpArithmeticCoder.Limit025;

  private sealed class Table {
    public ushort[]? Counts;
    public Table?[]? Links;
    public Table? Lesser;
    public ushort MaxCount;
    public ushort MaxSymbol;
  }

  private readonly uint[] _totals;
  private readonly bool[] _scoreboard;
  private int _unexcluded;
  private readonly Table?[] _storage; // contexts[i] == _storage[i + 1]
  private readonly int _maxSymbol, _maxContext, _maxOrder, _maxCount;
  private int _currentOrder;

  public PmpModelS(int maxSymbol, int maxContext, int maxOrder, int countLimit = 511) {
    this._maxSymbol = maxSymbol;
    this._maxContext = maxContext;
    this._maxOrder = maxOrder;
    this._maxCount = countLimit;
    this._totals = new uint[maxSymbol + 2];
    this._scoreboard = new bool[maxSymbol];
    this._unexcluded = maxSymbol;
    this._currentOrder = maxOrder;

    var nullTable = new Table { Counts = new ushort[maxSymbol], MaxCount = 1, MaxSymbol = (ushort)maxSymbol };
    Array.Fill(nullTable.Counts, (ushort)1);
    var startTable = new Table { Links = new Table?[maxContext], Lesser = nullTable };
    nullTable.Links = new Table?[maxContext];
    Array.Fill(nullTable.Links, startTable);

    this._storage = new Table?[maxOrder + 3];
    this._storage[0] = nullTable;
    this._storage[1] = startTable;
    for (var i = 1; i <= maxOrder; ++i) {
      var t = new Table { Lesser = this._storage[i], Links = i < maxOrder ? new Table?[maxContext] : null };
      this._storage[i + 1] = t;
      this._storage[i]!.Links![0] = t;
    }
  }

  private Table Context(int order) => this._storage[order + 1]!;

  public void Update(int symbol) {
    if (symbol >= 0) {
      for (var order = this._currentOrder < 0 ? 0 : this._currentOrder; order <= this._maxOrder; ++order) {
        var context = this.Context(order);
        var count = ++context.Counts![symbol];
        if (count > context.MaxCount) context.MaxCount = count;
        if (symbol >= context.MaxSymbol) context.MaxSymbol = (ushort)(symbol + 1);
        if (count >= this._maxCount) RescaleTable(context, 1);
      }
    }
    this._currentOrder = this._maxOrder;
    Array.Clear(this._scoreboard);
    this._unexcluded = this._maxSymbol;
  }

  public void ShiftContext(int c) {
    if (this._maxOrder < 1 || c < 0) return;
    for (var i = this._maxOrder; i > 0; --i) {
      var context = this.Context(i - 1).Links![c];
      if (context == null) {
        context = new Table { Lesser = this.Context(i - 2).Links![c] };
        if (i != this._maxOrder) {
          context.Links = new Table?[this._maxContext];
          this.Context(i + 1).Lesser = context;
        }
        this.Context(i - 1).Links![c] = context;
      }
      this._storage[i + 1] = context;
    }
  }

  public void Shift(int c1, int c2) {
    this.ShiftContext(c1);
    this.ShiftContext(c2);
  }

  public void Shift(int c1, int c2, int c3) {
    this.ShiftContext(c1);
    this.ShiftContext(c2);
    this.ShiftContext(c3);
  }

  public void Flush(int scaleFactor) => this.RecursiveFlush(this.Context(0), scaleFactor);

  /// <summary>The interval for <paramref name="c"/> in the current order; true when it is the escape.</summary>
  public bool SymbolFor(int c, ref PmpSymbol s) {
    var context = this.Context(this._currentOrder);
    this.Totalize(context);
    s.Scale = this._totals[0];
    if (c >= 0 && context.Counts![c] > 0) {
      s.Low = this._totals[c + 2];
      s.High = this._totals[c + 1];
      return false;
    }
    s.Low = this._totals[1];
    s.High = this._totals[0];
    --this._currentOrder;
    return true;
  }

  public void Scale(ref PmpSymbol s) {
    this.Totalize(this.Context(this._currentOrder));
    s.Scale = this._totals[0];
  }

  public int SymbolFromCount(uint count, ref PmpSymbol s) {
    int c;
    for (c = 1; (long)count < (int)this._totals[c]; ++c) { }
    s.Low = this._totals[c];
    s.High = this._totals[c - 1];
    if (c == 1) {
      --this._currentOrder;
      return Escape;
    }
    return c - 2;
  }

  private void Totalize(Table context) {
    var counts = context.Counts;
    if (counts == null) {
      context.Counts = new ushort[this._maxSymbol];
      this._totals[0] = 1;
      this._totals[1] = 0;
      return;
    }
    var before = this._unexcluded;
    var i = context.MaxSymbol - 1;
    this._totals[i + 2] = 0;
    uint total = 0;
    for (; i >= 0; --i) {
      if (!this._scoreboard[i]) {
        uint count = counts[i];
        if (count > 0) {
          total += count;
          this._scoreboard[i] = true;
          --this._unexcluded;
        }
      }
      this._totals[i + 1] = total;
    }
    uint escape;
    if (before == this._unexcluded)
      escape = 1;
    else if (this._unexcluded == 0)
      escape = 0;
    else {
      escape = (uint)(this._unexcluded * (before - this._unexcluded));
      escape /= (uint)(before * context.MaxCount);
      ++escape;
    }
    this._totals[0] = this._totals[1] + escape;
  }

  private static void RescaleTable(Table context, int scaleFactor) {
    var counts = context.Counts;
    if (counts == null) return;
    int last = context.MaxSymbol;
    for (var i = 0; i < last; ++i)
      if (counts[i] > 0) counts[i] >>= scaleFactor;
    context.MaxCount >>= scaleFactor;
    int j;
    for (j = last - 1; j >= 0; --j)
      if (counts[j] > 0) break;
    context.MaxSymbol = (ushort)(j + 1);
  }

  private void RecursiveFlush(Table context, int scaleFactor) {
    if (context.Links != null)
      for (var i = 0; i < this._maxContext; ++i)
        if (context.Links[i] is { } link) this.RecursiveFlush(link, scaleFactor);
    RescaleTable(context, scaleFactor);
  }

  public void Encode(PmpArithmeticCoder coder, int c) {
    var s = default(PmpSymbol);
    bool escaped;
    do {
      escaped = this.SymbolFor(c, ref s);
      coder.Encode(s);
    } while (escaped);
    this.Update(c);
  }

  public int Decode(PmpArithmeticCoder coder) {
    var s = default(PmpSymbol);
    int c;
    do {
      this.Scale(ref s);
      var count = coder.DecodeCount(s);
      c = this.SymbolFromCount(count, ref s);
      coder.Decode(s);
    } while (c == Escape);
    this.Update(c);
    return c;
  }
}

/// <summary>packMP3's binary context model (<c>model_b</c> in <c>aricoder.cpp</c>).</summary>
internal sealed class PmpModelB {
  private sealed class Table {
    public ushort[]? Counts;
    public Table?[]? Links;
    public Table? Lesser;
    public uint Scale;
  }

  private readonly Table?[] _storage; // contexts[i] == _storage[i + 1]
  private readonly int _maxContext, _maxOrder, _maxCount;

  public PmpModelB(int maxContext, int maxOrder, int countLimit = 511) {
    this._maxContext = maxContext;
    this._maxOrder = maxOrder;
    this._maxCount = countLimit;
    var nullTable = new Table { Counts = [1, 1], Scale = 2 };
    var startTable = new Table { Links = new Table?[maxContext], Lesser = nullTable };
    nullTable.Links = new Table?[maxContext];
    Array.Fill(nullTable.Links, startTable);
    this._storage = new Table?[maxOrder + 3];
    this._storage[0] = nullTable;
    this._storage[1] = startTable;
    for (var i = 1; i <= maxOrder; ++i) {
      var t = new Table { Lesser = this._storage[i], Links = i < maxOrder ? new Table?[maxContext] : null };
      this._storage[i + 1] = t;
      this._storage[i]!.Links![0] = t;
    }
  }

  private Table Context(int order) => this._storage[order + 1]!;

  public void Update(int symbol) {
    var context = this.Context(this._maxOrder);
    if (symbol >= 0 && this._maxOrder >= 0) {
      ++context.Counts![symbol];
      ++context.Scale;
      if (context.Counts[symbol] >= this._maxCount) Rescale(context, 1);
    }
  }

  public void ShiftContext(int c) {
    if (this._maxOrder < 1 || c < 0) return;
    for (var i = this._maxOrder; i > 0; --i) {
      var context = this.Context(i - 1).Links![c];
      if (context == null) {
        context = new Table { Lesser = this.Context(i - 2).Links![c] };
        if (i != this._maxOrder) {
          context.Links = new Table?[this._maxContext];
          this.Context(i + 1).Lesser = context;
        }
        this.Context(i - 1).Links![c] = context;
      }
      this._storage[i + 1] = context;
    }
  }

  public void Shift(int c1, int c2) {
    this.ShiftContext(c1);
    this.ShiftContext(c2);
  }

  public void Flush(int scaleFactor) => this.RecursiveFlush(this.Context(0), scaleFactor);

  private static void CheckCounts(Table context) {
    if (context.Counts != null) return;
    context.Counts = [1, 1];
    context.Scale = 2;
  }

  private static void Rescale(Table context, int scaleFactor) {
    var counts = context.Counts;
    if (counts == null) return;
    counts[0] >>= scaleFactor;
    counts[1] >>= scaleFactor;
    if (counts[0] == 0) counts[0] = 1;
    if (counts[1] == 0) counts[1] = 1;
    context.Scale = (uint)(counts[0] + counts[1]);
  }

  private void RecursiveFlush(Table context, int scaleFactor) {
    if (context.Links != null)
      for (var i = 0; i < this._maxContext; ++i)
        if (context.Links[i] is { } link) this.RecursiveFlush(link, scaleFactor);
    Rescale(context, scaleFactor);
  }

  public void Encode(PmpArithmeticCoder coder, int c) {
    var context = this.Context(this._maxOrder);
    CheckCounts(context);
    var s = c == 0
      ? new PmpSymbol { Scale = context.Scale, Low = 0, High = context.Counts![0] }
      : new PmpSymbol { Scale = context.Scale, Low = context.Counts![0], High = context.Scale };
    coder.Encode(s);
    this.Update(c);
  }

  public int Decode(PmpArithmeticCoder coder) {
    var context = this.Context(this._maxOrder);
    CheckCounts(context);
    var s = new PmpSymbol { Scale = context.Scale };
    var count = coder.DecodeCount(s);
    int c;
    if (count < context.Counts![0]) {
      s.Low = 0;
      s.High = context.Counts[0];
      c = 0;
    } else {
      s.Low = context.Counts[0];
      s.High = s.Scale;
      c = 1;
    }
    coder.Decode(s);
    this.Update(c);
    return c;
  }
}

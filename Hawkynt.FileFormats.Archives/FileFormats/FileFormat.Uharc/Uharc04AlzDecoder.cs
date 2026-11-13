namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.4 ALZ decoder (stream methods 40..87, modes <c>-m1</c>..<c>-m3</c>), implemented from
/// docs/UHARC-ON-DISK.md section "UHARC 0.4 ALZ".
/// </summary>
/// <remarks>
/// The token table, the literal-mode state machine, the literal contexts and the multimedia
/// predictors (<see cref="UharcAlzSideModel"/>) are those of 0.6's ALZ. What differs is the
/// frequency model (its steps, escape fallbacks and halving) and the copy coding: distance slots,
/// recent-distance indices and length classes are symbols of that frequency model, and a
/// per-byte-pair length prediction can stand in for the length symbol.
/// </remarks>
internal sealed class Uharc04AlzDecoder {

  private const int ContextCount = 1041;
  private const int WeightedContexts = 1027;
  private const int OrderZero = 0x300;
  private const int TextEscape = 0x403;
  private const int OrderZeroEscape = 0x405;
  private const int DistanceContext = 1017;
  private const int RecentContext = 1018;
  private const int LengthEscape = 1039;
  private const int DistanceEscape = 1040;
  private const int Limit = 0x4000;
  private const int BlockSpan = 0x20000;
  private const int RunLength = 255;
  private const int RecentCount = 12;
  private const uint ContextCostThreshold = 0x6D4250;
  private const uint RawCostThreshold = 0x91ADC0;
  private const uint CostWrapThreshold = 0xFEFF01;
  private const uint InitialSlotCost = 37440;

  private static readonly ushort[] TokenSeed = [0, 1, 66, 88, 111, 199];
  private static readonly ushort[] RunTokenSeed = [0, 1, 55, 155, 177, 199];

  // Length symbol -> first length, and the number of equiprobable values that follow it.
  private static readonly byte[] LengthBase = [2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 17, 19, 23, 31, 47, 63, 95, 127, 191, 255];
  private static readonly byte[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 4, 8, 16, 16, 32, 32, 64, 64, 0];

  // Confidence of the length prediction: [hit * 64 + confidence] -> next confidence.
  private static readonly byte[] ConfidenceNext = [
    0, 17, 20, 20, 24, 24, 29, 29, 35, 0, 35, 0, 42, 0, 42, 0, 0, 50, 0, 0, 50, 0, 0, 0, 59, 0, 0, 0, 0, 59, 0, 0,
    0, 0, 0, 60, 0, 0, 0, 0, 0, 0, 60, 0, 0, 0, 0, 0, 0, 0, 61, 0, 0, 0, 0, 0, 0, 0, 0, 61, 62, 62, 63, 63,
    0, 1, 1, 2, 3, 4, 5, 6, 7, 0, 8, 0, 10, 0, 12, 0, 0, 14, 0, 0, 17, 0, 0, 0, 20, 0, 0, 0, 0, 24, 0, 0,
    0, 0, 0, 29, 0, 0, 0, 0, 0, 0, 35, 0, 0, 0, 0, 0, 0, 0, 42, 0, 0, 0, 0, 0, 0, 0, 0, 50, 59, 60, 61, 62,
  ];

  private readonly UharcRangeDecoder _coder;
  private readonly UharcAlzSideModel _side = new();
  private readonly byte[] _window;
  private readonly uint _mask;
  private readonly uint _dictionarySize;

  // Frequency model.
  private readonly ushort[] _frequency = new ushort[ContextCount * 256];
  private readonly ushort[] _total = new ushort[ContextCount];
  private readonly ushort[] _escapeWeight = new ushort[WeightedContexts];
  private int _escapeContext = OrderZeroEscape;
  private bool _excluding;
  private byte _excluded;

  // Cost tracker deciding how literals are coded.
  private readonly uint[] _recentCost = new uint[256];
  private uint _costWindow;
  private uint _cost;
  private uint _contextCost;
  private uint _orderZeroCost;
  private int _costSlot;

  // Tokens and copies.
  private readonly ushort[,] _tokens = new ushort[66, 6];
  private readonly byte[] _tokenHistory = new byte[65536];
  private int _pendingHistory = -1;
  private int _literalMode;
  private uint _runCount;
  private readonly uint[] _recent = new uint[RecentCount];
  private ushort _lowBitProbability = 2048;
  private readonly byte[] _predictedLength = new byte[0x20000];
  private readonly byte[] _lengthConfidence = new byte[0x20000];

  private uint _writePosition;
  private uint _history;
  private int _group;
  private bool _alternativeText;

  private Uharc04AlzDecoder(byte[] packed, int offset, int length, int method) {
    this._dictionarySize = (uint)UharcMethods.DictionarySize(method);
    this._window = new byte[this._dictionarySize];
    this._mask = this._dictionarySize - 1;
    this._coder = new UharcRangeDecoder(packed, offset, length);

    for (var c = WeightedContexts; c < ContextCount; ++c) {
      Array.Fill(this._frequency, (ushort)8, c * 256, 256);
      this._total[c] = 8 * 256;
    }

    for (var c = OrderZero + 1; c < WeightedContexts; ++c)
      this._escapeWeight[c] = 1;
    for (var row = 0; row < 66; ++row) {
      var seed = row < 64 ? TokenSeed : RunTokenSeed;
      for (var i = 0; i < 6; ++i)
        this._tokens[row, i] = seed[i];
    }

    Array.Fill(this._lengthConfidence, (byte)0x3F);
    Array.Fill(this._recent, uint.MaxValue);
    this._literalMode = 0;
  }

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of a 0.4 ALZ stream.</summary>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.AlzFirst or > UharcMethods.AlzLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var decoder = new Uharc04AlzDecoder(packed, offset, length, method);
    long blockEnd = 0;
    var nextFile = 0;
    var position = 0;
    while (position < unpackedSize) {
      if (position >= blockEnd) {
        // A block reaches 128 KiB past the previous block end, extended to the next file end.
        var target = Math.Min(blockEnd + BlockSpan, unpackedSize);
        while (nextFile < fileEnds.Count && fileEnds[nextFile] < target)
          ++nextFile;
        blockEnd = nextFile < fileEnds.Count ? fileEnds[nextFile] : unpackedSize;
        decoder.StartBlock();
      }

      position = decoder.DecodeStep(output, position);
    }

    return output;
  }

  private void StartBlock() {
    Array.Clear(this._frequency, 0, (OrderZero + 1) * 256);
    Array.Clear(this._total, 0, OrderZero + 1);
    Array.Fill(this._escapeWeight, (ushort)1, 0, OrderZero + 1);
    for (var row = 0; row < 66; ++row)
      this.HalveTokens(row);
    for (var c = OrderZero + 1; c < ContextCount; ++c)
      this.Halve(c);

    Array.Fill(this._recentCost, InitialSlotCost);
    this._costWindow = InitialSlotCost * 255;
    this._orderZeroCost = 0;
    this._contextCost = 0;
    this._costSlot = 255;
    this.SwitchGroup((int)this._coder.DecodeUniform(11));
  }

  private void SwitchGroup(int group) {
    if (group == this._group) {
      this._alternativeText = false;
      return;
    }

    this._alternativeText = group == 10;
    if (group == 10) {
      for (var c = 0x200; c < 0x300; ++c)
        this.Halve(c);
      group = 0;
    }

    this._group = group;
    this._side.Select(group);
  }

  private int DecodeStep(byte[] output, int position) {
    int row;
    if (this._runCount != 0)
      row = this._runCount > 2 ? 65 : 64;
    else {
      row = this._literalMode;
      if (row <= 15) {
        var key = (int)(this._history & 0xFFFF);
        this._pendingHistory = key;
        var remembered = this._tokenHistory[key];
        if ((remembered & 0x38) != 0)
          row = 32 + (remembered >> 3);
      }
    }

    var token = this.DecodeToken(row);
    if (token == 4) {
      this._runCount = 0;
      return this.DecodeLiteral(output, position);
    }

    this._excluding = false;
    var (distance, count, recentSlot) = this.DecodeCopy(token, position);
    if (distance == 0 || distance > (uint)position || distance > this._dictionarySize)
      throw new InvalidDataException("UHARC: ALZ copy reaches before the start of the stream.");

    if (distance > 1 && recentSlot != 0) {
      for (var i = recentSlot == 0xFF ? RecentCount - 1 : recentSlot; i > 0; --i)
        this._recent[i] = this._recent[i - 1];
      this._recent[0] = distance;
    }

    this._excluding = count != RunLength;
    this._runCount = count == RunLength ? this._runCount + 1 : 0;
    var source = (this._writePosition - distance) & this._mask;
    for (var i = 0; i < count; ++i) {
      this.Put(output, position + i, this._window[source]);
      source = (source + 1) & this._mask;
    }

    if (this._excluding)
      this._excluded = this._window[source];
    return position + count;
  }

  private void Put(byte[] output, int position, byte value) {
    if (position < output.Length)
      output[position] = value;
    this._window[this._writePosition] = value;
    this._writePosition = (this._writePosition + 1) & this._mask;
    this._history = (this._history << 8) | value;
    if (this._group != 0)
      this._side.Observe(value, this._history, position + 1);
  }

  // ── Tokens ─────────────────────────────────────────────────────────────

  private int DecodeToken(int row) {
    for (var escapes = 0; escapes <= 64; ++escapes) {
      var count = this._coder.GetCount(this._tokens[row, 5]);
      var token = 4;
      while (token > 0 && count < this._tokens[row, token])
        --token;

      this._coder.Consume(this._tokens[row, token], (uint)(this._tokens[row, token + 1] - this._tokens[row, token]));
      if (token == 0) {
        this.SwitchGroup((int)this._coder.DecodeUniform(11));
        continue;
      }

      for (var i = token + 1; i < 6; ++i)
        this._tokens[row, i] += 88;
      if (this._tokens[row, 5] >= Limit)
        this.HalveTokens(row);

      if (this._pendingHistory >= 0) {
        var tag = token - 1;
        var remembered = this._tokenHistory[this._pendingHistory];
        if (remembered >> 6 == tag) {
          if ((remembered & 63) != 63)
            ++remembered;
        } else
          remembered = (byte)(tag << 6);
        this._tokenHistory[this._pendingHistory] = remembered;
        this._pendingHistory = -1;
      }

      this._literalMode = UharcAlzDecoder.ModeNext[((token - 1) << 5) + (this._literalMode & 31)];
      return token;
    }

    throw new InvalidDataException("UHARC: ALZ selector loop.");
  }

  /// <summary>Halves the four token widths; the escape slot keeps its width of 1.</summary>
  private void HalveTokens(int row) {
    Span<uint> width = stackalloc uint[5];
    for (var i = 1; i < 5; ++i)
      width[i] = (uint)(this._tokens[row, i + 1] - this._tokens[row, i] + 1) >> 1;
    for (var i = 1; i < 5; ++i)
      this._tokens[row, i + 1] = (ushort)(this._tokens[row, i] + width[i]);
  }

  // ── Copies ─────────────────────────────────────────────────────────────

  /// <summary>Decodes a copy: distance, length, and the recent-distance slot it came from (0xFF: a new distance).</summary>
  private (uint Distance, int Count, int RecentSlot) DecodeCopy(int token, int position) {
    uint distance;
    int recentSlot;
    if (token == 2) {
      distance = this._recent[0];
      recentSlot = 0;
    } else {
      var context = token == 1 ? DistanceContext : RecentContext;
      this._escapeContext = DistanceEscape;
      var symbol = this.DecodeSymbol(context);
      this.Update(symbol, context);
      if (token == 1) {
        distance = this.DecodeDistance(symbol, position);
        recentSlot = 0xFF;
      } else {
        if (symbol >= RecentCount)
          throw new InvalidDataException("UHARC: ALZ recent distance index out of range.");
        distance = this._recent[symbol];
        recentSlot = symbol;
      }
    }

    return (distance, this.DecodeLength(token, distance), recentSlot);
  }

  private uint DecodeDistance(int slot, int position) {
    if (slot >= UharcAlzDecoder.SlotBase.Length)
      throw new InvalidDataException("UHARC: ALZ distance slot out of range.");

    var low = UharcAlzDecoder.SlotBase[slot];
    if (slot == 0)
      return low;
    if (low > (uint)position)
      throw new InvalidDataException("UHARC: ALZ distance slot beyond the decoded data.");

    var span = UharcAlzDecoder.SlotSpan[slot];
    if (low + span - 1 > (uint)position)
      span = (uint)position - low + 1;

    var lowBit = -1;
    if (span >= 4) {
      lowBit = this._coder.DecodeBit(ref this._lowBitProbability, 6);
      span >>= 1;
    }

    var extra = 0u;
    if (span > 0x800) {
      var blocks = ((span - 1) >> 11) + 1;
      var block = this._coder.DecodeUniform(blocks);
      extra = block << 11;
      span = block + 1 != blocks ? 0x800 : span - extra;
    }

    if (span >= 2)
      extra += this._coder.DecodeUniform(span);
    if (lowBit >= 0)
      extra = extra * 2 + (uint)lowBit;

    return low + extra;
  }

  private int DecodeLength(int token, uint distance) {
    int context;
    if (token == 1)
      context = distance switch {
        < 0x100 => 0x3FB,
        < 0x1000 => 0x3FC,
        < 0x10000 => 0x3FD,
        < 0x100000 => 0x3FE,
        _ => 0x3FF,
      };
    else
      context = 0x400 + (this._runCount != 0 ? 1 : 0) + (this._runCount > 2 ? 1 : 0);

    // Lengths after a byte pair are predicted from the last length seen after the same pair.
    var key = -1;
    var symbol = -1;
    if (context <= 0x400 && distance > 1) {
      key = this._window[(this._writePosition - distance) & this._mask] << 8
        | this._window[(this._writePosition - distance + 1) & this._mask];
      if (context == 0x400)
        key += 0x10000;

      var confidence = (uint)this._lengthConfidence[key];
      if (confidence <= 50) {
        if (this._coder.GetCount(256) < confidence) {
          this._coder.Consume(0, confidence);
          this._excluding = true;
          this._excluded = this._predictedLength[key];
        } else {
          this._coder.Consume(confidence, 256 - confidence);
          this._excluding = false;
          symbol = this._predictedLength[key];
        }
      }
    }

    if (symbol < 0) {
      this._escapeContext = LengthEscape;
      symbol = this.DecodeSymbol(context);
      this.Update(symbol, context);
    }

    if (key >= 0) {
      var hit = this._predictedLength[key] == symbol ? 64 : 0;
      this._lengthConfidence[key] = ConfidenceNext[hit + this._lengthConfidence[key]];
      this._predictedLength[key] = (byte)symbol;
    }

    if (symbol >= LengthBase.Length)
      throw new InvalidDataException("UHARC: ALZ length symbol out of range.");

    var length = (int)LengthBase[symbol];
    if (LengthExtra[symbol] != 0)
      length += (int)this._coder.DecodeUniform(LengthExtra[symbol]);
    return length;
  }

  // ── Literals ───────────────────────────────────────────────────────────

  private int DecodeLiteral(byte[] output, int position) {
    byte value;
    if (this._group != 0) {
      if (this._excluding)
        this._excluded = this._side.ToDifference(this._excluded);
      var difference = this.DecodeLiteralSymbol(this._side.Context, OrderZeroEscape + this._group, withPredictor: true);
      value = this._side.ToByte(difference);
    } else {
      var context = (int)(this._history & 0xFF);
      if (this._alternativeText)
        context += 0x200;
      else if ((context & 0xC0) == ((this._history >> 8) & 0xC0))
        context += 0x100;
      value = (byte)this.DecodeLiteralSymbol(context, TextEscape, withPredictor: false);
    }

    this.Put(output, position, value);
    this._excluding = false;
    return position + 1;
  }

  /// <summary>
  /// Decodes one literal symbol of <paramref name="context"/> (with <paramref name="escape"/> as
  /// its fallback). Depending on how well the last 256 literals compressed it is coded in the
  /// context, as a raw byte, or as an order-0 byte; the context learns it either way.
  /// </summary>
  private int DecodeLiteralSymbol(int context, int escape, bool withPredictor) {
    this._costSlot = (this._costSlot + 1) & 0xFF;
    this._costWindow -= this._recentCost[this._costSlot];
    this._cost = 0;
    this._escapeContext = escape;
    var window = this._costWindow;
    int symbol;
    var trackWindow = true;
    var trackOrderZero = true;

    if (window is < ContextCostThreshold or >= CostWrapThreshold)
      symbol = this.DecodeSymbol(context);
    else if (window >= RawCostThreshold) {
      symbol = (int)this._coder.GetCount(256);
      this._coder.Consume((uint)symbol, 1);
      this.Estimate(context, symbol);
    } else if (this._orderZeroCost >= this._contextCost) {
      symbol = this.DecodeSymbol(context);
      var contextCost = this._cost;
      this._cost = 0;
      this._escapeContext = OrderZeroEscape;
      this._excluding = false;
      var actual = withPredictor ? this._side.ToByte(symbol) : (byte)symbol;
      this.Estimate(OrderZero, actual);
      var orderZeroCost = this._cost;
      this._cost = contextCost;
      this.Update(actual, OrderZero);
      this._orderZeroCost = Decay(this._orderZeroCost) + orderZeroCost;
      this._escapeContext = escape;
      trackOrderZero = false;
    } else {
      var excluding = this._excluding;
      this._excluding = false;
      this._escapeContext = OrderZeroEscape;
      var actual = this.DecodeSymbol(OrderZero);
      var orderZeroCost = this._cost;
      this._costWindow += orderZeroCost;
      this._recentCost[this._costSlot] = orderZeroCost;
      symbol = withPredictor ? this._side.ToDifference(actual) : actual;
      this._excluding = excluding;
      this._escapeContext = escape;
      this._cost = 0;
      this.Estimate(context, symbol);
      var contextCost = this._cost;
      this._escapeContext = OrderZeroEscape;
      this._cost = contextCost;
      this.Update(actual, OrderZero);
      this._orderZeroCost = Decay(this._orderZeroCost) + orderZeroCost;
      this._escapeContext = escape;
      trackWindow = false;
      trackOrderZero = false;
    }

    this.Update(symbol, context);
    if (trackWindow) {
      this._recentCost[this._costSlot] = this._cost;
      this._costWindow += this._cost;
    }

    if (trackOrderZero)
      this._orderZeroCost = Decay(this._orderZeroCost) + this._cost;
    this._contextCost = Decay(this._contextCost) + this._cost;
    return symbol;
  }

  private static uint Decay(uint value) => (value * 31) >> 5;

  private ushort F(int context, int symbol) => this._frequency[context * 256 + symbol];

  private void Charge(uint width, uint total) {
    var index = (width << 14) / total;
    this._cost += UharcAlzLiteralModel.Cost[Math.Min(index, 16383u)];
  }

  // ── Frequency model ────────────────────────────────────────────────────

  /// <summary>
  /// Decodes a symbol of <paramref name="context"/>; symbols occupy the interval from 255
  /// downwards, the excluded one left out. An escape continues in the current fallback context
  /// among the symbols <paramref name="context"/> has not seen.
  /// </summary>
  private int DecodeSymbol(int context) {
    var excluded = this._excluding ? this._excluded : -1;
    var seen = (uint)(this._total[context] - (excluded >= 0 ? this.F(context, excluded) : 0)) & 0xFFFF;
    var fallback = this._escapeContext;
    if (seen == 0) {
      var all = (uint)(this._total[fallback] - (excluded >= 0 ? this.F(fallback, excluded) : 0)) & 0xFFFF;
      return this.DecodeInside(fallback, this._coder.GetCount(all), all, excluded);
    }

    var withEscape = (seen + this._escapeWeight[context]) & 0xFFFF;
    var count = this._coder.GetCount(withEscape);
    if (count < seen)
      return this.DecodeInside(context, count, withEscape, excluded);

    this.Charge(withEscape - seen, withEscape);
    this._coder.Consume(seen, withEscape - seen);

    var basis = context * 256;
    var ruledOut = 0u;
    for (var s = 0; s < 256; ++s)
      if (this._frequency[basis + s] != 0 || s == excluded)
        ruledOut += this.F(fallback, s);

    var remaining = (this._total[fallback] - ruledOut) & 0xFFFF;
    var target = this._coder.GetCount(remaining);
    var low = 0u;
    for (var s = 255; s >= 0; --s) {
      if (this._frequency[basis + s] != 0 || s == excluded)
        continue;

      var f = (uint)this.F(fallback, s);
      if (target < low + f) {
        this.Charge(f, remaining);
        this._coder.Consume(low, f);
        return s;
      }

      low += f;
    }

    throw new InvalidDataException("UHARC: ALZ escape symbol not found.");
  }

  private int DecodeInside(int context, uint count, uint total, int excluded) {
    var basis = context * 256;
    var low = 0u;
    for (var s = 255; s >= 0; --s) {
      if (s == excluded)
        continue;

      var f = (uint)this._frequency[basis + s];
      if (f != 0 && count < low + f) {
        this.Charge(f, total);
        this._coder.Consume(low, f);
        return s;
      }

      low += f;
    }

    throw new InvalidDataException("UHARC: ALZ symbol outside its context.");
  }

  /// <summary>Adds the cost <paramref name="symbol"/> would have had in <paramref name="context"/>.</summary>
  private void Estimate(int context, int symbol) {
    var excluded = this._excluding ? this._excluded : -1;
    var count = (uint)this.F(context, symbol);
    var ruledOutBy = -1;
    if (count == 0) {
      var seen = (uint)(this._total[context] - (excluded >= 0 ? this.F(context, excluded) : 0)) & 0xFFFF;
      if (seen != 0) {
        uint weight = this._escapeWeight[context];
        this._cost += UharcAlzLiteralModel.Cost[(int)(((weight << 14) / (seen + weight)) & 0x3FFF)];
        ruledOutBy = context;
      }

      context = this._escapeContext;
      count = this.F(context, symbol);
    }

    var total = (uint)this._total[context];
    if (context < TextEscape) {
      total = (total + this._escapeWeight[context]) & 0xFFFF;
      if (excluded >= 0)
        total = (total - this.F(context, excluded)) & 0xFFFF;
    } else if (ruledOutBy < 0) {
      if (excluded >= 0)
        total = (total - this.F(context, excluded)) & 0xFFFF;
    } else {
      var basis = ruledOutBy * 256;
      for (var s = 0; s < 256; ++s)
        if (this._frequency[basis + s] != 0)
          total = (total - this.F(context, s)) & 0xFFFF;
      if (excluded >= 0 && this._frequency[basis + excluded] == 0)
        total = (total - this.F(context, excluded)) & 0xFFFF;
    }

    if (total != 0)
      this._cost += UharcAlzLiteralModel.Cost[(int)(((count << 14) / total) & 0x3FFF)];
  }

  private void Update(int symbol, int context) {
    var step = context >= 1017 ? (ushort)44 : context >= 1009 ? (ushort)10 : (ushort)33;
    if (this.F(context, symbol) == 0) {
      var fallbackStep = context is >= 993 and <= 1016 ? (ushort)8 : context == OrderZero ? (ushort)6 : (ushort)15;
      var fallback = this._escapeContext;
      this._escapeWeight[context] += step;
      this.Add(fallback, symbol, fallbackStep);
      if (this._total[fallback] >= Limit)
        this.Halve(fallback);
    }

    // The order-0 context only ever feeds its fallback.
    if (context == OrderZero)
      return;

    this.Add(context, symbol, step);
    if (this._escapeWeight[context] + this._total[context] >= Limit)
      this.Halve(context);
  }

  private void Add(int context, int symbol, ushort step) {
    this._frequency[context * 256 + symbol] += step;
    this._total[context] += step;
  }

  private void Halve(int context) {
    var basis = context * 256;
    var total = 0;
    for (var s = 0; s < 256; ++s) {
      var f = (ushort)((this._frequency[basis + s] + 1) >> 1);
      this._frequency[basis + s] = f;
      total += f;
    }

    this._total[context] = (ushort)total;
    if (context < WeightedContexts)
      this._escapeWeight[context] = (ushort)((this._escapeWeight[context] >> 2) + 1);
  }
}

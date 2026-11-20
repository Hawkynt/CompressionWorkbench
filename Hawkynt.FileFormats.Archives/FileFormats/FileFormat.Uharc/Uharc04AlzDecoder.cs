namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.4 ALZ decoder (stream methods 40..87, modes <c>-m1</c>..<c>-m3</c>), implemented from
/// docs/UHARC-ON-DISK.md section "UHARC 0.4 ALZ". The model is <see cref="Uharc04AlzModel{TCoder}"/>,
/// shared with <see cref="Uharc04AlzEncoder"/>.
/// </summary>
internal static class Uharc04AlzDecoder {

  /// <summary>Decodes <paramref name="unpackedSize"/> bytes of a 0.4 ALZ stream.</summary>
  public static byte[] Decode(byte[] packed, int offset, int length, int method, int unpackedSize, IReadOnlyList<long> fileEnds) {
    if (method is < UharcMethods.AlzFirst or > UharcMethods.AlzLast)
      throw new ArgumentOutOfRangeException(nameof(method));

    var output = new byte[unpackedSize];
    if (unpackedSize == 0)
      return output;

    var model = new Uharc04AlzModel<UharcDecoding>(new(new UharcRangeDecoder(packed, offset, length)), method, fileEnds, unpackedSize);
    var position = 0;
    while (position < unpackedSize)
      position = model.Step(output, position, default);
    return output;
  }
}

/// <summary>
/// The 0.4 ALZ model, written once for both directions (<see cref="IUharcCoder"/>).
/// </summary>
/// <remarks>
/// The token table, the literal-mode state machine, the literal contexts and the multimedia
/// predictors (<see cref="UharcAlzSideModel"/>) are those of 0.6's ALZ. What differs is the
/// frequency model (its steps, escape fallbacks and halving) and the copy coding: distance slots,
/// recent-distance indices and length classes are symbols of that frequency model, and a
/// per-byte-pair length prediction can stand in for the length symbol.
/// </remarks>
internal sealed class Uharc04AlzModel<TCoder> where TCoder : struct, IUharcCoder {

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

  private TCoder _coder;
  private readonly IReadOnlyList<long> _fileEnds;
  private readonly long _size;
  private long _blockEnd;
  private int _nextFile;
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

  public Uharc04AlzModel(TCoder coder, int method, IReadOnlyList<long> fileEnds, long size) {
    this._dictionarySize = (uint)UharcMethods.DictionarySize(method);
    this._window = new byte[this._dictionarySize];
    this._mask = this._dictionarySize - 1;
    this._coder = coder;
    this._fileEnds = fileEnds;
    this._size = size;

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

  /// <summary>The dictionary (window) size in bytes.</summary>
  public uint DictionarySize => this._dictionarySize;

  /// <summary>Distance <paramref name="slot"/> of the recent list; <see cref="uint.MaxValue"/> when no copy has filled it.</summary>
  public uint Recent(int slot) => this._recent[slot];

  /// <summary>Where the current model block ends (valid after the first step).</summary>
  public long BlockEnd => this._blockEnd;

  /// <summary>The byte the next literal cannot be, or -1.</summary>
  public int Excluded => this._excluding ? this._excluded : -1;

  /// <summary>Starts a new model block when <paramref name="position"/> reached the end of the current one.</summary>
  public void EnterBlock(int position) {
    if (position < this._blockEnd)
      return;

    // A block reaches 128 KiB past the previous block end, extended to the next file end.
    var target = Math.Min(this._blockEnd + BlockSpan, this._size);
    while (this._nextFile < this._fileEnds.Count && this._fileEnds[this._nextFile] < target)
      ++this._nextFile;
    this._blockEnd = this._nextFile < this._fileEnds.Count ? this._fileEnds[this._nextFile] : this._size;
    this.StartBlock();
  }

  /// <summary>
  /// The length symbol the decoder takes without coding one when a copy by
  /// <paramref name="token"/> at <paramref name="distance"/> starts at <paramref name="position"/>
  /// (a fully trusted per-byte-pair prediction), or -1.
  /// </summary>
  public int ForcedLengthSymbol(int position, int token, uint distance) {
    this.EnterBlock(position);
    if (distance <= 1 || (token != 1 && this._runCount != 0))
      return -1;
    var key = this._window[(this._writePosition - distance) & this._mask] << 8
      | this._window[(this._writePosition - distance + 1) & this._mask];
    if (token != 1)
      key += 0x10000;
    return this._lengthConfidence[key] == 0 ? this._predictedLength[key] : -1;
  }

  /// <summary>The length symbol for copy length <paramref name="length"/> (2..255).</summary>
  public static int LengthSymbol(int length) {
    var symbol = LengthBase.Length - 1;
    while (LengthBase[symbol] > length)
      --symbol;
    return symbol;
  }

  /// <summary>Whether <paramref name="length"/> lies in the range of length symbol <paramref name="symbol"/>.</summary>
  public static bool LengthInSymbol(int length, int symbol)
    => length >= LengthBase[symbol] && length < LengthBase[symbol] + Math.Max(1, (int)LengthExtra[symbol]);

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
    this.SwitchGroup((int)this._coder.Uniform(11, 0));
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

  /// <summary>Codes one token and what it carries; returns the position after the bytes it produced.</summary>
  public int Step(byte[] output, int position, in UharcAlzChoice choice) {
    this.EnterBlock(position);
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

    var token = this.DecodeToken(row, choice.Token);
    if (token == 4) {
      this._runCount = 0;
      return this.DecodeLiteral(output, position, choice.Literal);
    }

    this._excluding = false;
    var (distance, count, recentSlot) = this.DecodeCopy(token, position, choice);
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

  private int DecodeToken(int row, int target) {
    for (var escapes = 0; escapes <= 64; ++escapes) {
      var count = this._coder.Count(this._tokens[row, 5], TCoder.Encoding ? this._tokens[row, target] : 0u);
      var token = 4;
      while (token > 0 && count < this._tokens[row, token])
        --token;

      this._coder.Consume(this._tokens[row, token], (uint)(this._tokens[row, token + 1] - this._tokens[row, token]));
      if (token == 0) {
        this.SwitchGroup((int)this._coder.Uniform(11, 0));
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
  private (uint Distance, int Count, int RecentSlot) DecodeCopy(int token, int position, in UharcAlzChoice choice) {
    uint distance;
    int recentSlot;
    if (token == 2) {
      distance = this._recent[0];
      recentSlot = 0;
    } else {
      var context = token == 1 ? DistanceContext : RecentContext;
      this._escapeContext = DistanceEscape;
      var symbol = this.DecodeSymbol(context, !TCoder.Encoding ? 0 : token == 1 ? UharcAlzDecoder.SlotOf(choice.Distance) : choice.Slot);
      this.Update(symbol, context);
      if (token == 1) {
        distance = this.DecodeDistance(symbol, position, choice.Distance);
        recentSlot = 0xFF;
      } else {
        if (symbol >= RecentCount)
          throw new InvalidDataException("UHARC: ALZ recent distance index out of range.");
        distance = this._recent[symbol];
        recentSlot = symbol;
      }
    }

    return (distance, this.DecodeLength(token, distance, choice.Length), recentSlot);
  }

  private uint DecodeDistance(int slot, int position, uint target) {
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

    var offset = TCoder.Encoding ? target - low : 0;
    var lowBit = -1;
    if (span >= 4) {
      lowBit = this._coder.Bit(ref this._lowBitProbability, 6, (int)(offset & 1));
      span >>= 1;
      offset >>= 1;
    }

    var extra = 0u;
    if (span > 0x800) {
      var blocks = ((span - 1) >> 11) + 1;
      var block = this._coder.Uniform(blocks, offset >> 11);
      extra = block << 11;
      span = block + 1 != blocks ? 0x800 : span - extra;
    }

    if (span >= 2)
      extra += this._coder.Uniform(span, offset - extra);
    if (lowBit >= 0)
      extra = extra * 2 + (uint)lowBit;

    var distance = low + extra;
    if (TCoder.Encoding && distance != target)
      throw new InvalidOperationException("UHARC: 0.4 ALZ distance cannot be coded here.");
    return distance;
  }

  private int DecodeLength(int token, uint distance, int targetLength) {
    var targetSymbol = TCoder.Encoding ? LengthSymbol(targetLength) : 0;
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
        if (this._coder.Count(256, TCoder.Encoding && targetSymbol == this._predictedLength[key] ? confidence : 0u) < confidence) {
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
      symbol = this.DecodeSymbol(context, targetSymbol);
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
      length += (int)this._coder.Uniform(LengthExtra[symbol], (uint)(targetLength - length));
    if (TCoder.Encoding && length != targetLength)
      throw new InvalidOperationException("UHARC: 0.4 ALZ copy length cannot be coded.");
    return length;
  }

  // ── Literals ───────────────────────────────────────────────────────────

  private int DecodeLiteral(byte[] output, int position, byte target) {
    byte value;
    if (this._group != 0) {
      if (this._excluding)
        this._excluded = this._side.ToDifference(this._excluded);
      var difference = this.DecodeLiteralSymbol(this._side.Context, OrderZeroEscape + this._group, withPredictor: true,
        TCoder.Encoding ? this._side.ToDifference(target) : 0);
      value = this._side.ToByte(difference);
    } else {
      var context = (int)(this._history & 0xFF);
      if (this._alternativeText)
        context += 0x200;
      else if ((context & 0xC0) == ((this._history >> 8) & 0xC0))
        context += 0x100;
      value = (byte)this.DecodeLiteralSymbol(context, TextEscape, withPredictor: false, target);
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
  private int DecodeLiteralSymbol(int context, int escape, bool withPredictor, int target) {
    this._costSlot = (this._costSlot + 1) & 0xFF;
    this._costWindow -= this._recentCost[this._costSlot];
    this._cost = 0;
    this._escapeContext = escape;
    var window = this._costWindow;
    int symbol;
    var trackWindow = true;
    var trackOrderZero = true;

    if (window is < ContextCostThreshold or >= CostWrapThreshold)
      symbol = this.DecodeSymbol(context, target);
    else if (window >= RawCostThreshold) {
      symbol = (int)this._coder.Count(256, (uint)target & 0xFF);
      this._coder.Consume((uint)symbol, 1);
      this.Estimate(context, symbol);
    } else if (this._orderZeroCost >= this._contextCost) {
      symbol = this.DecodeSymbol(context, target);
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
      var actual = this.DecodeSymbol(OrderZero, !TCoder.Encoding ? 0 : withPredictor ? this._side.ToByte(target) : target);
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
  private int DecodeSymbol(int context, int target) {
    var excluded = this._excluding ? this._excluded : -1;
    var seen = (uint)(this._total[context] - (excluded >= 0 ? this.F(context, excluded) : 0)) & 0xFFFF;
    var fallback = this._escapeContext;
    if (seen == 0) {
      var all = (uint)(this._total[fallback] - (excluded >= 0 ? this.F(fallback, excluded) : 0)) & 0xFFFF;
      return this.DecodeInside(fallback, this._coder.Count(all, TCoder.Encoding ? this.SlotOf(fallback, target, excluded) : 0u), all, excluded);
    }

    var withEscape = (seen + this._escapeWeight[context]) & 0xFFFF;
    var present = TCoder.Encoding && target != excluded && this.F(context, target) != 0;
    var count = this._coder.Count(withEscape, !TCoder.Encoding ? 0u : present ? this.SlotOf(context, target, excluded) : seen);
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
    var slot = 0u;
    if (TCoder.Encoding) {
      if (target == excluded || this.F(context, target) != 0 || this.F(fallback, target) == 0)
        throw new InvalidOperationException("UHARC: 0.4 ALZ symbol cannot be coded after the escape.");
      for (var s = 255; s > target; --s)
        if (this._frequency[basis + s] == 0 && s != excluded)
          slot += this.F(fallback, s);
    }

    var wanted = this._coder.Count(remaining, slot);
    var low = 0u;
    for (var s = 255; s >= 0; --s) {
      if (this._frequency[basis + s] != 0 || s == excluded)
        continue;

      var f = (uint)this.F(fallback, s);
      if (wanted < low + f) {
        this.Charge(f, remaining);
        this._coder.Consume(low, f);
        return s;
      }

      low += f;
    }

    throw new InvalidDataException("UHARC: ALZ escape symbol not found.");
  }

  /// <summary>The encoder's slot for <paramref name="symbol"/> in <paramref name="context"/>: what the symbols above it take.</summary>
  private uint SlotOf(int context, int symbol, int excluded) {
    if (symbol == excluded || this.F(context, symbol) == 0)
      throw new InvalidOperationException("UHARC: 0.4 ALZ symbol cannot be coded in its context.");
    var basis = context * 256;
    var low = 0u;
    for (var s = 255; s > symbol; --s)
      if (s != excluded)
        low += this._frequency[basis + s];
    return low;
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

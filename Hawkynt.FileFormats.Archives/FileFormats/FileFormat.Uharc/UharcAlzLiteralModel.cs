namespace FileFormat.Uharc;

/// <summary>
/// The ALZ literal coder (docs/UHARC-ON-DISK.md, "ALZ literals"): 1056 frequency contexts with
/// escape weights, a second "escape" context per literal, and a cost tracker that switches
/// between context coding, an order-0 blend and raw bytes depending on how well the last 256
/// literals compressed.
/// </summary>
internal sealed class UharcAlzLiteralModel {

  public const int ContextCount = 0x420;
  public const int OrderZero = 0x300;
  public const int OrderZeroEscape = 0x402;
  private const int FirstSideContext = 0x301;
  private const int FirstEscapeContext = 0x3F9;
  private const int Limit = 0x4000;
  private const uint ContextCostThreshold = 0x6D4250;
  private const uint RawCostThreshold = 0x91ADC0;
  private const uint CostWrapThreshold = 0xFEFF01;

  // Cost of coding a symbol of probability i/16384: 65520 - ceil(4680 * log2(i)), 0 for i = 0.
  private static readonly ushort[] Cost = BuildCostTable();
  private static readonly ushort[] PrimaryStep = BuildPrimarySteps();
  private static readonly ushort[] EscapeStep = BuildEscapeSteps();

  private readonly UharcRangeDecoder _coder;
  private readonly ushort[] _frequency = new ushort[ContextCount * 256];
  private readonly ushort[] _total = new ushort[ContextCount];
  private readonly ushort[] _escapeWeight = new ushort[ContextCount];
  private readonly uint[] _budget = new uint[ContextCount];
  private readonly uint[] _recentCost = new uint[256];

  private uint _cost;
  private uint _costWindow;
  private uint _orderZeroCost;
  private uint _contextCost;
  private int _costSlot;
  private int _escapeContext;

  public UharcAlzLiteralModel(UharcRangeDecoder coder) {
    this._coder = coder;
    for (var c = 0; c < ContextCount; ++c) {
      this._escapeWeight[c] = 1;
      this._budget[c] = c is >= FirstSideContext and < FirstEscapeContext ? 256u : 16384u;
    }

    for (var c = FirstEscapeContext; c < ContextCount; ++c) {
      this.FillContext(c, c <= 0x400 ? (ushort)2 : (ushort)4);
      this._escapeWeight[c] = 0;
    }

    this.ResetCostTracker(37440);
  }

  /// <summary>
  /// The active multimedia predictor, or null. With a predictor, symbols are differences from
  /// its bias, while the order-0 blend still models the bytes themselves.
  /// </summary>
  public UharcAlzSideModel? Side { get; set; }

  /// <summary>When set, this symbol is known not to occur (the byte that would have continued the last copy).</summary>
  public int Excluded { get; set; } = -1;

  /// <summary>Forgets the per-file statistics at a model reset point.</summary>
  public void ResetForNextFile() {
    for (var c = 0; c <= OrderZero; ++c) {
      Array.Clear(this._frequency, c * 256, 256);
      this._total[c] = 0;
      this._escapeWeight[c] = 1;
      this._budget[c] = Limit;
    }

    for (var c = FirstSideContext; c < FirstEscapeContext; ++c)
      this.Forget(c);
    for (var c = FirstEscapeContext; c <= 0x40B; ++c)
      this.Halve(c);

    this.ResetCostTracker(0x9240);
  }

  /// <summary>Halves the alternative-text contexts (selector 10).</summary>
  public void ForgetAlternativeContexts() {
    for (var c = 0x200; c < 0x300; ++c)
      this.Forget(c);
  }

  private void ResetCostTracker(uint slotValue) {
    Array.Fill(this._recentCost, slotValue);
    this._costSlot = 255;
    this._costWindow = RawCostThreshold;
    this._orderZeroCost = 0;
    this._contextCost = 0;
  }

  private void FillContext(int context, ushort value) {
    Array.Fill(this._frequency, value, context * 256, 256);
    this._total[context] = (ushort)(value * 256);
  }

  private ushort F(int context, int symbol) => this._frequency[context * 256 + symbol];

  // ── Decoding ───────────────────────────────────────────────────────────

  /// <summary>Decodes one literal from <paramref name="context"/> with <paramref name="escape"/> as its fallback.</summary>
  public int Decode(int context, int escape) {
    this._escapeContext = escape;
    this._costSlot = (this._costSlot + 1) & 0xFF;
    var window = this._costWindow - this._recentCost[this._costSlot];
    this._costWindow = window;
    this._cost = 0;
    var excluded = this.Excluded;
    int symbol;

    if (window is < ContextCostThreshold or >= CostWrapThreshold) {
      symbol = this.DecodeFromContexts(context, escape);
      var cost = this._cost;
      this.Update(context, symbol);
      this.Track(cost, window: true, orderZero: true);
    } else if (window < RawCostThreshold) {
      if (this._orderZeroCost >= this._contextCost) {
        symbol = this.DecodeFromContexts(context, escape);
        var cost = this._cost;
        this._escapeContext = OrderZeroEscape;
        this.Excluded = -1;
        var actual = this.Side?.ToByte(symbol) ?? symbol;
        var orderZeroCost = this.Estimate(OrderZero, actual);
        this.CountOrderZero(actual);
        this._orderZeroCost = Decay(this._orderZeroCost) + orderZeroCost;
        this._escapeContext = escape;
        this.Update(context, symbol);
        this.Track(cost, window: true, orderZero: false);
      } else {
        this.Excluded = -1;
        this._escapeContext = OrderZeroEscape;
        symbol = this.DecodeFromContexts(OrderZero, OrderZeroEscape);
        var cost = this._cost;
        this._recentCost[this._costSlot] = cost;
        this._costWindow += cost;
        this.Excluded = excluded;
        this._escapeContext = escape;
        var difference = this.Side?.ToDifference(symbol) ?? symbol;
        var contextCost = this.Estimate(context, difference);
        this._escapeContext = OrderZeroEscape;
        this.CountOrderZero(symbol);
        this._orderZeroCost = Decay(this._orderZeroCost) + cost;
        this._escapeContext = escape;
        this.Update(context, difference);
        this._contextCost = Decay(this._contextCost) + contextCost;
        symbol = difference;
      }
    } else {
      symbol = (int)(this._coder.GetCount(256) & 0xFF);
      this._coder.Consume((uint)symbol, 1);
      var cost = this.Estimate(context, symbol);
      this.Update(context, symbol);
      this.Track(cost, window: true, orderZero: true);
    }

    return symbol;
  }

  private static uint Decay(uint value) => (value * 31) >> 5;

  private void Track(uint cost, bool window, bool orderZero) {
    if (window) {
      this._recentCost[this._costSlot] = cost;
      this._costWindow += cost;
    }

    if (orderZero)
      this._orderZeroCost = Decay(this._orderZeroCost) + cost;
    this._contextCost = Decay(this._contextCost) + cost;
  }

  private void Charge(uint width, uint total) => this._cost += Cost[(int)((width & 0xFFFF) << 14) / (int)total];

  private int DecodeFromContexts(int context, int escape) {
    var excluded = this.Excluded;
    var seen = (uint)(this._total[context] - (excluded >= 0 ? this.F(context, excluded) : 0)) & 0xFFFF;
    if (seen == 0) {
      var fallback = (uint)(this._total[escape] - (excluded >= 0 ? this.F(escape, excluded) : 0)) & 0xFFFF;
      var target = this._coder.GetCount(fallback);
      return this.DecodeInside(escape, fallback, target, fallback, excluded);
    }

    var withEscape = (seen + this._escapeWeight[context]) & 0xFFFF;
    var count = this._coder.GetCount(withEscape);
    if (count < seen)
      return this.DecodeInside(context, seen, count, withEscape, excluded);

    this.Charge(withEscape - seen, withEscape);
    this._coder.Consume(seen, withEscape - seen);

    // Escape: every symbol already seen in the context (and the excluded one) is ruled out.
    var basis = context * 256;
    var ruledOut = 0u;
    for (var s = 0; s < 256; ++s)
      if (this._frequency[basis + s] != 0 || s == excluded)
        ruledOut += this.F(escape, s);

    var remaining = (this._total[escape] - ruledOut) & 0xFFFF;
    var target2 = this._coder.GetCount(remaining);
    var low = 0u;
    for (var s = 255; s >= 0; --s) {
      if (this._frequency[basis + s] != 0 || s == excluded)
        continue;

      var f = (uint)this.F(escape, s);
      if (target2 < low + f) {
        this.Charge(f, remaining);
        this._coder.Consume(low, f);
        return s;
      }

      low = (low + f) & 0xFFFF;
    }

    throw new InvalidDataException("UHARC: ALZ escape literal not found.");
  }

  /// <summary>Symbols occupy the interval from the top down: symbol 255 is coded nearest 0.</summary>
  private int DecodeInside(int context, uint seen, uint count, uint total, int excluded) {
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

    throw new InvalidDataException("UHARC: ALZ literal outside its context.");
  }

  // ── Cost estimates for symbols coded elsewhere ─────────────────────────

  private uint Estimate(int context, int symbol) {
    if (this.F(context, symbol) != 0)
      return this.EstimateKnown(context, symbol, -1);

    var excluded = this.Excluded;
    var seen = (uint)(this._total[context] - (excluded >= 0 ? this.F(context, excluded) : 0)) & 0xFFFF;
    if (seen == 0)
      return this.EstimateKnown(this._escapeContext, symbol, -1);

    uint weight = this._escapeWeight[context];
    return Cost[(int)((weight << 14) / ((weight + seen) & 0xFFFFFFFF))] + this.EstimateKnown(this._escapeContext, symbol, context);
  }

  private uint EstimateKnown(int context, int symbol, int ruledOutBy) {
    var total = (uint)this._total[context];
    if (context < FirstEscapeContext)
      total = (total + this._escapeWeight[context]) & 0xFFFF;

    if (ruledOutBy >= 0) {
      var basis = ruledOutBy * 256;
      for (var s = 0; s < 256; ++s)
        if (this._frequency[basis + s] != 0)
          total = (total - this.F(context, s)) & 0xFFFF;
    }

    var excluded = this.Excluded;
    if (excluded >= 0 && (ruledOutBy < 0 || this.F(ruledOutBy, excluded) == 0))
      total = (total - this.F(context, excluded)) & 0xFFFF;

    return total == 0 ? 0u : Cost[(int)(((uint)this.F(context, symbol) << 14) / total)];
  }

  // ── Adaptation ─────────────────────────────────────────────────────────

  private void CountOrderZero(int symbol) {
    var escape = this._escapeContext;
    this.Add(escape, symbol, EscapeStep[OrderZero]);
    if (this._total[escape] >= Limit)
      this.Halve(escape);
  }

  private void Update(int context, int symbol) {
    var escape = this._escapeContext;
    var primary = PrimaryStep[context];
    var budget = this._budget[context] - (uint)(this.F(context, symbol) + this._escapeWeight[context]);
    if (this.F(context, symbol) == 0) {
      this.Add(escape, symbol, EscapeStep[context]);
      if (this._total[escape] >= Limit)
        this.Halve(escape);
      this._escapeWeight[context] += primary;
    }

    this.Add(context, symbol, primary);
    this._budget[context] = budget;
    if (this._escapeWeight[context] + this._total[context] >= Limit || (budget & 0x80000000) != 0)
      this.Forget(context);
  }

  private void Add(int context, int symbol, ushort step) {
    this._frequency[context * 256 + symbol] += step;
    this._total[context] += step;
  }

  private void Forget(int context) {
    var basis = context * 256;
    var total = 0;
    for (var s = 0; s < 256; ++s) {
      var f = this._frequency[basis + s];
      f = f > 16 ? (ushort)(f >> 1) : (ushort)0;
      this._frequency[basis + s] = f;
      total += f;
    }

    this._total[context] = (ushort)total;
    var weight = (ushort)((this._escapeWeight[context] + 1) >> 1);
    this._escapeWeight[context] = weight;
    this._budget[context] = (uint)(total + weight) << (context < OrderZero ? 6 : 8);
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
  }

  private static ushort[] BuildCostTable() {
    var table = new ushort[16385];
    for (var i = 1; i < table.Length; ++i)
      table[i] = (ushort)(65520 - (int)Math.Ceiling(4680.0 * Math.Log2(i)));
    return table;
  }

  private static ushort[] BuildPrimarySteps() {
    var steps = new ushort[ContextCount];
    for (var i = 0; i <= 1008; ++i)
      steps[i] = 32;
    for (var i = 1009; i <= 1016; ++i)
      steps[i] = 10;
    steps[1017] = 65281;
    steps[1020] = 256;
    steps[1021] = 65280;
    steps[1022] = 2;
    return steps;
  }

  private static ushort[] BuildEscapeSteps() {
    var steps = new ushort[ContextCount];
    Array.Fill(steps, (ushort)8);
    for (var i = 512; i <= 992; ++i)
      steps[i] = 16;
    steps[768] = 6;
    for (var i = 1017; i < ContextCount; ++i)
      steps[i] = 32;
    return steps;
  }
}

namespace FileFormat.Uharc;

/// <summary>
/// PPM frequency contexts (docs/UHARC-ON-DISK.md, "PPM", Literals step 4): 0x30F rows of 256
/// frequencies. Rows 0..0x1FF are order-1 (the text stream doubles them by a word-boundary
/// flag), 0x200 is order-0, 0x201..0x300 belong to the multimedia classes, and 0x301..0x30E are
/// the uniform fallbacks a row escapes into.
/// </summary>
internal sealed partial class UharcPpmDecoder {

  private const int Rows = 0x30F;
  private const int OrderZeroRow = 0x200;
  private const int LiteralFallback = 0x301;

  private readonly ushort[] _frequency = new ushort[Rows * 256];
  private readonly ushort[] _rowTotal = new ushort[Rows];
  private readonly ushort[] _escapeWeight = new ushort[Rows];
  private readonly uint[] _novelty = new uint[Rows];
  private readonly uint[] _budget = new uint[Rows];
  private readonly ushort[] _step = new ushort[Rows];
  private readonly ushort[] _fallbackStep = new ushort[Rows];
  private readonly byte[] _predicted = new byte[Rows];
  private readonly byte[] _confidence = new byte[Rows];
  private readonly uint[] _mpsProbability = new uint[0x800];
  private readonly uint[] _escapeScale = new uint[0x20];

  private readonly byte[] _excluded = new byte[MaxExcluded + 1];
  private int _excludedCount;
  private int _fallbackRow;

  private static readonly ushort[] Cost = BuildCost();

  private static ushort[] BuildCost() {
    var table = new ushort[0x4001];
    for (var i = 1; i < table.Length; ++i)
      table[i] = (ushort)(65520 - (int)Math.Ceiling(4680.0 * Math.Log2(i)));
    return table;
  }

  private static uint CostOf(uint key) => Cost[Math.Min(key, 0x4000u)];

  private ushort F(int row, int symbol) => this._frequency[row * 256 + symbol];

  private bool IsExcluded(ReadOnlySpan<byte> list, int symbol) => list.Contains((byte)symbol);

  private void Exclude(byte symbol) {
    if (this._excludedCount < MaxExcluded)
      this._excluded[this._excludedCount++] = symbol;
  }

  private ReadOnlySpan<byte> ExcludedList => this._excluded.AsSpan(0, this._excludedCount);

  private uint RemainingAfter(int row, ReadOnlySpan<byte> excluded) {
    uint rest = this._rowTotal[row];
    foreach (var s in excluded)
      rest = (rest - this.F(row, s)) & 0xFFFF;
    return rest;
  }

  // ── Decoding a symbol from a row ───────────────────────────────────────

  private struct KernelResult {
    public uint Cost;
    public uint LastTotal;
    public int ScaleMiss;
    public int ScaleHit;
    public bool Supported;
  }

  /// <summary>Decodes a literal from <paramref name="row"/> with <see cref="_fallbackRow"/> behind it.</summary>
  private int KernelDecode(int row, ref KernelResult result) {
    result = new() { ScaleMiss = -1, ScaleHit = -1, Supported = true };
    uint total = this._rowTotal[row];
    var weight = this._escapeWeight[row] * this._novelty[row];
    Span<byte> excluded = stackalloc byte[MaxExcluded + 2];
    this.ExcludedList.CopyTo(excluded);
    var count = this._excludedCount;
    return total > weight
      ? this.DecodeWithPrediction(row, total, excluded, count, ref result)
      : this.DecodeFallback(excluded[..count], ref result);
  }

  private int DecodeWithPrediction(int row, uint total, Span<byte> excluded, int count, ref KernelResult result) {
    var rest = this.RemainingAfter(row, excluded[..count]);
    if (row >= 0x200 >> this._kind)
      return this.DecodeRest(row, rest, excluded[..count], ref result);

    var predicted = this._predicted[row];
    if (this.IsExcluded(excluded[..count], predicted))
      return this.DecodeRest(row, rest, excluded[..count], ref result);

    var index = this.PredictionIndex(row, predicted, rest);
    var probability = this._mpsProbability[index];
    if (this._coder.DecodeScaled(probability, 0x2000)) {
      result.Cost += CostOf(probability * 2);
      return predicted;
    }

    result.Cost += CostOf((0x2000 - probability) * 2);
    rest = (rest - this.F(row, predicted)) & 0xFFFF;
    excluded[count++] = predicted;
    return this.DecodeRest(row, rest, excluded[..count], ref result);
  }

  /// <summary>Index of the SSE cell for the predicted byte of <paramref name="row"/>.</summary>
  private uint PredictionIndex(int row, byte predicted, uint rest) {
    uint frequency = this.F(row, predicted);
    var weight = (uint)UharcPpmTables.PredictedByteWeight[this._confidence[row]];
    var index = ((weight + frequency) << 4) / (weight + this._escapeWeight[row] + rest);
    if (frequency <= 0x600)
      index |= frequency > 0x100 ? 0x30u : frequency > 0x40 ? 0x20u : 0x10u;
    if ((row & 0xC0) == 0xC0)
      index |= 0x40;
    if ((predicted & 0xC0) == 0xC0)
      index |= predicted == 0xFF ? 0x100u : 0x80u;
    if (this._kind != 0 ? this._position - this._recentAt[predicted] <= 0x40 : UharcPpmTables.TextByte[predicted] != 0)
      index |= 0x200;
    if (this._symbols == this._lastSeenAt[predicted])
      index |= 0x400;
    return index;
  }

  private int DecodeRest(int row, uint rest, ReadOnlySpan<byte> excluded, ref KernelResult result)
    => rest == 0 ? this.DecodeFallback(excluded, ref result) : this.DecodeWithEscape(row, rest, excluded, ref result);

  private int DecodeFallback(ReadOnlySpan<byte> excluded, ref KernelResult result) {
    var row = this._fallbackRow;
    var total = this.RemainingAfter(row, excluded);
    if (total == 0) {
      result.Supported = false;
      return -1;
    }

    result.LastTotal = total;
    var count = this._coder.GetCount(total);
    return this.DecodeInRow(row, total, count, excluded, ref result);
  }

  /// <summary>Symbols take the interval from the top down: symbol 255 lies nearest 0.</summary>
  private int DecodeInRow(int row, uint total, uint count, ReadOnlySpan<byte> excluded, ref KernelResult result) {
    var low = 0u;
    for (var s = 255; s >= 0; --s) {
      if (excluded.Contains((byte)s))
        continue;

      var f = (uint)this.F(row, s);
      if (f != 0 && count < low + f) {
        this._coder.Consume(low, f);
        if (result.LastTotal != 0)
          result.Cost += CostOf((f << 14) / result.LastTotal);
        return s;
      }

      low += f;
    }

    result.Supported = false;
    return -1;
  }

  private int DecodeWithEscape(int row, uint rest, ReadOnlySpan<byte> excluded, ref KernelResult result) {
    uint weight = this._escapeWeight[row];
    uint total;
    var scale = -1;
    if (row >= 0x200 || rest >= 0x2000)
      total = rest + weight;
    else {
      scale = (int)((weight << 5) / (rest + weight));
      total = Math.Min(((rest << 13) / this._escapeScale[scale]) + 1, 0x3FFFu);
    }

    result.LastTotal = total;
    var count = this._coder.GetCount(total);
    if ((count & 0xFFFF) < rest) {
      if (scale >= 0)
        result.ScaleHit = scale;
      return this.DecodeInRow(row, rest, count, excluded, ref result);
    }

    if (scale >= 0)
      result.ScaleMiss = scale;
    result.Cost += CostOf(((total - rest) << 14) / total);
    this._coder.Consume(rest, total - rest);
    return this.DecodeNovel(row, excluded, ref result);
  }

  /// <summary>After an escape: the fallback row, without everything the row or the exclusions rule out.</summary>
  private int DecodeNovel(int row, ReadOnlySpan<byte> excluded, ref KernelResult result) {
    var fallback = this._fallbackRow;
    uint ruledOut = 0;
    for (var b = 0; b < 256; ++b)
      if (this.F(row, b) != 0 || excluded.Contains((byte)b))
        ruledOut += this.F(fallback, b);

    var total = (this._rowTotal[fallback] - ruledOut) & 0xFFFF;
    if (total == 0) {
      result.Supported = false;
      return -1;
    }

    result.LastTotal = total;
    var count = this._coder.GetCount(total) & 0xFFFF;
    uint high = 0;
    var symbol = 256;
    do {
      --symbol;
      if (this.F(row, symbol) == 0 && !excluded.Contains((byte)symbol))
        high = (high + this.F(fallback, symbol)) & 0xFFFF;
    } while (high <= count && symbol != 0);

    uint f = this.F(fallback, symbol);
    if (f == 0)
      throw new InvalidDataException("UHARC: PPM escape symbol not found.");
    this._coder.Consume(high - f, f);
    result.Cost += CostOf((f << 14) / total);
    return symbol;
  }

  // ── Cost estimates and SSE updates after the fact ──────────────────────

  private struct Estimate {
    public uint Cost;
    public int Index;
    public uint Value;
  }

  private uint TotalFromRest(int row, uint rest) {
    if (row >= LiteralFallback)
      return rest;

    uint weight = this._escapeWeight[row];
    if (row >= 0x200 || rest >= 0x2000)
      return rest + weight;
    if (rest + weight == 0)
      return rest;

    var scale = this._escapeScale[Math.Min((weight << 5) / (rest + weight), 0x1Fu)];
    return scale == 0 ? rest : Math.Min(((rest << 13) / scale) + 1, 0x3FFFu);
  }

  private uint PresentCost(int row, int symbol, ReadOnlySpan<byte> excluded) {
    var total = this.TotalFromRest(row, this.RemainingAfter(row, excluded));
    return total == 0 ? 0 : CostOf(Math.Min(((uint)this.F(row, symbol) << 14) / total, 0x3FFFu));
  }

  private uint NovelCost(int row, int fallback, int symbol, ReadOnlySpan<byte> excluded) {
    uint rest = this._rowTotal[fallback];
    for (var b = 0; b < 256; ++b)
      if (this.F(row, b) != 0 || excluded.Contains((byte)b))
        rest = (rest - this.F(fallback, b)) & 0xFFFF;
    var total = this.TotalFromRest(fallback, rest);
    return total == 0 ? 0 : CostOf(Math.Min(((uint)this.F(fallback, symbol) << 14) / total, 0x3FFFu));
  }

  /// <summary>What coding <paramref name="symbol"/> in <paramref name="row"/> would have cost, and the SSE cell it moves.</summary>
  private Estimate EstimateCost(int row, int symbol) {
    var estimate = new Estimate { Index = -1 };
    Span<byte> excluded = stackalloc byte[MaxExcluded + 2];
    this.ExcludedList.CopyTo(excluded);
    var count = this._excludedCount;

    if (this._rowTotal[row] <= this._escapeWeight[row] * this._novelty[row]) {
      estimate.Cost = this.PresentCost(this._fallbackRow, symbol, excluded[..count]);
      return estimate;
    }

    var noPrediction = row >= 0x200 >> this._kind;
    var predicted = this._predicted[row];
    if (!noPrediction && excluded[..count].Contains(predicted))
      noPrediction = true;
    if (!noPrediction) {
      var index = this.PredictionIndex(row, predicted, this.RemainingAfter(row, excluded[..count]));
      var old = this._mpsProbability[index];
      estimate.Index = (int)index;
      if (predicted == symbol) {
        estimate.Cost = CostOf(old * 2);
        estimate.Value = old + ((0x2000 - old) >> 6);
        return estimate;
      }

      estimate.Cost = CostOf((0x2000 - old) * 2);
      estimate.Value = old - (old >> 6);
      excluded[count++] = predicted;
    }

    if (this.F(row, symbol) != 0) {
      estimate.Cost += this.PresentCost(row, symbol, excluded[..count]);
      return estimate;
    }

    var rest = this.RemainingAfter(row, excluded[..count]);
    if (rest != 0) {
      var total = this.TotalFromRest(row, rest);
      estimate.Cost += CostOf(Math.Min(((total - rest) << 14) / total, 0x3FFFu));
    }

    estimate.Cost += this.NovelCost(row, this._fallbackRow, symbol, excluded[..count]);
    return estimate;
  }

  private void Apply(in Estimate estimate) {
    if (estimate.Index >= 0)
      this._mpsProbability[estimate.Index] = estimate.Value;
  }

  // ── Adaptation ─────────────────────────────────────────────────────────

  private void AddToRow(int row, int symbol, ushort step) {
    this._frequency[row * 256 + symbol] += step;
    this._rowTotal[row] += step;
  }

  /// <summary>Counts <paramref name="symbol"/> in the fallback row with the step <paramref name="row"/> prescribes.</summary>
  private void CountInFallback(int symbol, int row) {
    var fallback = this._fallbackRow;
    this.AddToRow(fallback, symbol, this._fallbackStep[row]);
    if (this._rowTotal[fallback] >= 0x4000)
      this.HalveRow(fallback, forget: false);
  }

  private uint HalveRow(int row, bool forget) {
    var basis = row * 256;
    uint total = 0;
    for (var s = 0; s < 256; ++s) {
      uint f = this._frequency[basis + s];
      if (f == 0)
        continue;
      f = forget && this._kind != 0 ? (f > 0x10 ? f >> 1 : 0) : (f + 1) >> 1;
      this._frequency[basis + s] = (ushort)f;
      total += f;
    }

    this._rowTotal[row] = (ushort)total;
    return total;
  }

  private void RescaleRow(int row) {
    var total = this.HalveRow(row, forget: true);
    if (total == 0)
      return;

    var weight = (uint)(this._escapeWeight[row] + 1) >> 1;
    this._escapeWeight[row] = (ushort)weight;
    var budget = weight + total;
    budget = row >= 0x200 >> this._kind ? budget << 8 : (budget * 3) << 5;
    if (this._selector == 3 && total > weight << 3)
      budget <<= 4;
    this._budget[row] = budget;
  }

  /// <summary>Counts <paramref name="symbol"/> in <paramref name="row"/> (and, while the row is young, in the fallback).</summary>
  private void Count(int symbol, int row) {
    var step = this._step[row];
    var present = (uint)this.F(row, symbol);
    var combined = this._escapeWeight[row] + present;
    uint charge;
    if (symbol == this._predicted[row]) {
      if (this._confidence[row] < 0xF)
        ++this._confidence[row];
      charge = combined << 5;
    } else {
      this._predicted[row] = (byte)symbol;
      this._confidence[row] = 0;
      charge = combined;
    }

    this._budget[row] -= charge;
    if (present != 0) {
      if (this._rowTotal[row] <= this._novelty[row] * this._escapeWeight[row])
        this.CountInFallback(symbol, row);
      this._novelty[row] >>= 4;
    } else {
      this.CountInFallback(symbol, row);
      this._escapeWeight[row] += step;
      this._novelty[row] += (uint)this._kind;
    }

    this.AddToRow(row, symbol, step);
    if (this._escapeWeight[row] + this._rowTotal[row] >= 0x4000 || (int)this._budget[row] < 0)
      this.RescaleRow(row);
  }

  private void CountLiteral(int row, int fallback, int symbol) {
    var orderZeroFallback = fallback == LiteralFallback ? 0x303 : 0x302;
    this._fallbackRow = fallback;
    this.Count(symbol, row);
    this._fallbackRow = orderZeroFallback;
    this.CountInFallback(symbol, OrderZeroRow);
  }
}

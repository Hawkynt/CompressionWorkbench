namespace FileFormat.Uharc;

/// <summary>
/// The UHARC 0.3 PPM literal cascade (docs/UHARC-ON-DISK.md, "UHARC 0.3 PPM", Literals): order-4
/// record, order-2 record and binary predictor in an order set by the predictor's strength, then
/// the frequency rows.
/// </summary>
internal sealed partial class Uharc03PpmModel<TCoder> {

  private const int Order4Records = 0x40000;
  private const int Order4Stride = 32;
  private const int Order4Slots = 13;
  private const int Order2Records = 0x10000;
  private const int Order2Stride = 20;
  private const int Rows = 0x30B;
  private const int LiteralFallback = 0x2FD;
  private const uint UniformCost = 0x9240;

  // The 256-literal cost sum: below RowCost the row decides, from RawCost up to CeilingCost
  // bytes are coded raw, in between the cheaper of the row and order-0. It starts at RawCost,
  // a little below 256 uniform costs.
  private const uint RowCost = 0x6D4250;
  private const uint RawCost = 0x91ADC0;
  private const uint CeilingCost = 0xFEFF01;

  // Count pairs (hits, misses) live in one table: sixteen pairs for single-symbol order-2
  // records, sixteen for single-symbol order-4 records, thirty-two for the binary predictor.
  private const int Order2PairBase = 0x00;
  private const int Order4PairBase = 0x20;
  private const int RingPairBase = 0x40;

  private static readonly ushort[] Price = BuildPrice();

  private static ushort[] BuildPrice() {
    var table = new ushort[0x4001];
    for (var i = 1; i < table.Length; ++i)
      table[i] = (ushort)(65520 - (int)Math.Ceiling(4680.0 * Math.Log2(i)));
    return table;
  }

  // Order-4 records: escape count, thirteen counts, thirteen symbols, a confidence byte, the
  // context as a tag. Order-2 records: escape count, eight counts and symbols, novelty, total.
  private readonly byte[] _o4 = new byte[Order4Records * Order4Stride];
  private readonly byte[] _o2 = new byte[Order2Records * Order2Stride];

  // Binary predictor: eight context tables of (hits, misses, byte, confidence).
  private static readonly int[] RingSize = [0x10000, 0x20000, 0x20000, 0x10000, 0x10000, 0x10000, 0x70000, 0x10000];
  private readonly byte[][] _ring = RingSize.Select(n => new byte[n * 4]).ToArray();
  private readonly uint[] _ringKey = new uint[8];

  private readonly byte[] _pairs = new byte[0x80];

  // Frequency rows: 0..0x1FF order-1, 0x200 order-0, 0x201..0x2FA class contexts, 0x2FB..0x2FC
  // copy lengths, 0x2FD..0x30A fallbacks.
  private readonly ushort[] _count = new ushort[Rows * 256];
  private readonly ushort[] _rowTotal = new ushort[Rows];
  private readonly ushort[] _escape = new ushort[LiteralFallback];
  private int _fallback;
  private uint _cost;

  private readonly byte[] _excluded = new byte[24];
  private int _excludedCount;

  // Deferred row updates of the current literal: (fallback, row, symbol).
  private readonly (int Fallback, int Row, byte Symbol)[] _log = new (int, int, byte)[8];
  private int _logCount;

  // Row choice by cost: the last 256 literals' costs, and decayed averages per path.
  private readonly uint[] _ageRing = new uint[256];
  private int _ageSlot = 0xFF;
  private uint _ageTotal = RawCost;
  private readonly uint[] _costOrderZero = new uint[2];
  private readonly uint[] _costRow = new uint[2];
  private uint _wpA;
  private uint _wpB;
  private uint _wpCost;
  private uint _classCost;

  // Order-4 / order-2 state of the current literal.
  private uint _context;
  private int _o4Base;
  private int _o4Record;
  private bool _o4Found;
  private uint _o4Total;
  private uint _o4Rest;
  private int _o4Slot;
  private int _o2Slot = 0xFF;
  private uint _o2Rest;
  private uint _o2Novelty;

  private bool _o4Used;
  private bool _o4Charge;
  private bool _o2Charge;
  private bool _ringCharge;
  private bool _classCharge;
  private bool _o2Update;
  private bool _ringUpdate;
  private bool _rankBoost;
  private int _ringChosen;
  private int _ringPairIndex = -1;
  private uint _ringCredit;

  // Stride detection: candidate distances ranked by hits in a list (up/down links, 0 its head),
  // and the rank table it drives.
  private readonly uint[] _lastAt = new uint[256];
  private readonly uint[] _distanceHits = new uint[256];
  private readonly byte[] _rankUp = new byte[256];
  private readonly byte[] _rankDown = new byte[256];
  private uint _strideEvents;
  private uint _stride = 1;
  private uint _strideLast = 1;
  private readonly uint[] _rank = new uint[0x10C];
  private uint _rankPointer;
  private uint _rankStep = 0x1FF;

  private readonly byte[] _markers = new byte[4];
  private int _markerState;

  private Span<byte> O4(int index) => this._o4.AsSpan(index * Order4Stride, Order4Stride);

  private Span<byte> O2(int index) => this._o2.AsSpan(index * Order2Stride, Order2Stride);

  private ReadOnlySpan<byte> Excluded => this._excluded.AsSpan(0, this._excludedCount);

  private void Exclude(byte symbol) => this._excluded[this._excludedCount++] = symbol;

  private static uint Decay(uint value) => (value * 31) >> 5;

  private static int Step(int row) => row is >= 0x2F3 and <= 0x2FA ? 0x0A : 0x20;

  private static int FallbackStep(int row) => row == 0x200 ? 6 : row is >= 0x2E3 and <= 0x2FA ? 8 : 0x10;

  private void InitializeStrideList() {
    for (var i = 0; i < 256; ++i) {
      this._rankUp[i] = (byte)(i - 1);
      this._rankDown[i] = (byte)(i + 1);
    }

    this._rankUp[0] = 0;
    this._rankDown[0] = 0;
    this._stride = 1;
    this._strideLast = 1;
  }

  // ── Count pairs ────────────────────────────────────────────────────────

  /// <summary>A binary decision with (hits, misses) at <paramref name="at"/> as frequencies; true for a hit (the encoder's <paramref name="hitWanted"/>).</summary>
  private bool DecodePair(int at, bool hitWanted) {
    uint hits = this._pairs[at], misses = this._pairs[at + 1];
    var count = this._coder.Count(hits + misses, !TCoder.Encoding || hitWanted ? 0u : hits);
    var hit = count < hits;
    if (hit)
      this._coder.Consume(0, hits);
    else
      this._coder.Consume(hits, misses);
    CountPair(this._pairs, at, hit);
    return hit;
  }

  private static void CountPair(byte[] table, int at, bool hit) {
    if (hit)
      ++table[at];
    else
      ++table[at + 1];
    if (table[at] >= 0xFE || table[at + 1] >= 0xFE) {
      table[at] = (byte)((table[at] + 1) >> 1);
      table[at + 1] = (byte)((table[at + 1] + 1) >> 1);
    }
  }

  // ── Frequency rows ─────────────────────────────────────────────────────

  private uint C(int row, int symbol) => this._count[row * 256 + symbol];

  /// <summary>Decodes a symbol of <paramref name="row"/> with <see cref="_fallback"/> behind it.</summary>
  private int DecodeRow(int row) {
    var excluded = this.Excluded;
    uint total = this._rowTotal[row];
    foreach (var s in excluded)
      total -= this.C(row, s);
    total &= 0xFFFF;
    if (total == 0) {
      var fallback = this._fallback;
      uint rest = this._rowTotal[fallback];
      foreach (var s in excluded)
        rest -= this.C(fallback, s);
      rest &= 0xFFFF;
      return this.DecodeIn(fallback, rest, this._coder.Count(rest, TCoder.Encoding ? this.SlotInRow(fallback, excluded) : 0u), excluded);
    }

    var all = total + this._escape[row];
    var present = TCoder.Encoding && this.C(row, this._rowTarget) != 0 && !excluded.Contains((byte)this._rowTarget);
    var count = this._coder.Count(all, !TCoder.Encoding ? 0u : present ? this.SlotInRow(row, excluded) : total);
    if ((count & 0xFFFF) < total)
      return this.DecodeIn(row, all, count, excluded);

    this._coder.Consume(total, all - total);
    this._cost += Price[((all - total) << 14) / all];
    return this.DecodeNovel(row, excluded);
  }

  /// <summary>The encoder's slot for its target in <paramref name="row"/>: what the symbols above it take.</summary>
  private uint SlotInRow(int row, ReadOnlySpan<byte> excluded) {
    var target = this._rowTarget;
    if (this.C(row, target) == 0 || excluded.Contains((byte)target))
      throw new InvalidOperationException("UHARC: 0.3 PPM symbol cannot be coded in its row.");
    var low = 0u;
    for (var s = 255; s > target; --s)
      if (!excluded.Contains((byte)s))
        low += this.C(row, s);
    return low;
  }

  /// <summary>Symbols take the interval from the top down: symbol 255 lies nearest 0.</summary>
  private int DecodeIn(int row, uint slots, uint count, ReadOnlySpan<byte> excluded) {
    var low = 0u;
    var basis = row * 256;
    for (var s = 255; s >= 0; --s) {
      uint f = this._count[basis + s];
      if (f == 0 || excluded.Contains((byte)s))
        continue;
      if (count < low + f) {
        this._coder.Consume(low, f);
        this._cost += Price[(f << 14) / slots];
        return s;
      }

      low += f;
    }

    throw new InvalidDataException("UHARC: PPM symbol not found.");
  }

  /// <summary>After an escape: the fallback row without what the row or the exclusions rule out.</summary>
  private int DecodeNovel(int row, ReadOnlySpan<byte> excluded) {
    var fallback = this._fallback;
    var rowBase = row * 256;
    var fallbackBase = fallback * 256;
    uint total = this._rowTotal[fallback];
    for (var s = 0; s < 256; ++s)
      if (this._count[rowBase + s] != 0 || excluded.Contains((byte)s))
        total -= this._count[fallbackBase + s];

    var slot = 0u;
    if (TCoder.Encoding) {
      var target = this._rowTarget;
      if (this._count[rowBase + target] != 0 || excluded.Contains((byte)target) || this._count[fallbackBase + target] == 0)
        throw new InvalidOperationException("UHARC: 0.3 PPM symbol cannot be coded after the escape.");
      for (var s = 255; s > target; --s)
        if (this._count[rowBase + s] == 0 && !excluded.Contains((byte)s))
          slot = (slot + this._count[fallbackBase + s]) & 0xFFFF;
    }

    var count = this._coder.Count(total, slot) & 0xFFFF;
    uint high = 0;
    for (var s = 255; s >= 0; --s) {
      if (this._count[rowBase + s] != 0 || excluded.Contains((byte)s))
        continue;
      uint f = this._count[fallbackBase + s];
      high = (high + f) & 0xFFFF;
      if (high > count) {
        this._coder.Consume(high - f, f);
        this._cost += Price[(f << 14) / total];
        return s;
      }
    }

    throw new InvalidDataException("UHARC: PPM escape symbol not found.");
  }

  /// <summary>Adds to <see cref="_cost"/> what coding <paramref name="symbol"/> in <paramref name="row"/> would have cost.</summary>
  private void EstimateRow(int row, int symbol) {
    var excluded = this.Excluded;
    var escaped = false;
    var target = row;
    if (this.C(row, symbol) == 0) {
      uint total = this._rowTotal[row];
      foreach (var s in excluded)
        total -= this.C(row, s);
      total &= 0xFFFF;
      if (total != 0) {
        uint weight = this._escape[row];
        this._cost += Price[(weight << 14) / (weight + total)];
        escaped = true;
      }

      target = this._fallback;
    }

    uint slots = this._rowTotal[target];
    if (target < LiteralFallback) {
      slots += this._escape[target];
      foreach (var s in excluded)
        slots -= this.C(target, s);
    } else if (escaped)
      for (var s = 0; s < 256; ++s) {
        if (this.C(row, s) != 0 || excluded.Contains((byte)s))
          slots -= this.C(target, s);
      }
    else
      foreach (var s in excluded)
        slots -= this.C(target, s);

    this._cost += Price[(this.C(target, symbol) << 14) / (slots & 0xFFFF)];
  }

  /// <summary>Counts <paramref name="symbol"/> in <paramref name="row"/> (a new one also in <see cref="_fallback"/>).</summary>
  private void CountSymbol(int symbol, int row) {
    var step = (ushort)Step(row);
    if (this.C(row, symbol) == 0) {
      this._escape[row] += step;
      var fallback = this._fallback;
      this._count[fallback * 256 + symbol] += (ushort)FallbackStep(row);
      this._rowTotal[fallback] += (ushort)FallbackStep(row);
      if (this._rowTotal[fallback] >= 0x4000)
        this.RescaleRow(fallback);
    }

    if (row == 0x200)
      return;

    this._count[row * 256 + symbol] += step;
    this._rowTotal[row] += step;
    if (this._rowTotal[row] + this._escape[row] >= 0x4000)
      this.RescaleRow(row);
  }

  private void RescaleRow(int row) {
    var counts = this._count.AsSpan(row * 256, 256);
    uint total = 0;
    for (var s = 0; s < 256; ++s) {
      var f = (ushort)((counts[s] + 1) >> 1);
      counts[s] = f;
      total += f;
    }

    this._rowTotal[row] = (ushort)total;
    if (row < LiteralFallback)
      this._escape[row] = (ushort)((this._escape[row] >> 2) + 1);
  }

  private void Log(int fallback, int row, byte symbol) => this._log[this._logCount++] = (fallback, row, symbol);

  /// <summary>
  /// Decodes from <paramref name="row"/>, from order-0 or as a raw byte, by the cost of the last
  /// 256 literals and the decayed costs of the row and of order-0.
  /// </summary>
  private int DecodeChosen(int row, int fallback) {
    var flag = fallback == LiteralFallback ? 1 : 0;
    this._fallback = fallback;
    this._ageSlot = (this._ageSlot + 1) & 0xFF;
    this._ageTotal -= this._ageRing[this._ageSlot];
    this._cost = 0;
    int symbol;
    var split = false;
    if (this._ageTotal is < RowCost or >= CeilingCost) {
      symbol = this.DecodeRow(row);
      if (this._classCharge)
        this._classCost += this._cost;
    } else if (this._ageTotal >= RawCost) {
      symbol = (int)this._coder.Uniform(256, (uint)this._rowTarget);
      if (this._classCharge)
        this._classCost += UniformCost;
      this.EstimateRow(row, symbol);
    } else {
      split = true;
      uint rowCost, zeroCost;
      if (this._costOrderZero[flag] < this._costRow[flag]) {
        this._fallback = 0x2FE + flag;
        symbol = this.DecodeRow(0x200);
        zeroCost = this._cost;
        if (this._classCharge)
          this._classCost += zeroCost;
        this._cost = 0;
        this._fallback = fallback;
        this.EstimateRow(row, symbol);
        rowCost = this._cost;
      } else {
        symbol = this.DecodeRow(row);
        if (this._classCharge)
          this._classCost += this._cost;
        rowCost = this._cost;
        this._cost = 0;
        this._fallback = 0x2FE + flag;
        this.EstimateRow(0x200, symbol);
        zeroCost = this._cost;
      }

      this.Log(0x2FE + flag, 0x200, (byte)symbol);
      this._ageTotal += zeroCost;
      this._ageRing[this._ageSlot] = zeroCost;
      this._costOrderZero[flag] = Decay(this._costOrderZero[flag]) + zeroCost;
      this._cost = rowCost;
      this._fallback = fallback;
    }

    this.Log(fallback, row, (byte)symbol);
    if (!split) {
      this._ageRing[this._ageSlot] = this._cost;
      this._ageTotal += this._cost;
      this._costOrderZero[flag] = Decay(this._costOrderZero[flag]) + this._cost;
    }

    this._costRow[flag] = Decay(this._costRow[flag]) + this._cost;
    return symbol;
  }

  /// <summary>Charges what coding <paramref name="symbol"/> in <paramref name="row"/> would have cost to the path's averages.</summary>
  private void ChargeOther(int row, int fallback, byte symbol) {
    this._cost = 0;
    this._fallback = fallback;
    this.EstimateRow(row, symbol);
    this.Log(fallback, row, symbol);
    var flag = fallback == LiteralFallback ? 1 : 0;
    this._costOrderZero[flag] = Decay(this._costOrderZero[flag]) + this._cost;
    this._costRow[flag] = Decay(this._costRow[flag]) + this._cost;
  }

  // ── One literal ────────────────────────────────────────────────────────

  private int DecodeLiteral(byte[] output, int written) {
    if (TCoder.Encoding)
      this._target = (byte)~this._source[written];
    var binary = this._kind != 0;
    var word = this._markerState == 2
      ? ((this._roll0 >> 8) & 0xFF) | ((this._roll0 >> 16) & 0xFF00) | ((this._roll1 << 8) & 0xFF0000) | (this._roll1 & 0xFF000000)
      : this._roll0;
    this._context = word;
    var o2Index = (int)(word & 0xFFFF);

    this._o4Used = this._o4Charge = this._o2Charge = this._ringCharge = this._classCharge = false;
    this._o2Update = this._ringUpdate = this._rankBoost = false;
    this._excludedCount = 0;
    this._logCount = 0;
    this._wpCost = 0;
    this._classCost = 0;
    if (this._hasExcluded) {
      this.Exclude(this._window[this._excludedPosition]);
      this._hasExcluded = false;
    }

    if (this._stride > 1 || ((this._strideLast - 2) & 0xFF) <= 2)
      this.PointRank();

    var gate = binary && (this._selector is 5 or 6
      ? this._costRow[0] * 31 < this._costRow[1] * 32
      : this._costRow[0] < this._costRow[1]);
    var classSelected = this._selector != 0 && (this._stride <= 4 || this._rank[this._rankPointer] <= 0x3FE);
    var armed = gate && classSelected;
    var engaged = armed && this._wpA < this._wpB;

    var symbol = -1;
    this.FindOrder4();
    if (this._o4Found)
      symbol = this.TryOrder4(binary, armed, engaged);

    if (symbol < 0) {
      var ring = -1;
      if (binary) {
        ring = this.ScoreOrders();
        if (ring >= 0)
          this._ringCredit += 2;
      }

      var order = this._ring[this._ringChosen];
      this._o2Update = ring < 0 || (order[ring] * 3 < order[ring + 1] * 5 && (order[ring + 3] & 0x7F) <= 2);
      var o2Tried = false;
      var ringTried = false;
      while (true) {
        if (this._o2Update || ringTried) {
          o2Tried = true;
          this._o2Update = this._o2Novelty == 0 || this.O2(o2Index)[0x11] == 0 || (this._rankStep < 0xFFFF && this._rank[this._rankPointer] <= 0x3FE);
          if (this._o2Update && this.O2(o2Index)[0] != 0)
            symbol = this.TryOrder2(o2Index, armed, engaged);
        }

        if (!ringTried) {
          ringTried = true;
          this._ringUpdate = symbol < 0 && binary;
          if (symbol < 0 && ring >= 0)
            symbol = this.TryRing(ring, armed, engaged);
          if (symbol < 0 && !o2Tried)
            continue;
        }

        break;
      }

      if (symbol < 0)
        symbol = this.DecodeFromRows(binary, gate, classSelected, armed);
    }

    var value = (byte)symbol;
    this.UpdateOrder4(value);
    if (this._o2Update)
      this.UpdateOrder2(o2Index, value);
    if (this._ringUpdate)
      this.UpdateRing(value);
    if (armed) {
      var delta = this._side.ToDifference(value);
      this._excludedCount = 0;
      this._cost = 0;
      this._fallback = 0x300 + this._selector;
      this.EstimateRow(this.ClassRow, delta);
      this._wpA = Decay(this._wpA) + this._cost;
      if (this._classCharge)
        this._wpCost += this._classCost;
      this._wpB = Decay(this._wpB) + this._wpCost;
    }

    for (var i = this._logCount - 1; i >= 0; --i) {
      var (fallback, row, s) = this._log[i];
      this._fallback = fallback;
      this.CountSymbol(s, row);
    }

    written = this.Emit(output, written, value);
    this.Upkeep(value, true);
    this.Store(value);
    return written;
  }

  /// <summary>The rank slot of the current phase: per phase for strides up to four, per stride above.</summary>
  private void PointRank() {
    var k = this._strideLast;
    this._rankPointer = k <= 4 ? 0xF8 + 4 * k + this._symbols % k : k;
  }

  // ── Order-4 record ─────────────────────────────────────────────────────

  /// <summary>Finds the record tagged with <see cref="_context"/> among the eight banks of its slot.</summary>
  private void FindOrder4() {
    var context = this._context;
    var index = (int)((context ^ (context >> 13)) & 0x7FFF);
    this._o4Base = index;
    this._o4Found = false;
    for (; index < Order4Records; index += 0x8000) {
      var r = this.O4(index);
      if (r[0] == 0 || (uint)(r[0x1C] | r[0x1D] << 8 | r[0x1E] << 16 | r[0x1F] << 24) != context)
        continue;

      this._o4Found = true;
      this._o4Record = index;
      this._o4Slot = 0xFF;
      uint total = r[0];
      for (var k = 1; k <= Order4Slots; ++k)
        total += r[k];
      this._o4Total = total;
      return;
    }
  }

  private int TryOrder4(bool binary, bool armed, bool engaged) {
    var r = this.O4(this._o4Record);
    var skip = this._ringCredit == 0 && this._statStrongHits <= this._statTotal && this._stride > 1
      && this._rank[this._rankPointer] > 0x3FE;
    if (skip)
      return -1;

    var confidence = r[0x1B] & 0x3F;
    var o2 = this.O2((int)(this._context & 0xFFFF));
    if (confidence < 8 && o2[0] != 0) {
      var order2 = o2[0x12] == 0 ? 0u : (uint)(o2[1] * (0x3E0 - confidence * 32) / o2[0x12]);
      var order4 = ((uint)r[1] << 10) / this._o4Total;
      if (order2 > order4)
        return -1;
    }

    this._o4Used = true;
    var rest = this._o4Total;
    Span<bool> active = stackalloc bool[Order4Slots];
    active.Fill(true);
    if (this._excludedCount != 0)
      for (var k = 0; k < Order4Slots && r[k + 1] != 0; ++k)
        if (r[0xE + k] == this._excluded[0]) {
          active[k] = false;
          rest -= r[k + 1];
          break;
        }

    this._o4Rest = rest;
    if (r[0] >= rest)
      return -1;

    this._o4Charge = armed;
    this._o4Used = !engaged;
    if (engaged)
      return -1;

    if (this._o4Total <= 8 && r[0] + r[1] == this._o4Total) {
      var pair = Order4PairBase + ((r[0] >> 1) * 4 + (r[1] >> 1)) * 2;
      if (this.DecodePair(pair, r[0xE] == this._target)) {
        this._o4Slot = 0;
        return r[0xE];
      }

      this._o4Slot = Order4Slots;
      this.Exclude(r[0xE]);
      return -1;
    }

    var count = this._coder.Count(rest, TCoder.Encoding ? this.SlotInRecord(r, active, Order4Slots, 0xE) : 0u);
    uint low = 0;
    var symbol = -1;
    var slot = 0;
    for (; slot < Order4Slots; ++slot) {
      if (!active[slot])
        continue;
      uint f = r[slot + 1];
      if (f == 0)
        break;
      if (count < low + f) {
        symbol = r[0xE + slot];
        this._coder.Consume(low, f);
        break;
      }

      this.Exclude(r[0xE + slot]);
      low += f;
    }

    if (symbol < 0) {
      slot = Order4Slots;
      this._coder.Consume(low, rest - low);
    }

    this._o4Slot = slot;
    return symbol;
  }

  /// <summary>
  /// The encoder's slot in a record of <paramref name="slots"/> counts (symbols from
  /// <paramref name="symbolBase"/>): what the active symbols before its target take, or, for a
  /// target the record does not offer, the start of the escape.
  /// </summary>
  private uint SlotInRecord(ReadOnlySpan<byte> r, ReadOnlySpan<bool> active, int slots, int symbolBase) {
    var low = 0u;
    for (var slot = 0; slot < slots; ++slot) {
      if (!active[slot])
        continue;
      uint f = r[slot + 1];
      if (f == 0)
        break;
      if (r[symbolBase + slot] == this._target)
        return low;
      low += f;
    }

    return low;
  }

  private void UpdateOrder4(byte symbol) {
    if (!this._o4Found) {
      this.CreateOrder4(symbol);
      return;
    }

    var r = this.O4(this._o4Record);
    var slot = this._o4Slot;
    if (slot == 0xFF) {
      slot = 0;
      while (slot < Order4Slots && r[0xE + slot] != symbol)
        ++slot;
    }

    if (this._o4Used && slot < Order4Slots && r[slot + 1] >> 6 > r[0])
      this._statStrongHits += 4;

    if (this._o4Charge) {
      if (slot >= Order4Slots)
        this._wpCost += Price[((uint)r[0] << 14) / this._o4Rest];
      else {
        this._o2Charge = this._ringCharge = this._classCharge = false;
        this._wpCost += Price[((uint)r[slot + 1] << 14) / this._o4Rest];
      }
    }

    var halve = UharcPpmTables.Order4Threshold[r[0]] < this._o4Total;
    if (slot < Order4Slots) {
      var hits = (byte)(r[0x1B] & 0xC0);
      if (r[slot + 1] + 1 < r[1] >> 1) {
        hits += 0x40;
        if (hits == 0)
          halve = true;
      } else
        hits = (byte)((hits >> 1) & 0xC0);
      r[0x1B] = (byte)((r[0x1B] & 0x3F) | hits);
    }

    if (halve)
      for (var j = 0; j <= Order4Slots; ++j)
        r[j] = (byte)((r[j] + 1) >> 1);

    if (slot < Order4Slots) {
      r[slot + 1] += 2;
      if ((r[0x1B] & 0x3F) < 0x3F)
        ++r[0x1B];
      this._statRecordHits += 5;
    } else {
      r[0] += 2;
      slot = 0;
      while (slot < 12 && r[slot + 1] != 0)
        ++slot;
      if (r[slot + 1] < 6) {
        r[slot + 1] = 2;
        r[0xE + slot] = symbol;
      }

      r[0x1B] = (byte)((r[0x1B] & 0xC0) + ((r[0x1B] & 0x3F) >> 1));
    }

    while (slot != 0 && r[slot + 1] >= r[slot]) {
      (r[slot], r[slot + 1]) = (r[slot + 1], r[slot]);
      (r[0xD + slot], r[0xE + slot]) = (r[0xE + slot], r[0xD + slot]);
      --slot;
    }

    this._o4Slot = slot;
    this.CountStatistics();
  }

  /// <summary>A new record goes into the first empty bank of its slot, else over the least confident one.</summary>
  private void CreateOrder4(byte symbol) {
    var chosen = 0;
    for (var bank = 0; bank < 8; ++bank) {
      var current = this.O4(this._o4Base + bank * 0x8000);
      if (current[0] == 0) {
        chosen = bank;
        break;
      }

      if ((current[0x1B] & 0x3F) < (this.O4(this._o4Base + chosen * 0x8000)[0x1B] & 0x3F))
        chosen = bank;
    }

    var r = this.O4(this._o4Base + chosen * 0x8000);
    r.Clear();
    r[0] = 1;
    r[1] = 1;
    r[0xE] = symbol;
    var context = this._context;
    r[0x1C] = (byte)context;
    r[0x1D] = (byte)(context >> 8);
    r[0x1E] = (byte)(context >> 16);
    r[0x1F] = (byte)(context >> 24);
  }

  private void CountStatistics() {
    this._statTotal += 2;
    if (this._statTotal > 0x800) {
      this._statTotal >>= 1;
      this._statRecordHits >>= 1;
      this._statStrongHits >>= 1;
    }
  }

  /// <summary>Quarters every order-4 record; a record left without counts becomes empty.</summary>
  private void QuarterOrder4() {
    for (var index = 0; index < Order4Records; ++index) {
      var r = this.O4(index);
      if (r[0] == 0)
        continue;

      byte sum = 0;
      for (var k = 1; k <= Order4Slots; ++k) {
        r[k] >>= 2;
        sum += r[k];
      }

      r[0] = sum != 0 ? (byte)((r[0] >> 2) + 1) : (byte)0;
      r[0x1B] >>= 1;
    }
  }

  // ── Order-2 record ─────────────────────────────────────────────────────

  private int TryOrder2(int index, bool armed, bool engaged) {
    var r = this.O2(index);
    var symbols = 0;
    var rest = r[0x12];
    Span<bool> active = stackalloc bool[8];
    active.Fill(true);
    for (var j = 0; j < 8 && r[j + 1] != 0; ++j) {
      ++symbols;
      if (this.Excluded.Contains(r[9 + j])) {
        active[j] = false;
        rest -= r[j + 1];
      }
    }

    this._o2Rest = rest;
    this._o2Slot = 0xFF;
    if (rest * 4 <= r[0] * 5 || r[0x11] * 4 + this._o2Novelty >= 0x10)
      return -1;

    this._o2Charge = armed;
    if (engaged)
      return -1;

    if (rest <= 0x10 && symbols == 1) {
      var pair = Order2PairBase + ((r[0] >> 2) * 4 + (r[1] >> 2)) * 2;
      if (this.DecodePair(pair, r[9] == this._target)) {
        this._o2Slot = 0;
        return r[9];
      }

      this._o2Slot = 8;
      this.Exclude(r[9]);
      return -1;
    }

    var count = this._coder.Count(rest, TCoder.Encoding ? this.SlotInRecord(r, active, 8, 9) : 0u);
    uint low = 0;
    var symbol = -1;
    var slot = 0;
    for (; slot < 8; ++slot) {
      if (!active[slot])
        continue;
      uint f = r[slot + 1];
      if (f == 0)
        break;
      if (count < low + f) {
        symbol = r[9 + slot];
        this._coder.Consume(low, f);
        break;
      }

      this.Exclude(r[9 + slot]);
      low += f;
    }

    if (symbol < 0) {
      slot = 8;
      this._coder.Consume(low, rest - low);
    }

    this._o2Slot = slot;
    return symbol;
  }

  private void UpdateOrder2(int index, byte symbol) {
    var r = this.O2(index);
    if (r[0] == 0) {
      r.Clear();
      r[0] = 3;
      r[1] = 3;
      r[0x12] = 6;
      r[9] = symbol;
      return;
    }

    var slot = this._o2Slot;
    if (slot == 0xFF) {
      slot = 0;
      while (slot < 8 && r[9 + slot] != symbol)
        ++slot;
    }

    if (this._o2Charge) {
      if (slot < 8) {
        this._ringCharge = this._classCharge = false;
        this._wpCost += Price[((uint)r[slot + 1] << 14) / this._o2Rest];
      } else
        this._wpCost += Price[((uint)r[0] << 14) / this._o2Rest];
    }

    if (r[0x12] > UharcPpmTables.Order2Threshold[r[0]]) {
      r[0x12] = 0;
      for (var j = 8; j >= 0; --j) {
        r[j] = (byte)((r[j] + 1) >> 1);
        r[0x12] += r[j];
      }
    }

    r[0x12] += 4;
    if (slot >= 8) {
      ++r[0x11];
      ++this._o2Novelty;
      r[0] += 4;
      slot = 0;
      while (slot < 7 && r[slot + 1] != 0)
        ++slot;
      if (r[slot + 1] < 0xC) {
        r[0x12] += (byte)(4 - r[slot + 1]);
        r[slot + 1] = 4;
        r[9 + slot] = symbol;
      }
    } else {
      this._o2Novelty = (this._o2Novelty * 3) >> 2;
      r[0x11] >>= 1;
      r[slot + 1] += 4;
      this._statRecordHits += 5;
    }

    while (slot != 0 && r[slot + 1] >= r[slot]) {
      (r[slot], r[slot + 1]) = (r[slot + 1], r[slot]);
      (r[8 + slot], r[9 + slot]) = (r[9 + slot], r[8 + slot]);
      --slot;
    }

    this._o2Slot = slot;
    this.CountStatistics();
  }

  /// <summary>Quarters every order-2 record; the total is recomputed and the novelty count halved.</summary>
  private void QuarterOrder2() {
    for (var index = 0; index < Order2Records; ++index) {
      var r = this.O2(index);
      if (r[0] == 0)
        continue;

      byte sum = 0;
      for (var k = 1; k <= 8; ++k) {
        r[k] >>= 2;
        sum += r[k];
      }

      r[0] = sum != 0 ? (byte)((r[0] >> 2) + 1) : (byte)0;
      r[0x11] >>= 1;
      r[0x12] = (byte)(sum + r[0]);
    }
  }

  // ── Binary predictor ───────────────────────────────────────────────────

  /// <summary>
  /// Keys the eight contexts, plants the stride extrapolation in the last one and returns the
  /// offset of the most trusted record, or -1.
  /// </summary>
  private int ScoreOrders() {
    this._ringPairIndex = -1;
    var r0 = this._roll0;
    var r1 = this._roll1;
    var marker = (r0 & 0xFFFF0000) == 0xFFFF0000 ? 0x10000u
      : (r0 & 0xFFFF) == 0xFFFF ? 0x20000u
      : (r0 & 0xFF000000) == 0xFF000000 ? 0x30000u
      : (r0 & 0xFF0000) == 0xFF0000 ? 0x40000u
      : (r0 & 0xFF00) == 0xFF00 ? 0x50000u
      : (r0 & 0xFF) == 0xFF ? 0x60000u
      : 0u;
    var keys = this._ringKey;
    keys[0] = ((r0 >> 16) & 0xFF00) + (r1 >> 24);
    keys[1] = (r0 >> 8) & 0xFFFF;
    keys[2] = r0 >> 16;
    keys[3] = ((r0 >> 8) & 0xFF00) + (r0 & 0xFF);
    keys[4] = ((r0 >> 16) & 0xFF00) + ((r0 >> 8) & 0xFF);
    keys[5] = ((r0 >> 16) & 0xFF00) + (r0 & 0xFF);
    keys[6] = (r1 & 0xFFFF) + marker;
    if ((r0 & 0xFF) == 0xFF) {
      keys[1] += 0x10000;
      keys[2] += 0x10000;
    }

    var stride = this._strideLast;
    var b1 = this._window[(this._position - stride) & this._mask];
    var b2 = this._window[(this._position - 2 * stride) & this._mask];
    uint phase;
    var candidates = true;
    if (this._recordStride == stride) {
      if ((r0 & 0xFF) == 1)
        candidates = false;
      phase = r0 >> 16 == 0xF6FF ? 0x100u : 0u;
    } else
      phase = (this._symbols % stride) << 8;
    keys[7] = phase + (uint)((b1 - b2) & 0xFF);
    this._ring[7][keys[7] * 4 + 2] = (byte)(2 * b1 - b2);

    var best = 8;
    uint bestScore = 0;
    if (candidates)
      for (var k = 0; k < 8; ++k) {
        var order = this._ring[k];
        var at = (int)keys[k] * 4;
        if (order[at + 1] == 0 || order[at + 3] >= 0x80)
          continue;
        var score = (uint)((order[at] << 8) * order[at + 3] / order[at + 1]);
        if (score > bestScore) {
          bestScore = score;
          best = k;
        }
      }

    if (best < 8) {
      var order = this._ring[best];
      var at = (int)keys[best] * 4;
      if (order[at] > order[at + 1] || (order[at + 3] & 0x7F) > 1) {
        if (this._strideLast > 1)
          this._rankBoost = best == 7;
        var hits = order[at];
        if (order[at + 1] == 2 && hits is 3 or 4)
          this._ringPairIndex = (hits - 3) * 8 + best;
        else if (order[at + 1] == 3 && hits is 4 or 5)
          this._ringPairIndex = 16 + (hits - 4) * 8 + best;
        this._ringChosen = best;
        return at;
      }
    }

    if (this._stride > 1)
      this._rank[this._rankPointer] >>= 1;
    return -1;
  }

  private int TryRing(int at, bool armed, bool engaged) {
    this._ringCredit >>= 2;
    var order = this._ring[this._ringChosen];
    var guess = order[at + 2];
    if (this.Excluded.Contains(guess)) {
      this._ringUpdate = false;
      return -1;
    }

    this._ringCharge = armed;
    if (engaged)
      return -1;

    uint hits, missCount;
    if (this._ringPairIndex >= 0) {
      hits = this._pairs[RingPairBase + this._ringPairIndex * 2];
      missCount = this._pairs[RingPairBase + this._ringPairIndex * 2 + 1];
    } else {
      hits = order[at];
      missCount = order[at + 1];
    }

    var count = this._coder.Count(hits + missCount, !TCoder.Encoding || guess == this._target ? 0u : hits);
    if (count < hits) {
      if (this._rankBoost) {
        this._rank[this._rankPointer] += this._rankStep;
        this._rankStep = Math.Min(this._rankStep * 2, 0xFFFFu);
      }

      this._coder.Consume(0, hits);
      return guess;
    }

    if (this._rankBoost) {
      this._rank[this._rankPointer] >>= 1;
      this._rankStep = (this._rankStep >> 1) + 0xFF;
    }

    this._coder.Consume(hits, missCount);
    this.Exclude(guess);
    return -1;
  }

  private void UpdateRing(byte symbol) {
    for (var k = 0; k < 8; ++k) {
      var order = this._ring[k];
      var at = (int)this._ringKey[k] * 4;
      if (order[at + 1] == 0) {
        order[at] = 2;
        order[at + 1] = 2;
        order[at + 2] = symbol;
        order[at + 3] = 0;
        continue;
      }

      var hit = symbol == order[at + 2];
      if (k == this._ringChosen) {
        if (this._ringCharge) {
          if (hit)
            this._classCharge = false;
          uint c = hit ? order[at] : order[at + 1];
          this._wpCost += Price[(c << 14) / (uint)(order[at] + order[at + 1])];
        }

        if (this._ringPairIndex >= 0)
          CountPair(this._pairs, RingPairBase + this._ringPairIndex * 2, hit);
      }

      order[at + 3] &= 0x7F;
      if (hit) {
        if (order[at] >= 0xFE) {
          order[at] = (byte)((order[at] + 1) >> 1);
          order[at + 1] = (byte)((order[at + 1] + 1) >> 1);
        }

        ++order[at];
        if (order[at + 3] < 0x7F)
          ++order[at + 3];
      } else {
        if (order[at + 1] >= 0xFE) {
          order[at] = (byte)((order[at] + 1) >> 1);
          order[at + 1] = (byte)((order[at + 1] + 1) >> 1);
        }

        ++order[at + 1];
        if (order[at + 3] < 2) {
          order[at + 2] = symbol;
          order[at + 3] = 0x80;
        } else
          order[at + 3] = (byte)((order[at + 3] >> 1) + 0x80);
      }
    }
  }

  // ── The rows ───────────────────────────────────────────────────────────

  /// <summary>
  /// The last stage: the order-1 row (in strided binary data the byte one stride back keys it), or,
  /// when its decayed cost is lower, the class row coding the difference from the class's
  /// predicted byte; the path not taken is charged what it would have cost.
  /// </summary>
  private int DecodeFromRows(bool binary, bool gate, bool classSelected, bool armed) {
    this._classCharge = armed;
    var classRow = 0x201;
    var classFallback = 0x300;
    int row;
    byte bias;
    var word = this._context;
    if (binary && this._stride > 1 && this._rank[this._rankPointer] != 0) {
      var b1 = this._window[(this._position - this._stride) & this._mask];
      var b2 = this._window[(this._position - 2 * this._stride) & this._mask];
      row = 0x100 + b1;
      bias = b1;
      if (b1 == b2)
        classRow = 0x202;
    } else {
      var b = (byte)word;
      row = b;
      bias = b;
      if (b == (byte)(word >> 8))
        classRow = 0x202;
      if (!binary && UharcPpmTables.TextByte[(word >> 8) & 0xFF] != 0)
        row += 0x100;
    }

    var negate = false;
    if (classSelected) {
      classRow = this.ClassRow;
      classFallback = 0x300 + this._selector;
      bias = this._side.Bias;
      negate = this._side.Negate;
    }

    if (!gate) {
      this._rowTarget = this._target;
      var symbol = this.DecodeChosen(row, LiteralFallback);
      if (binary) {
        var delta = negate ? (byte)(bias - symbol) : (byte)(symbol - bias);
        this.ToDifferences(bias, negate);
        this.ChargeOther(classRow, classFallback, delta);
      }

      return symbol;
    }

    this.ToDifferences(bias, negate);
    this._rowTarget = negate ? (byte)(bias - this._target) : (byte)(this._target - bias);
    var difference = this.DecodeChosen(classRow, classFallback);
    var value = negate ? (byte)(bias - difference) : (byte)(bias + difference);
    for (var k = 0; k < this._excludedCount; ++k)
      this._excluded[k] = negate ? (byte)(bias - this._excluded[k]) : (byte)(this._excluded[k] + bias);
    this.ChargeOther(row, LiteralFallback, value);
    return value;
  }

  private void ToDifferences(byte bias, bool negate) {
    for (var k = 0; k < this._excludedCount; ++k)
      this._excluded[k] = negate ? (byte)(bias - this._excluded[k]) : (byte)(this._excluded[k] - bias);
  }

  // ── Per-symbol upkeep ──────────────────────────────────────────────────

  /// <summary>Marker counters, the stride detector, the multimedia class predictor and the context history.</summary>
  private void Upkeep(byte value, bool trackMarkers) {
    if (trackMarkers)
      this.TrackMarkers(value);
    this.TrackRepetition(value);
    ++this._symbols;
    if (this._kind == 0 && value is >= 4 and <= 6) {
      if ((this._roll0 & 0xFF) != 0xDF)
        this.Roll(0xDF);
    } else
      this.Roll(value);

    if (this._selector != 0)
      this._side.Observe(value, this._roll0, this._symbols);
  }

  private void TrackMarkers(byte value) {
    var lane = (int)(this._symbols & 1);
    var other = 1 - lane;
    this._markers[lane] = (byte)((this._markers[lane] * 2) & 0xF);
    this._markers[2 + lane] = (byte)((this._markers[2 + lane] * 2) & 0xF);
    this._markerState = 0;
    if (value < 0x80)
      return;

    if (value == 0xFF) {
      ++this._markers[2 + lane];
      if (this._markers[other] == 0xF && this._markers[other] == this._markers[2 + lane])
        this._markerState = 2;
      return;
    }

    if (value is <= 0xDF or (>= 0xF2 and <= 0xF6)) {
      var count = ++this._markers[lane];
      if (this._markers[2 + other] == 0xF && count == this._markers[2 + other])
        this._markerState = 1;
    }
  }

  /// <summary>
  /// The stride detector: the distance back to the last occurrence of the byte (or of a
  /// neighbour) gains a hit and climbs the ranked list past every distance with no more hits;
  /// one reaching the head becomes the stride. After 256 counted distances the hits halve.
  /// </summary>
  private void TrackRepetition(byte value) {
    if (value is 0 or 0xFF)
      return;

    var position = this._position;
    var distance = (position - this._lastAt[value]) & this._mask;
    if (distance >= 0x100) {
      distance = (position - this._lastAt[value - 1]) & this._mask;
      if (distance >= 0x100)
        distance = (position - this._lastAt[value + 1]) & this._mask;
    }

    if (distance != 0 && distance < 0x100) {
      ++this._distanceHits[distance];
      if (distance != this._stride) {
        var up = this._rankUp[distance];
        while (this._distanceHits[distance] >= this._distanceHits[up]) {
          // The list is relinked only where the two entries meet; the neighbours keep their links.
          this._rankUp[distance] = this._rankUp[up];
          this._rankDown[up] = this._rankDown[distance];
          this._rankUp[up] = (byte)distance;
          this._rankDown[distance] = up;
          up = this._rankUp[distance];
          if (up == 0) {
            this._stride = distance;
            if (distance > 1)
              this._strideLast = distance;
            break;
          }
        }
      }

      if (++this._strideEvents > 0x100) {
        uint sum = 0;
        for (var i = 0; i < 256; ++i)
          sum += this._distanceHits[i] >>= 1;
        this._strideEvents = sum;
      }
    }

    this._lastAt[value] = position;
  }
}

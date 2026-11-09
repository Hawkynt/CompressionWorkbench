namespace FileFormat.Uharc;

/// <summary>
/// PPM order-4 and order-2 records (docs/UHARC-ON-DISK.md, "PPM", Literals steps 1 and 3, Updates).
/// </summary>
/// <remarks>
/// An order-4 record (32 bytes, keyed by a hash of the last four bytes, up to eight in a chain
/// 0x8000 apart) holds an escape count, twelve counts and their symbols, a hit history, the
/// slot last hit with a confidence nibble, a scale and the four context bytes as a tag. An
/// order-2 record (20 bytes, indexed by the last two bytes) holds an escape count, eight counts
/// and symbols, a novelty count, the total, and the last-hit slot with confidence.
/// </remarks>
internal sealed partial class UharcPpmDecoder {

  private readonly byte[] _o4 = new byte[Order4Records * Order4Stride];
  private readonly byte[] _o2 = new byte[Order2Records * Order2Stride];

  private uint _contextWord;
  private uint _o4Index;
  private int _o2Index;
  private uint _o4Total;
  private uint _o4Rest;
  private uint _o2Total;
  private uint _o2Novelty;
  private uint _sinceMiss;
  private int _o4Slot;
  private int _o2Slot;
  private int _bonus;
  private bool _order2Update;
  private bool _o4Used;
  private bool _o4Charge;
  private bool _o2Charge;
  private uint _wpCost;
  private bool _wpKernel;

  private readonly uint[] _o4Hit = new uint[0x2000];
  private readonly uint[] _o4Escape = new uint[0x2000];
  private readonly uint[] _o4Main = new uint[0x800];
  private readonly uint[] _o4Predicted = new uint[0x200];
  private readonly uint[] _o4Order2 = new uint[0x100];
  private readonly uint[] _o4First = new uint[0x200];
  private readonly uint[] _o2Hit = new uint[0x100];
  private readonly uint[] _o2Main = new uint[0x800];
  private readonly uint[] _o2Predicted = new uint[0x100];
  private readonly uint[] _o2First = new uint[0x200];
  private readonly uint[] _o2Escape = new uint[0x20];
  private readonly (uint[] Cells, uint Index, int Slot)[] _order2Adapt = new (uint[], uint, int)[4];

  /// <summary>A new symbol does not displace an order-4 slot holding this count or more.</summary>
  private const int Order4Keep = 6;

  /// <summary>A new symbol does not displace an order-2 slot holding this count or more.</summary>
  private const int Order2Keep = 12;

  private Span<byte> O4(uint index) => this._o4.AsSpan((int)index * Order4Stride, Order4Stride);

  private Span<byte> O2(int index) => this._o2.AsSpan(index * Order2Stride, Order2Stride);

  private static uint Tag(ReadOnlySpan<byte> record) => (uint)(record[0x1C] | record[0x1D] << 8 | record[0x1E] << 16 | record[0x1F] << 24);

  /// <summary>Order-1 row of the current context, doubled in a text stream by the word-boundary flag.</summary>
  private int LiteralRow() {
    var row = (int)(this._contextWord & 0xFF);
    if (this._kind == 0 && UharcPpmTables.TextByte[(this._contextWord >> 8) & 0xFF] != 0)
      row += 0x100;
    return row;
  }

  private static uint Adapt(uint probability, bool up) => up ? probability + ((0x2000 - probability) >> 6) : probability - (probability >> 6);

  // ── Finding the order-4 record ─────────────────────────────────────────

  /// <summary>
  /// Walks the chain for the record tagged with the current context. On a miss
  /// <see cref="_o4Index"/> is moved past 0x40000, which makes the update create one.
  /// </summary>
  private bool FindOrder4(ref uint index) {
    if (index >= Order4Records || this.O4(index)[0] == 0) {
      if (this._o4Index < Order4Records)
        this._o4Index += Order4Records;
      return false;
    }

    while (this.O4(index)[0] != 0 && Tag(this.O4(index)) != this._contextWord) {
      this._o4Index += 0x8000;
      index += 0x8000;
      if (this._o4Index >= Order4Records || index >= Order4Records)
        break;
    }

    var found = index < Order4Records && this.O4(index)[0] != 0 && Tag(this.O4(index)) == this._contextWord;
    if (!found && this._o4Index < Order4Records)
      this._o4Index += Order4Records;
    return found;
  }

  private uint Order4Total(uint index) {
    var record = this.O4(index);
    uint total = record[0];
    for (var k = 0; k < 12 && record[1 + k] != 0; ++k)
      total += record[1 + k];
    return total;
  }

  // ── Decoding from the order-4 record ───────────────────────────────────

  private int SlotOf(ReadOnlySpan<byte> record, byte symbol) {
    for (var k = 0; k < 12; ++k) {
      if (k > 0 && record[k + 1] == 0)
        break;
      if (record[0xD + k] == symbol)
        return k;
    }

    return 12;
  }

  private static bool NewCount(uint probability, uint others, uint count, out uint value) {
    var room = 0x2000 - probability;
    if (room == 0 || room > 0x2000) {
      value = 0xFF;
      return true;
    }

    value = probability * others / room + 1;
    if (value <= count)
      return false;
    if (value >= 0x100)
      value = 0xFF;
    return true;
  }

  private bool BoundaryBits(uint word, out uint bits) {
    if (this._kind != 0) {
      bits = (word & 0xC0) >> 5;
      if ((word & 0xE0) == ((word >> 8) & 0xE0))
        ++bits;
    } else
      bits = ((word & 0xFF) == 0xDF ? 1u : 0u) + UharcPpmTables.TextByte[word & 0xFF] * 4u + UharcPpmTables.TextByte[(word >> 8) & 0xFF] * 2u;
    return true;
  }

  private bool EscapeIndex(byte escapes, uint others, bool order2Busy, bool longSince, out uint index) {
    index = 0;
    if (others >= 0x80)
      return false;

    this.BoundaryBits(this._contextWord, out var bits);
    bits += (order2Busy ? 8u : 0u) + (longSince ? 0x10u : 0u);
    index = UharcPpmTables.CountBucket[escapes] * 0x100u + UharcPpmTables.CountBucket[others & 0xFF] * 0x20u + bits;
    return true;
  }

  private enum Table { Main, Predicted, Order2, First }

  /// <summary>
  /// Decodes from the order-4 record after lifting the counts of up to four symbols (the one
  /// last hit, the order-1 prediction, the order-2 prediction, the first) by learned odds.
  /// Escaped symbols are added to the exclusions.
  /// </summary>
  private bool DecodeOrder4(uint index, int row, out int symbol) {
    symbol = -1;
    var source = this.O4(index);
    Span<byte> record = stackalloc byte[Order4Stride];
    source.CopyTo(record);
    var o2 = this.O2(this._o2Index);
    var o2Marker = o2[0x13];
    var predicted = this._predicted[row];
    var order2Symbol = o2[(o2Marker & 0xF) + 9];
    var last = record[0x1A] & 0xF;
    if (this._statTotal <= this._statHits)
      last = 12;
    var predictedSlot = this.SlotOf(record, predicted);
    var order2Slot = this.SlotOf(record, order2Symbol);
    var lastSymbol = last < 12 ? record[0xD + last] : (byte)0;

    Span<bool> active = stackalloc bool[12];
    active.Fill(true);
    var rest = this._o4Total;
    if (this._excludedCount != 0)
      for (var s = 0; s < 12; ++s) {
        if (s > 0 && record[s + 1] == 0)
          break;
        if (record[0xD + s] == this._excluded[0]) {
          active[s] = false;
          rest -= record[s + 1];
          break;
        }
      }

    var others = rest - record[0];
    var order2Busy = o2[7] != 0;
    var longSince = this._sinceMiss >= 6;
    var hasEscape = this.EscapeIndex(record[0], others, order2Busy, longSince, out var escapeIndex);
    var total = hasEscape ? ((others << 13) / this._o4Escape[escapeIndex]) + 1 : rest;

    var baseWeight = (uint)UharcPpmTables.Order4Weight[record[0x1A] >> 4];
    var order2Weight = (uint)UharcPpmTables.Order2Weight[o2Marker >> 4];
    var confidenceWeight = (uint)UharcPpmTables.ConfidenceWeight[this._confidence[row]];
    var upper = (this._contextWord & 0xC0) == 0xC0 ? 0x10u : 0u;
    this._o4Rest = rest;

    Span<(Table Table, uint Index, int Slot)> adapt = stackalloc (Table, uint, int)[4];
    var adapted = 0;

    if (last < 12 && active[last]) {
      uint count = record[last + 1];
      var mult = rest - count;
      var lift = baseWeight + (count - (count == 2 ? 1u : 0u)) + (uint)(record[0x1B] >> 4);
      uint flags = 0;
      if (lastSymbol == order2Symbol) {
        lift += order2Weight;
        flags = 0x40;
      }

      if (lastSymbol == predicted) {
        lift += confidenceWeight;
        flags |= 0x80;
      }

      var cell = mult + lift != 0 ? (lift << 4) / (mult + lift) : 0;
      if (last != 0)
        cell |= 0x100;
      if ((lastSymbol & 0xC0) == 0xC0)
        cell |= 0x20;
      if (this.Seen(lastSymbol))
        cell |= 0x200;
      if (this._symbols == this._lastSeenAt[lastSymbol])
        cell |= 0x400;
      cell |= flags | upper;
      adapt[adapted++] = (Table.Main, cell, last);
      if (NewCount(this._o4Main[cell], mult, count, out var value)) {
        total = total + value - count;
        record[last + 1] = (byte)value;
      }
    }

    if (predictedSlot < 12 && active[predictedSlot] && last != predictedSlot) {
      uint count = record[predictedSlot + 1];
      var mult = rest - count;
      var lift = count + confidenceWeight;
      var cell = mult + lift != 0 ? (lift << 4) / (mult + lift) : 0;
      if ((predicted & 0xC0) == 0xC0)
        cell |= 0x20;
      if (predicted == order2Symbol)
        cell |= 0x40;
      if (this.Seen(predicted))
        cell |= 0x80;
      if (this._symbols == this._lastSeenAt[predicted])
        cell |= 0x100;
      cell |= upper;
      adapt[adapted++] = (Table.Predicted, cell, predictedSlot);
      if (NewCount(this._o4Predicted[cell], mult, count, out var value)) {
        total = total + value - count;
        record[predictedSlot + 1] = (byte)value;
      }
    }

    if (order2Slot < 12 && active[order2Slot] && last != order2Slot && order2Slot != predictedSlot) {
      uint count = record[order2Slot + 1];
      var mult = rest - count;
      var lift = count + order2Weight;
      var cell = mult + lift != 0 ? (lift << 4) / (mult + lift) : 0;
      if ((order2Symbol & 0xC0) == 0xC0)
        cell |= 0x20;
      if (this.Seen(order2Symbol))
        cell |= 0x40;
      if (this._symbols == this._lastSeenAt[order2Symbol])
        cell |= 0x80;
      cell |= upper;
      adapt[adapted++] = (Table.Order2, cell, order2Slot);
      if (NewCount(this._o4Order2[cell], mult, count, out var value)) {
        total = total + value - count;
        record[order2Slot + 1] = (byte)value;
      }
    }

    if (last != 0 && predictedSlot != 0 && order2Slot != 0 && active[0] && record[1] != 0) {
      uint count = record[1];
      var cell = rest != 0 ? (count << 4) / rest : 0;
      var first = record[0xD];
      if ((first & 0xC0) == 0xC0)
        cell |= 0x20;
      if (this.Seen(first))
        cell |= 0x80;
      if (this._symbols == this._lastSeenAt[first])
        cell |= 0x100;
      cell |= upper;
      adapt[adapted++] = (Table.First, cell, 0);
      var value = Math.Min(((rest * this._o4First[cell]) >> 13) + 1, 0xFFu);
      total = total + value - count;
      record[1] = (byte)value;
    }

    var escaped = true;
    var hitSlot = -1;
    if (total != 0) {
      var count = this._coder.GetCount(total) & 0xFFFF;
      uint low = 0, high = total;
      var placed = false;
      for (var s = 0; s < 12; ++s) {
        if (!active[s])
          continue;
        uint f = record[s + 1];
        if (f == 0)
          break;
        if (count < low + f) {
          symbol = record[0xD + s];
          hitSlot = s;
          high = low + f;
          escaped = false;
          placed = true;
          break;
        }

        this.Exclude(record[0xD + s]);
        low += f;
      }

      if (!placed) {
        escaped = true;
        high = total;
      }

      this._o4Slot = escaped ? 12 : hitSlot;
      this._coder.Consume(low, high - low);
    }

    for (var i = 0; i < adapted; ++i) {
      var hit = !escaped && adapt[i].Slot == hitSlot;
      var cells = adapt[i].Table switch {
        Table.Main => this._o4Main,
        Table.Predicted => this._o4Predicted,
        Table.Order2 => this._o4Order2,
        _ => this._o4First,
      };
      cells[adapt[i].Index] = Adapt(cells[adapt[i].Index], hit);
    }

    if (hasEscape)
      this._o4Escape[escapeIndex] = Adapt(this._o4Escape[escapeIndex], !escaped);

    return !escaped;
  }

  /// <summary>One binary decision: is the next byte the order-4 record's top symbol?</summary>
  private bool DecodeOrder4Top(uint index, out int symbol) {
    var word = this._contextWord;
    var o2Index = (int)(word & 0xFFFF);
    if (this._kind == 0 && UharcPpmTables.TextByte[(word >> 16) & 0xFF] != 0)
      o2Index += 0x10000;
    var o2 = this.O2(o2Index);
    var row = this.LiteralRow();
    var predicted = this._predicted[row];
    var o2Top = o2[(o2[0x13] & 0xF) + 9];
    var record = this.O4(index);
    var top = record[0xD];

    uint count = record[1];
    if (top == o2Top)
      count += (uint)o2[0x13] >> 4;
    if (top == predicted)
      count += this._confidence[row];

    uint cell = 0;
    if (top == o2Top || top == predicted)
      cell = 1;
    if (o2[7] != 0 && this._sinceMiss < 6)
      cell |= 0x10;
    if (this._kind == 0) {
      if (UharcPpmTables.TextByte[top] != 0)
        cell |= 2;
    } else {
      if (this.Seen(top))
        cell |= 0x100;
      if (top == (word & 0xFF))
        cell |= 2;
    }

    if (count < 0x1E) {
      cell |= word & 0xE0;
      if (this._kind == 0) {
        cell |= (uint)UharcPpmTables.TextByte[(word >> 8) & 0xFF] << 2;
        cell |= (uint)UharcPpmTables.TextByte[word & 0xFF] << 3;
      } else {
        if ((word & 0xE0) == ((word >> 8) & 0xE0))
          cell |= 8;
        if ((word & 0xE0) == ((uint)top & 0xE0))
          cell |= 4;
      }
    }

    if (this._symbols == this._lastSeenAt[top])
      cell |= 0x200;

    var page = UharcPpmTables.PageOfCount[count] * 0x400u + cell;
    var probability = this._o4Hit[page] & 0xFFFF;
    var hit = this._coder.DecodeScaled(probability, 0x2000);
    this._o4Hit[page] = Adapt(probability, hit);
    symbol = hit ? top : -1;
    return hit;
  }

  // ── Decoding from the order-2 record ───────────────────────────────────

  private uint Order2Total(Span<byte> record, Span<bool> active, out int used) {
    uint total = record[0x12];
    used = 0;
    for (var s = 0; s < 8; ++s) {
      if (s > 0 && record[s + 1] == 0)
        break;
      ++used;
    }

    active.Fill(true);
    foreach (var x in this.ExcludedList)
      for (var s = 0; s < used; ++s)
        if (active[s] && record[9 + s] == x) {
          active[s] = false;
          total -= record[s + 1];
          break;
        }

    return total;
  }

  private bool DecodeOrder2(int index, int hint, out int symbol) {
    symbol = -1;
    Span<byte> record = stackalloc byte[Order2Stride];
    this.O2(index).CopyTo(record);
    Span<bool> active = stackalloc bool[8];
    var baseTotal = this.Order2Total(record, active, out var used);
    this._o2Total = baseTotal;
    if (used == 0)
      return false;

    var last = this._statHits >= this._statTotal ? 8 : record[0x13] & 0xF;
    var total = baseTotal;
    var row = this.LiteralRow();
    var predicted = this._predicted[row];
    var upper = (this._contextWord & 0xC0) == 0xC0 ? 0x10u : 0u;
    var order2Weight = (uint)UharcPpmTables.Order2Weight[record[0x13] >> 4];
    var confidenceWeight = (uint)UharcPpmTables.ConfidenceWeight[this._confidence[row]];

    var adapt = this._order2Adapt;
    var adapted = 0;

    var others = baseTotal - record[0];
    if (used < 8 && others < 0x10) {
      var cell = (uint)UharcPpmTables.EscapeBias[used] + (others & 0xC) + (record[0x12] == baseTotal ? 1u : 0u);
      var p = this._o2Escape[cell];
      if (p != 0) {
        adapt[adapted++] = (this._o2Escape, cell, 8);
        total = ((others << 13) / p) + 1 + (uint)(record[0] >> 1);
      }
    }

    if (last < 8 && active[last] && record[last + 1] != 0) {
      uint count = record[last + 1];
      var top = record[last + 9];
      var mult = baseTotal - count;
      var lift = count + order2Weight;
      var cell = 0u;
      if (top == predicted) {
        lift += confidenceWeight;
        cell = 0x80;
      }

      cell |= mult + lift != 0 ? (lift << 4) / (mult + lift) : 0;
      if (last != 0)
        cell |= 0x100;
      if ((top & 0xC0) == 0xC0)
        cell |= 0x20;
      if (this._ringReady && top == this._ringBest.Predicted)
        cell |= 0x40;
      if (this.Seen(top))
        cell |= 0x200;
      if (this._symbols == this._lastSeenAt[top])
        cell |= 0x400;
      cell |= upper;
      adapt[adapted++] = (this._o2Main, cell, last);
      if (NewCount(this._o2Main[cell], mult, count, out var value)) {
        total = total + value - count;
        record[last + 1] = (byte)value;
      }
    }

    if (hint < 8 && active[hint] && hint != last && record[hint + 1] != 0) {
      uint count = record[hint + 1];
      var sym = record[hint + 9];
      var mult = baseTotal - count;
      var lift = count + confidenceWeight;
      var cell = mult + lift != 0 ? (lift << 4) / (mult + lift) : 0;
      if ((sym & 0xC0) == 0xC0)
        cell |= 0x20;
      if (this.Seen(sym))
        cell |= 0x40;
      if (this._symbols == this._lastSeenAt[sym])
        cell |= 0x80;
      cell |= upper;
      adapt[adapted++] = (this._o2Predicted, cell, hint);
      if (NewCount(this._o2Predicted[cell], mult, count, out var value)) {
        total = total + value - count;
        record[hint + 1] = (byte)value;
      }
    }

    if (active[0] && last != 0 && hint != 0 && record[1] != 0) {
      uint count = record[1];
      var first = record[9];
      var cell = baseTotal != 0 ? (count << 4) / baseTotal : 0;
      if ((first & 0xC0) == 0xC0)
        cell |= 0x20;
      if (this._ringReady && first == this._ringBest.Predicted)
        cell |= 0x40;
      if (this.Seen(first))
        cell |= 0x80;
      if (this._symbols == this._lastSeenAt[first])
        cell |= 0x100;
      cell |= upper;
      adapt[adapted++] = (this._o2First, cell, 0);
      var value = Math.Min(((baseTotal * this._o2First[cell]) >> 13) + 1, 0xFFu);
      total = total + value - count;
      record[1] = (byte)value;
    }

    var escaped = true;
    var hitSlot = 8;
    var decoded = false;
    if (total != 0) {
      var count = this._coder.GetCount(total) & 0xFFFF;
      uint low = 0, high = total;
      for (var s = 0; s < 8; ++s) {
        if (!active[s])
          continue;
        uint f = record[s + 1];
        if (f == 0)
          break;
        if (count < low + f) {
          hitSlot = s;
          symbol = record[s + 9];
          high = low + f;
          escaped = false;
          break;
        }

        this.Exclude(record[s + 9]);
        low += f;
      }

      if (escaped)
        high = total;
      this._o2Slot = escaped ? 8 : hitSlot;
      this._coder.Consume(low, high - low);
      decoded = !escaped;
    }

    for (var i = 0; i < adapted; ++i) {
      var hit = adapt[i].Slot == 8 ? decoded : decoded && adapt[i].Slot == hitSlot;
      adapt[i].Cells[adapt[i].Index] = Adapt(adapt[i].Cells[adapt[i].Index], hit);
    }

    return decoded;
  }

  /// <summary>One binary decision on an order-2 record that holds a single symbol.</summary>
  private bool DecodeOrder2Single(int index, out uint probability) {
    var record = this.O2(index);
    var only = record[9];
    var row = this.LiteralRow();
    var predicted = this._kind != 0 ? this._ringPredicted[row & 0xFF] : this._predicted[row];
    uint cell = only == predicted ? 1u : 0u;
    uint count = (uint)record[1] >> 1;
    if (only == predicted)
      count += this._confidence[row];
    if (count < 0x10) {
      if (this._kind == 0) {
        cell |= (uint)UharcPpmTables.TextByte[(this._contextWord >> 8) & 0xFF] << 1;
        cell |= (uint)UharcPpmTables.TextByte[this._contextWord & 0xFF] << 2;
      } else
        cell |= (this._contextWord & 0xC0) >> 5;
    }

    if (this._kind != 0 && this.Seen(only))
      cell |= 8;
    if (this._symbols == this._lastSeenAt[only])
      cell |= 0x10;

    var page = UharcPpmTables.PageOfCount[count] * 0x20u + cell;
    probability = this._o2Hit[page] & 0xFFFF;
    var hit = this._coder.DecodeScaled(probability, 0x2000);
    this._o2Hit[page] = Adapt(probability, hit);
    return hit;
  }

  // ── Updating the records ───────────────────────────────────────────────

  private void CountStatistics() {
    this._statTotal += 2;
    if (this._statTotal > 0x800) {
      this._statTotal >>= 1;
      this._statOrder4 >>= 1;
      this._statHits >>= 1;
    }
  }

  private static int SortOrder4(Span<byte> r, int slot) {
    while (slot > 0 && r[1 + slot] >= r[slot]) {
      (r[1 + slot], r[slot]) = (r[slot], r[1 + slot]);
      (r[0xD + slot], r[0xC + slot]) = (r[0xC + slot], r[0xD + slot]);
      --r[0x1A];
      --slot;
    }

    return slot;
  }

  /// <summary>Creates the order-4 record for the current context, replacing the weakest of its chain.</summary>
  private void CreateOrder4(byte symbol) {
    var head = this._o4Index & 0x7FFF;
    uint step = 0, best = 0;
    if (this.O4(head)[0] != 0) {
      var current = head;
      while (true) {
        var bestScore = (uint)(this.O4(head + best)[0x19] & 0x3F);
        if (this.O4(head + best)[1] > this.O4(current)[1] + 1u)
          ++bestScore;
        if (bestScore > (uint)(this.O4(current)[0x19] & 0x3F))
          best = step;
        step += 0x8000;
        current += 0x8000;
        if (step >= Order4Records)
          break;
        if (this.O4(current)[0] == 0) {
          best = step;
          break;
        }
      }
    }

    if (step < 0x38000)
      this.O4(head + step + 0x8000)[0] = 0;

    var record = this.O4(head + best);
    record.Clear();
    record[0] = 1;
    record[0x1A] = 0x20;
    record[1] = (byte)(this._bonus + 1);
    record[0xD] = symbol;
    record[0x1C] = (byte)this._contextWord;
    record[0x1D] = (byte)(this._contextWord >> 8);
    record[0x1E] = (byte)(this._contextWord >> 16);
    record[0x1F] = (byte)(this._contextWord >> 24);
    this._o4Index = head;
    this._hashConfidence[this._hashSlot] = record[0x1A];
  }

  private int CountOrder4(Span<byte> r, byte symbol, int hint, out bool hit) {
    hit = false;
    var slot = hint;
    if (slot == 0xFF) {
      slot = 0;
      while (slot < 12 && r[0xD + slot] != symbol)
        ++slot;
    }

    if (slot >= 12) {
      if (UharcPpmTables.Order4Threshold[r[0]] < this._o4Total)
        for (var i = 0; i <= 12; ++i)
          r[i] = (byte)((r[i] + 1) >> 1);
      r[0] += 2;
      r[0x1B] >>= 1;
      var free = 1;
      while (free < 11 && r[1 + free] != 0)
        ++free;
      r[0x19] = (byte)((r[0x19] & 0xC0) | ((r[0x19] & 0x3F) >> 1));

      // A full record gives up its last slot only when that count is below 6.
      if (r[1 + free] >= Order4Keep)
        return 12;
      r[1 + free] = (byte)(this._bonus + 2);
      r[0xD + free] = symbol;
      r[0x1A] = (byte)free;
      return SortOrder4(r, free);
    }

    hit = true;
    var halve = UharcPpmTables.Order4Threshold[r[0]] < this._o4Total;
    var flags = r[0x19] & 0xC0;
    if (r[1 + slot] + 1 < r[1] >> 1) {
      if (flags == 0xC0)
        halve = true;
      flags = (flags + 0x40) & 0xFF;
    } else
      flags = (flags >> 1) & 0xC0;
    r[0x19] = (byte)((r[0x19] & 0x3F) | flags);
    var streak = slot != 0 && r[0x1A] >> 4 >= 4 && (r[0x1A] & 0xF) == slot;
    if (streak || halve)
      for (var i = 0; i <= 12; ++i)
        r[i] = (byte)((r[i] + 1) >> 1);
    r[1 + slot] += 2;
    if ((r[0x19] & 0x3F) < 0x3F)
      ++r[0x19];
    if ((r[0x1A] & 0xF) == slot) {
      if (r[0x1A] < 0xF0)
        r[0x1A] += 0x10;
      if (r[0x1B] < 0xF8)
        r[0x1B] += 8;
    } else {
      r[0x1A] = (byte)slot;
      r[0x1B] >>= 1;
    }

    return SortOrder4(r, slot);
  }

  private void UpdateOrder4(byte symbol, uint index, int hint) {
    if (index >= Order4Records) {
      this.CreateOrder4(symbol);
      return;
    }

    var record = this.O4(index);
    if (this._o4Used) {
      var slot = hint;
      if (slot == 0xFF) {
        slot = 0;
        while (slot < 12 && record[0xD + slot] != symbol)
          ++slot;
      }

      if (slot < 12 && record[slot + 1] >> 6 > record[0])
        this._statHits += 4;
    }

    if (this._o4Charge) {
      var slot = hint;
      if (slot == 0xFF) {
        slot = 0;
        while (slot < 12 && record[0xD + slot] != symbol)
          ++slot;
      }

      uint count;
      if (slot < 12) {
        count = record[slot + 1];
        this._o2Charge = false;
        this._ringRan = false;
        this._wpKernel = false;
      } else
        count = record[0];

      if (this._o4Rest != 0) {
        var key = (count << 14) / this._o4Rest;
        if (key < 0x4000)
          this._wpCost += Cost[key];
      }
    }

    this._o4Slot = this.CountOrder4(record, symbol, hint, out var hit);
    if (hit)
      this._statOrder4 += 5;
    this.CountStatistics();
    this._hashConfidence[this._hashSlot] = this.O4(index)[0x1A];
  }

  private static int SortOrder2(Span<byte> r, int slot) {
    while (slot > 0 && r[slot + 1] >= r[slot]) {
      (r[slot + 1], r[slot]) = (r[slot], r[slot + 1]);
      (r[slot + 8], r[slot + 9]) = (r[slot + 9], r[slot + 8]);
      --r[0x13];
      --slot;
    }

    return slot;
  }

  private void HalveOrder2IfDue(Span<byte> r) {
    if (r[0x12] <= UharcPpmTables.Order2Threshold[r[0]])
      return;

    r[0x12] = 0;
    for (var a = 8; a >= 0; --a) {
      var h = (byte)(((r[a] + 1) & 0xFF) >> 1);
      r[a] = h;
      r[0x12] += h;
    }
  }

  /// <summary>Counts <paramref name="symbol"/> in the order-2 record (weight 4, or 1 when <paramref name="light"/>).</summary>
  private void UpdateOrder2(byte symbol, bool light) {
    var r = this.O2(this._o2Index);
    var step = light ? 1 : 4;
    if (r[0] == 0) {
      r.Clear();
      r[0] = (byte)(light ? 2 : 3);
      r[1] = r[0];
      r[0x12] = (byte)(light ? 4 : 6);
      r[9] = symbol;
      return;
    }

    var slot = light ? 0xFF : this._o2Slot;
    if (light)
      this._o2Slot = 0;
    if (slot == 0xFF) {
      slot = 0;
      while (slot < 8 && r[slot + 9] != symbol)
        ++slot;
    }

    if (!light && this._o2Charge) {
      if (slot < 8) {
        this._ringRan = false;
        this._wpKernel = false;
      }

      uint count = slot >= 8 ? r[0] : r[slot + 1];
      var key = this._o2Total != 0 ? ((count << 14) / this._o2Total) & 0x1FFFF : 0;
      if (key < 0x4000)
        this._wpCost += Cost[key];
    }

    this.HalveOrder2IfDue(r);
    r[0x12] += (byte)step;

    if (slot < 8) {
      if (!light) {
        this._o2Novelty = (this._o2Novelty * 3) >> 2;
        r[0x11] >>= 1;
      }

      r[slot + 1] += (byte)step;
      if ((r[0x13] & 0xF) == slot) {
        if (r[0x13] < (light ? 0x40 : 0xF0))
          r[0x13] += 0x10;
      } else
        r[0x13] = (byte)slot;
      if (!light)
        this._statOrder4 += 5;
      this._o2Slot = SortOrder2(r, slot);
    } else {
      if (!light)
        ++this._o2Novelty;
      ++r[0x11];
      r[0] += (byte)step;
      var free = 1;
      while (free < 7 && r[free + 1] != 0)
        ++free;
      // A full record gives up its last slot only when that count is below 12.
      var old = r[free + 1];
      if (old < Order2Keep) {
        r[0x12] += (byte)(step - old);
        r[free + 1] = (byte)step;
        r[free + 9] = symbol;
        r[0x13] = (byte)free;
        this._o2Slot = SortOrder2(r, free);
      } else
        this._o2Slot = 0;
    }

    if (!light)
      this.CountStatistics();
  }

  // ── Halving at reset points ────────────────────────────────────────────

  private void QuarterOrder4() {
    for (var head = 0u; head < 0x8000; ++head) {
      var index = head;
      while (index < Order4Records && this.O4(index)[0] != 0) {
        var r = this.O4(index);
        var sum = 0;
        for (var s = 1; s <= 12; ++s) {
          r[s] >>= 2;
          sum += r[s];
        }

        if (sum == 0) {
          var next = index;
          while (next + 0x8000 < Order4Records && this.O4(next + 0x8000)[0] != 0) {
            this.O4(next + 0x8000).CopyTo(this.O4(next));
            next += 0x8000;
          }

          this.O4(next)[0] = 0;
        } else {
          r[0] = (byte)((r[0] >> 2) + 1);
          r[0x19] = (byte)((r[0x19] & 0x3F) >> 1);
          r[0x1A] &= 0xF;
          r[0x1B] = 0;
          if (r[r[0x1A] + 1] == 0)
            r[0x1A] = 0;
          index += 0x8000;
        }
      }
    }
  }

  private void QuarterOrder2() {
    var limit = Math.Min(0x20000 >> this._kind, Order2Records);
    for (var i = 0; i < limit; ++i) {
      var r = this.O2(i);
      if (r[0] == 0)
        continue;

      var sum = 0;
      for (var k = 7; k >= 0; --k) {
        r[1 + k] >>= 2;
        sum = (sum + r[1 + k]) & 0xFF;
      }

      r[0] = sum == 0 ? (byte)0 : (byte)((r[0] >> 2) + 1);
      r[0x11] >>= 1;
      r[0x13] &= 0xF;
      r[0x12] = (byte)(sum + r[0]);
      if (r[1 + r[0x13]] == 0)
        r[0x13] = 0;
    }
  }
}

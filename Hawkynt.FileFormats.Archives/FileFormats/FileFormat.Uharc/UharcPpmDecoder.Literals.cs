namespace FileFormat.Uharc;

/// <summary>
/// The PPM literal cascade (docs/UHARC-ON-DISK.md, "PPM", Literals).
/// </summary>
internal sealed partial class UharcPpmDecoder {

  // Byte delta between the two previous stride positions -> class context offset.
  private static readonly byte[] DeltaContext = [
    0, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
    4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7,
  ];

  private uint _ageTotal = 0x91ADC0;
  private int _ageSlot = 0xFF;
  private readonly uint[] _ageRing = new uint[256];
  private readonly uint[] _costContext = new uint[2];
  private readonly uint[] _costOrderZero = new uint[2];

  private uint _wpA;
  private uint _wpB;
  private uint _wpAdd;
  private bool _wpPending;
  private bool _wpSelected;
  private bool _wpArmed;
  private bool _wpEngaged;
  private uint _wpKernelCost;
  private uint _ringCredit;

  private bool _ringEligible;
  private bool _ringTried;
  private bool _ringWanted;
  private bool _ringRecheck;
  private uint _ringOrder4Weight;
  private byte _ringOrder4Guess;
  private uint _rankBefore;
  private uint _rankAfter;

  private enum Outcome { Decoded, NotDecoded, Escaped }

  /// <summary>The context word: the last four bytes, or alternate bytes in interleaved 16-bit text.</summary>
  private uint LiteralContext() => this._markerState == 2
    ? ((this._roll0 >> 8) & 0xFF) | ((this._roll0 >> 16) & 0xFF00) | ((this._roll1 << 8) & 0xFF0000) | (this._roll1 & 0xFF000000)
    : this._roll0;

  private bool LinearPredictorGate() => this._selector is 5 or 6
    ? this._costContext[0] * 31 < this._costContext[1] * 32
    : this._costContext[0] < this._costContext[1];

  private bool ClassSelected() => this._kind != 0
    ? this._wpSelected
    : this._selector != 0 && (this._stride <= 4 || this._rankSmall[this._rankSmallPointer] <= 0x3FE);

  private void TakeExcludedFromCopy() {
    if (!this._hasExcluded)
      return;

    this._excluded[0] = this._window[this._excludedPosition];
    this._excludedCount = 1;
    this._hasExcluded = false;
    this._excludedPosition = 0;
  }

  // ── Entry point ────────────────────────────────────────────────────────

  private int DecodeLiteral(byte[] output, int written) {
    if (this._kind == 0)
      return this.DecodeLiteralCore(output, written, out _);

    this._excludedCount = 0;
    this.TakeExcludedFromCopy();
    this._o2Charge = false;
    this._order2Update = false;
    this._rankBoost = false;
    this._contextWord = this.LiteralContext();
    this._o4Index = (this._contextWord ^ (this._contextWord >> 13)) & 0x7FFF;
    this._o2Index = (int)(this._contextWord & 0xFFFF);
    this.PointRanksAtStride();
    this.MarkRecentPredictions();

    this._wpSelected = this._selector != 0 && (this._stride <= 4 || this._rankSmall[this._rankSmallPointer] <= 0x3FE);
    this._wpArmed = this._wpSelected && this.LinearPredictorGate();
    this._wpEngaged = this._wpArmed && this._wpA < this._wpB;
    this._wpKernelCost = 0;
    this._ringOrder4Weight = 0;
    this._ringOrder4Guess = 0;

    var contextByte = (byte)this._contextWord;
    var o2Index = (int)(this._contextWord & 0xFFFF);
    this._rankBefore = this._rankSmall[this._rankSmallPointer];
    var prepared = this.PrepareRing();
    this._rankAfter = this._rankSmall[this._rankSmallPointer];
    this._rankBoost = this._ringStrideLevel;

    var o2 = this.O2(o2Index);
    this._order2Update = o2[0x13] >= 0x90 || this._ringSkip;
    var wanted = !this._order2Update;
    this._ringRecheck = wanted;
    if (!wanted) {
      var chain = this._o2Novelty == 0 || o2[0x11] == 0 || (this._rankStep < 0xFFFF && this._rankSmall[this._rankSmallPointer] <= 0x3FE);
      this._order2Update = chain;
      wanted = !chain;
    }

    if (!wanted)
      wanted = o2[0] == 0;

    this._ringEligible = prepared;
    this._ringTried = false;
    this._ringRan = false;
    this._ringArmed = false;
    this._ringWanted = wanted;

    written = this.DecodeLiteralCore(output, written, out var symbol);

    if (this._ringArmed)
      this.UpdateRing((byte)symbol);

    if (this._wpPending) {
      if (this._wpKernel)
        this._wpCost += this._wpKernelCost;
      this._wpA = ((this._wpA * 31) >> 5) + this._wpAdd;
      this._wpB = ((this._wpB * 31) >> 5) + this._wpCost;
      this._wpPending = false;
    }

    this.CountRingByte(contextByte, (byte)symbol);
    return written;
  }

  private void PrepareLiteral(out int row, out bool predictionGate, out uint o4Index) {
    var word = this.LiteralContext();
    this._contextWord = word;
    this._o4Index = (word ^ (word >> 13)) & 0x7FFF;
    row = this.LiteralRow();
    if (this._kind == 0) {
      this._o2Charge = false;
      this._order2Update = false;
      this._excludedCount = 0;
    }

    this._o4Used = false;
    this._o4Charge = false;
    this._wpKernel = false;
    this._bonus = 0;
    this._wpCost = 0;
    if (this._kind == 0) {
      this._rankBoost = false;
      this.TakeExcludedFromCopy();
    }

    var o2Index = (int)(word & 0xFFFF);
    if (this._kind == 0 && UharcPpmTables.TextByte[(word >> 16) & 0xFF] != 0)
      o2Index += 0x10000;
    this._o2Index = o2Index;
    o4Index = this._o4Index & 0x7FFF;
    if (this._kind == 0)
      this.PointRanksAtStride();
    predictionGate = this.StrideGate();
    if (this._kind == 0)
      this.MarkRecentPredictions();
  }

  // ── The cascade ────────────────────────────────────────────────────────

  private int DecodeLiteralCore(byte[] output, int written, out int symbol) {
    if (this._kind != 0)
      this._rankSmall[this._rankSmallPointer] = this._rankBefore;
    this.PrepareLiteral(out var row, out var gate, out var o4Index);

    var predicted = this.FindOrder4(ref o4Index);
    if (predicted) {
      var record = this.O4(o4Index);
      this._ringOrder4Weight = (uint)UharcPpmTables.Order4Weight[record[0x1A] >> 4];
      this._ringOrder4Guess = record[0xD + (record[0x1A] & 0xF)];
      this._o4Slot = 0xFF;
      this._o4Total = this.Order4Total(o4Index);
      if (!this.Order4InlineGate(o4Index) || !this.Order4LateGate(o4Index, this._o2Index))
        predicted = false;
      else {
        ++this._sinceMiss;
        if (!this.Order4FrequencyGate(o4Index))
          predicted = false;
        else
          this._o4Used = true;
      }
    }

    if (predicted && this._kind != 0) {
      this._o4Charge = this._wpArmed;
      predicted = !this._wpEngaged;
    }

    if (predicted && this.O4(o4Index)[2] != 0) {
      if (this.DecodeOrder4(o4Index, row, out var found)) {
        var slot = this._o4Slot;
        var update = slot < 12 && this.O4(o4Index)[slot + 1] < 7;
        symbol = found;
        written = this.EmitLiteral(output, written, (byte)found, light: true, clearSlot: false, decay: false, slot, update, order2First: true);
        this._ringArmed = false;
        return written;
      }

      this._o4Slot = 12;
      if (this._kind == 0)
        this._order2Update = true;
      this._o2Slot = 0xFF;
      predicted = false;
    }

    if (predicted) {
      if (this.DecodeOrder4Top(o4Index, out var top)) {
        symbol = top;
        written = this.EmitLiteral(output, written, (byte)top, light: true, clearSlot: true, decay: false, 0, this.O4(o4Index)[1] < 7, order2First: true);
        this._ringArmed = false;
        return written;
      }

      this._o4Total = this.Order4Total(o4Index);
      this._o4Slot = 12;
      this._o2Slot = 0xFF;
      this.Exclude(this.O4(o4Index)[0xD]);
      this._o2Total = this.O2(this._o2Index)[0];
    }

    if (this._kind != 0) {
      this._rankSmall[this._rankSmallPointer] = this._rankAfter;
      gate = this.StrideGate();
      this._sinceMiss >>= 3;
      if (this._ringReady)
        this._ringCredit += 2;
    }

    var ringAttempted = false;
    var ringOutcome = Outcome.NotDecoded;
    if (this._ringWanted && this._kind != 0) {
      ringAttempted = true;
      ringOutcome = this.TryRing(output, ref written, out symbol);
      if (ringOutcome == Outcome.Decoded)
        return written;
    }

    var skipOrder2 = ringAttempted && ringOutcome == Outcome.NotDecoded && !this._ringEligible;
    if (ringAttempted && this._ringEligible && !this._ringRecheck)
      skipOrder2 = true;
    if (ringAttempted && this._ringEligible && this._ringRecheck) {
      this._order2Update = this._o2Novelty == 0 || this.O2(this._o2Index)[0x11] == 0
        || (this._rankStep < 0xFFFF && this._rankSmall[this._rankSmallPointer] <= 0x3FE);
      skipOrder2 = !this._order2Update || this.O2(this._o2Index)[0] == 0;
    }

    var order2GateFailed = false;
    if (!skipOrder2 && this.O2(this._o2Index)[0] != 0) {
      var o2 = this.O2(this._o2Index);
      var singleExcluded = o2[2] == 0 && this.ExcludedList.Contains(o2[9]);
      Span<byte> copy = stackalloc byte[Order2Stride];
      o2.CopyTo(copy);
      Span<bool> active = stackalloc bool[8];
      var total = this.Order2Total(copy, active, out _);
      this._o2Total = total;
      this._o2Slot = 0xFF;
      var order2Gate = (total << 2) > copy[0] * 5u && ((uint)copy[0x11] << 2) + this._o2Novelty < 0x10;
      if (order2Gate && this._kind != 0) {
        this._o2Charge = this._wpArmed;
        order2Gate = !this._wpEngaged;
      }

      order2GateFailed = !order2Gate;
      if (o2[2] != 0) {
        if (order2Gate) {
          var hint = this.Order2Hint();
          if (this.DecodeOrder2(this._o2Index, hint, out var found)) {
            var slot = this._o2Slot;
            if (slot < 8 && (this.O2(this._o2Index)[slot + 1] << 2) >= this._o2Total)
              this._bonus = this._o2Total > 0x10 ? 2 : 1;
            symbol = found;
            written = this.EmitAfterOrder2(output, written, (byte)found);
            if (!this._ringTried)
              this._ringArmed = false;
            return written;
          }

          if (this._kind != 0)
            for (var slot = 0; slot < 8 && this.O2(this._o2Index)[slot + 1] != 0; ++slot)
              if (!this.ExcludedList.Contains(this.O2(this._o2Index)[9 + slot]))
                this.Exclude(this.O2(this._o2Index)[9 + slot]);
          this._order2Update = true;
          if (this.TryRing(output, ref written, out symbol) == Outcome.Decoded)
            return written;
        }
      } else if (!singleExcluded) {
        if (order2Gate) {
          if (this.DecodeOrder2Single(this._o2Index, out _)) {
            this._bonus = 1;
            this._o2Slot = 0;
            this._order2Update = true;
            var only = this.O2(this._o2Index)[9];
            symbol = only;
            written = this.EmitLiteral(output, written, only, light: false, clearSlot: false, decay: this._kind == 0, this._o4Slot, true, order2First: false);
            if (!this._ringTried)
              this._ringArmed = false;
            return written;
          }

          this.Exclude(this.O2(this._o2Index)[9]);
          this._o2Slot = 8;
          if (this.TryRing(output, ref written, out symbol) == Outcome.Decoded)
            return written;
        }
      } else {
        this._order2Update = true;
        this._o2Slot = 0xFF;
      }
    }

    if (order2GateFailed && this.TryRing(output, ref written, out symbol) == Outcome.Decoded)
      return written;

    return this.DecodeFromContexts(output, written, row, gate, skipOrder2, out symbol);
  }

  private int Order2Hint() {
    var predicted = this._predicted[this.LiteralRow()];
    var o2 = this.O2(this._o2Index);
    for (var slot = 0; slot < 8; ++slot) {
      if (o2[9 + slot] == predicted)
        return slot;
      if (slot >= 7 || o2[2 + slot] == 0)
        break;
    }

    return 8;
  }

  private bool Order4InlineGate(uint index) {
    if (this._ringCredit != 0 || this._stride <= 1 || this._statHits > this._statTotal || this._rankSmall[this._rankSmallPointer] <= 0x3FE)
      return true;
    var record = this.O4(index);
    return (uint)(record[0x1A] >> 4) * ((uint)record[0x1B] << 6) >= this._rankLarge[this._rankLargePointer];
  }

  private bool Order4LateGate(uint index, int o2Index) {
    var r4 = this.O4(index);
    var r2 = this.O2(o2Index);
    if ((this._kind != 0 || r2[0x13] >= 0x30) && (r4[0x19] & 0x38) == 0) {
      var left = ((uint)(r4[0x19] & 4) + r4[1] + (uint)(r4[0x1A] >> 4)) * r2[0x12];
      var right = (r2[1] + (uint)(r2[0x13] >> 4)) * this._o4Total;
      if (left < right)
        return false;
    }

    return !(r2[0x13] >= 0x60 && r2[0x13] >> 2 > r4[0x1A]);
  }

  private bool Order4FrequencyGate(uint index) {
    var record = this.O4(index);
    var total = this._o4Total;
    if (this._excludedCount != 0)
      for (var k = 0; k < 12; ++k) {
        if (record[k + 0xD] == this._excluded[0]) {
          total -= record[k + 1];
          break;
        }

        if (record[k + 2] == 0)
          break;
      }

    this._o4Rest = total;
    return record[0] < total;
  }

  // ── The binary predictor's turn ────────────────────────────────────────

  private Outcome TryRing(byte[] output, ref int written, out int symbol) {
    symbol = -1;
    if (this._ringTried || this._kind == 0)
      return Outcome.NotDecoded;

    this._ringArmed = true;
    this._ringTried = true;
    if (!this._ringEligible)
      return Outcome.NotDecoded;

    this._ringCredit >>= 2;
    var guess = this._ringBest.Predicted;
    if (this.ExcludedList.Contains(guess)) {
      if (this._ringBest.Misses > 3 || this._ringBest.Hits >> 6 > this._ringBest.Misses)
        this._ringArmed = false;
      return Outcome.NotDecoded;
    }

    this._ringRan = this._kind != 0 && this.LinearPredictorGate() && this.ClassSelected();
    if (this._ringRan && this._wpA < this._wpB)
      return Outcome.NotDecoded;

    var o2Index = this._o2Index;
    var contextByte = (int)(this._contextWord & 0xFF);
    var o2 = this.O2(o2Index);
    var marker = o2[0x13];
    Span<uint> weights = [
      this._ringOrder4Weight,
      (uint)UharcPpmTables.Order2Weight[(marker >> 4) & 0xF],
      (uint)UharcPpmTables.ConfidenceWeight[this._ringConfidence[contextByte] & 0xF],
    ];
    Span<byte> guesses = [this._ringOrder4Guess, o2[9 + (marker & 0xF)], this._ringPredicted[contextByte]];

    if (this.DecodeRing(weights, guesses)) {
      if (this._rankBoost) {
        this._rankSmall[this._rankSmallPointer] += this._rankStep;
        this._rankLarge[this._rankLargePointer] += this._rankStep;
        this._rankStep = Math.Min(this._rankStep * 2, 0xFFFFu);
      }

      symbol = guess;
      this._o2Index = o2Index;
      this._excludedCount = 0;
      this._wpKernel = false;
      written = this.EmitLiteral(output, written, guess, light: false, clearSlot: false, decay: false, this._o4Slot, this._order2Update, order2First: false);
      return Outcome.Decoded;
    }

    if (this._rankBoost) {
      this._rankSmall[this._rankSmallPointer] >>= 1;
      this._rankLarge[this._rankLargePointer] >>= 1;
      this._rankStep = (this._rankStep >> 1) + 0xFF;
    }

    this.Exclude(guess);
    return Outcome.Escaped;
  }

  // ── Emitting a literal ─────────────────────────────────────────────────

  private int EmitLiteral(byte[] output, int written, byte value, bool light, bool clearSlot, bool decay, int slot, bool order2Update, bool order2First) {
    this.Store(value);
    written = this.Emit(output, written, value);
    this.CountPrediction(this.LiteralRow(), value);
    if (decay)
      this._sinceMiss >>= 3;
    if (clearSlot)
      this._o4Slot = 0;
    if (order2Update && order2First)
      this.UpdateOrder2(value, light);
    this.UpdateOrder4(value, this._o4Index, Math.Max(slot, 0));
    if (order2Update && !order2First)
      this.UpdateOrder2(value, light);
    this.PrepareClassUpdate(value);
    this.AfterSymbol(value, true);
    this.Advance();
    return written;
  }

  private int EmitAfterOrder2(byte[] output, int written, byte value) {
    this._order2Update = true;
    this.UpdateOrder4(value, this._o4Index, this._o4Slot);
    this.UpdateOrder2(value, light: false);
    this.PrepareClassUpdate(value);
    written = this.Emit(output, written, value);
    this.CountPrediction(this.LiteralRow(), value);
    if (this._kind == 0)
      this._sinceMiss >>= 3;
    this.AfterSymbol(value, true);
    this.Store(value);
    this.Advance();
    return written;
  }

  /// <summary>Pre-computes what the linear predictor's context would have charged for this byte.</summary>
  private void PrepareClassUpdate(byte value) {
    if (this._kind == 0 || !this._wpArmed)
      return;

    var delta = this._side.ToDifference(value);
    var savedCount = this._excludedCount;
    var savedFallback = this._fallbackRow;
    this._excludedCount = 0;
    this._fallbackRow = 0x304 + this._selector;
    var estimate = this.EstimateCost(this.ClassContext, delta);
    this.Apply(estimate);
    this._wpAdd = estimate.Cost;
    this._wpPending = true;
    this._excludedCount = savedCount;
    this._fallbackRow = savedFallback;
  }
}
